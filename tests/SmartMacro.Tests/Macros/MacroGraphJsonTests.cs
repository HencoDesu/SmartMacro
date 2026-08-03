using System.Text.Json;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: MacroGraphJson — ЭТО диалект файлов макросов: каждый тип ноды, каждый тип триггера,
// селекторы и координаты редактора обязаны пережить round trip, а испорченный ввод обязан падать
// чистым JsonException (хранилище показывает это как «файл битый», а не как крах).
public class MacroGraphJsonTests
{
    // Общий с тестами диалекта IPC — см. FullMacroGraphFixture.
    private static MacroGraph BuildFullGraph() => FullMacroGraphFixture.Build();

    [Test]
    public async Task RoundTrip_PreservesEveryNodeTriggerAndEditorCoordinate()
    {
        var original = BuildFullGraph();

        var json = MacroGraphJson.Serialize(original);
        var reloaded = MacroGraphJson.Deserialize(json);

        // Проверка на полное соответствие: повторная сериализация перечитанного графа обязана
        // совпасть байт в байт (записи с полями-List не сравниваются по значению, так что
        // свидетелем равенства выступает форма JSON).
        await Assert.That(MacroGraphJson.Serialize(reloaded)).IsEqualTo(json);

        // Выборочные проверки по типизированной модели.
        await Assert.That(reloaded.Name).IsEqualTo("полный");
        await Assert.That(reloaded.StartNodeId).IsEqualTo("key");
        await Assert.That(reloaded.Triggers).Count().IsEqualTo(3);
        await Assert.That(reloaded.Nodes).Count().IsEqualTo(11);

        var hotkey = (HotkeyTrigger)reloaded.Triggers[0];
        await Assert.That(hotkey.Modifiers).IsEqualTo(HotkeyModifiers.Control | HotkeyModifiers.Shift);
        await Assert.That(hotkey.Key).IsEqualTo(VirtualKey.F5);
        var mouse = (HotkeyTrigger)reloaded.Triggers[1];
        await Assert.That(mouse.IsMouse).IsTrue();
        await Assert.That(((ProcessAppearedTrigger)reloaded.Triggers[2]).ProcessName).IsEqualTo("elementclient");

        var key = (KeyPressNode)reloaded.Nodes[0];
        await Assert.That(key.Editor).IsEqualTo(new NodeEditorInfo(10.5, -20.25));
        await Assert.That(key.Target!.RequireTags).Count().IsEqualTo(1);
        await Assert.That(key.Target!.ExcludeTags[0]).IsEqualTo("МАСТЕР");

        var clickLiteral = (ClickNode)reloaded.Nodes[1];
        await Assert.That(clickLiteral.Point).IsEqualTo(new ScreenPoint(100, 200));
        await Assert.That(clickLiteral.DoubleClick).IsTrue();
        var clickVar = (ClickNode)reloaded.Nodes[2];
        await Assert.That(clickVar.Point).IsNull();
        await Assert.That(clickVar.PointVar).IsEqualTo("cursor");

        var run = (RunMacroNode)reloaded.Nodes[7];
        await Assert.That(run.Await).IsFalse();

        var find = (FindElementNode)reloaded.Nodes[8];
        await Assert.That(find.Region).IsEqualTo(new ScreenRect(1, 2, 3, 4));
        await Assert.That(find.NotFound).IsNull();

        var recognize = (RecognizeTagNode)reloaded.Nodes[10];
        await Assert.That(recognize.ApplyTag).IsFalse();
        await Assert.That(recognize.ResultVar).IsEqualTo("класс");
    }

    [Test]
    public async Task Serialized_UsesTypeDiscriminatorsAndStringEnums()
    {
        var json = MacroGraphJson.Serialize(BuildFullGraph());

        foreach (var discriminator in new[]
                 {
                     "keyPress", "click", "delay", "addTag", "removeTag", "setIcon",
                     "runMacro", "findElement", "waitForElement", "recognizeTag",
                     "hotkey", "process",
                 })
        {
            await Assert.That(json).Contains($"\"$type\": \"{discriminator}\"");
        }

        // Перечисления сериализуются именами, а не числами.
        await Assert.That(json).Contains("\"F8\"");
        await Assert.That(json).Contains("Control, Shift");
        await Assert.That(json).Contains("XButton1");
    }

    [Test]
    public async Task Deserialize_AppliesDocumentedDefaults()
    {
        // Await, ApplyTag, ResultVar и Triggers опущены — обязаны сработать значения по умолчанию.
        const string json =
            """
            {
              "Name": "м",
              "StartNodeId": "run",
              "Nodes": [
                { "$type": "runMacro", "Id": "run", "MacroName": "x" },
                { "$type": "recognizeTag", "Id": "rec", "TemplateSet": "классы",
                  "Region": { "X": 0, "Y": 0, "Width": 10, "Height": 10 } }
              ]
            }
            """;

        var graph = MacroGraphJson.Deserialize(json);

        await Assert.That(graph.Triggers).Count().IsEqualTo(0);
        var run = (RunMacroNode)graph.Nodes[0];
        await Assert.That(run.Await).IsTrue();
        await Assert.That(run.Next).IsNull();
        await Assert.That(run.Target).IsNull();
        var recognize = (RecognizeTagNode)graph.Nodes[1];
        await Assert.That(recognize.ApplyTag).IsTrue();
        await Assert.That(recognize.ResultVar).IsEqualTo("tag");
    }

    [Test]
    public async Task UnknownNodeType_ThrowsCleanJsonException()
    {
        const string json =
            """
            {
              "Name": "м",
              "StartNodeId": "a",
              "Nodes": [ { "$type": "teleport", "Id": "a" } ]
            }
            """;

        await Assert.That(() => MacroGraphJson.Deserialize(json)).Throws<JsonException>();
    }

    [Test]
    public async Task UnknownTriggerType_ThrowsCleanJsonException()
    {
        const string json =
            """
            {
              "Name": "м",
              "Triggers": [ { "$type": "voice", "Phrase": "го" } ],
              "StartNodeId": "a",
              "Nodes": []
            }
            """;

        await Assert.That(() => MacroGraphJson.Deserialize(json)).Throws<JsonException>();
    }

    [Test]
    public async Task NullDocument_ThrowsCleanJsonException()
    {
        await Assert.That(() => MacroGraphJson.Deserialize("null")).Throws<JsonException>();
    }
}
