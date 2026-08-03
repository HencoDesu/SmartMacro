using System.Text.Json;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Macros;

namespace SmartMacro.Tests.Contracts;

// Самый ценный тест стадии 1: MacroGraphJson и IpcJson — это два объекта настроек над одной и
// той же моделью, и отказ, ради предотвращения которого они и существуют, — это граф, который
// прекрасно сохраняется на диск, но теряет дискриминаторы $type (или свои строковые
// перечисления) по дороге через трубу. Всё здесь идёт через нагрузку IpcRequest, а не через
// голую сериализацию.
public class IpcGraphPayloadTests
{
    private static MacroGraph RoundTripThroughPayload(MacroGraph graph)
    {
        var request = new IpcRequest(1, IpcMessageTypes.SaveMacro, IpcJson.Write(new SaveMacroRequest(graph)));

        // Полный круг: объект → строка → объект, ровно так, как это сделает труба.
        var line = JsonSerializer.Serialize(request, IpcJson.Options);
        var reloaded = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options)!;

        return IpcJson.Read<SaveMacroRequest>(reloaded.Payload)!.Macro;
    }

    [Test]
    public async Task EveryNodeAndTriggerType_SurvivesAnIpcPayload()
    {
        var original = FullMacroGraphFixture.Build();

        var reloaded = RoundTripThroughPayload(original);

        // Эталон — файловый диалект: если поездка по проводу прошла без потерь, то повторная
        // сериализация перечитанного графа через MacroGraphJson воспроизводит исходный файл
        // байт в байт. (Записи, держащие List, не сравниваются по значению, поэтому
        // свидетелем равенства выступает JSON.)
        await Assert.That(MacroGraphJson.Serialize(reloaded))
            .IsEqualTo(MacroGraphJson.Serialize(original));

        await Assert.That(reloaded.Nodes).Count().IsEqualTo(FullMacroGraphFixture.NodeCount);
        await Assert.That(reloaded.Triggers).Count().IsEqualTo(FullMacroGraphFixture.TriggerCount);
    }

    [Test]
    public async Task Polymorphism_IsPreserved_NotFlattenedToTheBaseType()
    {
        var reloaded = RoundTripThroughPayload(FullMacroGraphFixture.Build());

        // Конкретные типы, а не заглушки MacroNode/MacroTrigger.
        await Assert.That(reloaded.Nodes.Select(node => node.GetType().Name).Distinct().Count())
            .IsEqualTo(10);
        await Assert.That(reloaded.Nodes[FullMacroGraphFixture.KeyPressIndex]).IsTypeOf<KeyPressNode>();
        await Assert.That(reloaded.Nodes[FullMacroGraphFixture.RecognizeIndex]).IsTypeOf<RecognizeTagNode>();
        await Assert.That(reloaded.Triggers[0]).IsTypeOf<HotkeyTrigger>();
        await Assert.That(reloaded.Triggers[2]).IsTypeOf<ProcessAppearedTrigger>();
    }

    [Test]
    public async Task TypedFields_SurviveTheWire()
    {
        var reloaded = RoundTripThroughPayload(FullMacroGraphFixture.Build());

        var key = (KeyPressNode)reloaded.Nodes[FullMacroGraphFixture.KeyPressIndex];
        await Assert.That(key.Key).IsEqualTo(VirtualKey.F8);
        await Assert.That(key.Editor).IsEqualTo(new NodeEditorInfo(10.5, -20.25));
        await Assert.That(key.Target!.ExcludeTags[0]).IsEqualTo("МАСТЕР");

        var click = (ClickNode)reloaded.Nodes[FullMacroGraphFixture.ClickLiteralIndex];
        await Assert.That(click.Point).IsEqualTo(new ScreenPoint(100, 200));
        await Assert.That(((ClickNode)reloaded.Nodes[FullMacroGraphFixture.ClickVarIndex]).PointVar)
            .IsEqualTo("cursor");

        await Assert.That(((RunMacroNode)reloaded.Nodes[FullMacroGraphFixture.RunMacroIndex]).Await).IsFalse();
        await Assert.That(((FindElementNode)reloaded.Nodes[FullMacroGraphFixture.FindIndex]).Region)
            .IsEqualTo(new ScreenRect(1, 2, 3, 4));

        var hotkey = (HotkeyTrigger)reloaded.Triggers[0];
        await Assert.That(hotkey.Modifiers).IsEqualTo(HotkeyModifiers.Control | HotkeyModifiers.Shift);
        await Assert.That(((HotkeyTrigger)reloaded.Triggers[1]).MouseButton).IsEqualTo(MouseButton.XButton1);
    }

    [Test]
    public async Task WireDialect_MatchesTheFileDialect_ForDiscriminatorsAndEnums()
    {
        var line = JsonSerializer.Serialize(
            new IpcRequest(1, IpcMessageTypes.SaveMacro,
                IpcJson.Write(new SaveMacroRequest(FullMacroGraphFixture.Build()))),
            IpcJson.Options);

        foreach (var discriminator in new[]
                 {
                     "keyPress", "click", "delay", "addTag", "removeTag", "setIcon",
                     "runMacro", "findElement", "waitForElement", "recognizeTag",
                     "hotkey", "process",
                 })
        {
            // Без отступов, а значит, и без пробела после двоеточия — вот эта разница и есть
            // всё расхождение диалектов.
            await Assert.That(line).Contains($"\"$type\":\"{discriminator}\"");
        }

        await Assert.That(line).Contains("\"F8\"");
        await Assert.That(line).Contains("Control, Shift");
    }

    [Test]
    public async Task MacroLibraryResponse_RoundTripsAsAnArrayOfGraphs()
    {
        // GetMacros отвечает массивом MacroGraph[] — тот же полиморфизм, только уровнем глубже.
        var library = new[] { FullMacroGraphFixture.Build(), FullMacroGraphFixture.Build("второй") };
        var response = new IpcResponse(1, Ok: true, IpcJson.Write(library));

        var line = JsonSerializer.Serialize(response, IpcJson.Options);
        var reloaded = IpcJson.Read<MacroGraph[]>(
            JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)!.Payload)!;

        await Assert.That(reloaded).Count().IsEqualTo(2);
        await Assert.That(reloaded[1].Name).IsEqualTo("второй");
        await Assert.That(reloaded[0].Nodes).Count().IsEqualTo(FullMacroGraphFixture.NodeCount);
        await Assert.That(reloaded[1].Nodes[FullMacroGraphFixture.RecognizeIndex]).IsTypeOf<RecognizeTagNode>();
    }
}
