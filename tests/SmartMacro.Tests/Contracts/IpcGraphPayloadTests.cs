using System.Text.Json;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Macros;

namespace SmartMacro.Tests.Contracts;

// Stage 1's highest-value test: MacroGraphJson and IpcJson are two options objects over
// the same model, and the failure they exist to prevent is a graph that saves to disk
// fine but loses its $type discriminators (or its string enums) on the way across the
// pipe. Everything here goes through an IpcRequest payload, not a bare serialize.
public class IpcGraphPayloadTests
{
    private static MacroGraph RoundTripThroughPayload(MacroGraph graph)
    {
        var request = new IpcRequest(1, IpcMessageTypes.SaveMacro, IpcJson.Write(new SaveMacroRequest(graph)));

        // Full trip: object → line → object, the way the pipe will do it.
        var line = JsonSerializer.Serialize(request, IpcJson.Options);
        var reloaded = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options)!;

        return IpcJson.Read<SaveMacroRequest>(reloaded.Payload)!.Macro;
    }

    [Test]
    public async Task EveryNodeAndTriggerType_SurvivesAnIpcPayload()
    {
        var original = FullMacroGraphFixture.Build();

        var reloaded = RoundTripThroughPayload(original);

        // The file dialect is the reference: if the wire trip is lossless, re-serializing
        // the reloaded graph with MacroGraphJson reproduces the original file byte for
        // byte. (Records holding Lists don't value-compare, so JSON is the equality witness.)
        await Assert.That(MacroGraphJson.Serialize(reloaded))
            .IsEqualTo(MacroGraphJson.Serialize(original));

        await Assert.That(reloaded.Nodes).Count().IsEqualTo(FullMacroGraphFixture.NodeCount);
        await Assert.That(reloaded.Triggers).Count().IsEqualTo(FullMacroGraphFixture.TriggerCount);
    }

    [Test]
    public async Task Polymorphism_IsPreserved_NotFlattenedToTheBaseType()
    {
        var reloaded = RoundTripThroughPayload(FullMacroGraphFixture.Build());

        // Concrete types, not MacroNode/MacroTrigger placeholders.
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
        await Assert.That(((ClickNode)reloaded.Nodes[FullMacroGraphFixture.ClickVarIndex]).PointVar).IsEqualTo("cursor");

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
            new IpcRequest(1, IpcMessageTypes.SaveMacro, IpcJson.Write(new SaveMacroRequest(FullMacroGraphFixture.Build()))),
            IpcJson.Options);

        foreach (var discriminator in new[]
                 {
                     "keyPress", "click", "delay", "addTag", "removeTag", "setIcon",
                     "runMacro", "findElement", "waitForElement", "recognizeTag",
                     "hotkey", "process",
                 })
        {
            // Unindented, so no space after the colon — that difference IS the dialect delta.
            await Assert.That(line).Contains($"\"$type\":\"{discriminator}\"");
        }

        await Assert.That(line).Contains("\"F8\"");
        await Assert.That(line).Contains("Control, Shift");
    }

    [Test]
    public async Task MacroLibraryResponse_RoundTripsAsAnArrayOfGraphs()
    {
        // GetMacros answers with MacroGraph[] — the same polymorphism, one level deeper.
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
