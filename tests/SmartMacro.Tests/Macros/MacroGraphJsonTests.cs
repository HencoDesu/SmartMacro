using System.Text.Json;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: MacroGraphJson is THE dialect for macro files — every node type, trigger type,
// selector, and editor coordinate must survive a round trip, and malformed input must
// fail with a clean JsonException (storage surfaces it as "file is broken", not a crash).
public class MacroGraphJsonTests
{
    private static MacroGraph BuildFullGraph() => new()
    {
        Name = "полный",
        Triggers =
        [
            new HotkeyTrigger(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F5),
            new HotkeyTrigger(HotkeyModifiers.None, default, MouseButton.XButton1),
            new ProcessAppearedTrigger("elementclient"),
        ],
        StartNodeId = "key",
        Nodes =
        [
            new KeyPressNode
            {
                Id = "key",
                Key = VirtualKey.F8,
                Target = new TargetSelector { RequireTags = ["перс"], ExcludeTags = ["МАСТЕР"] },
                Next = "clickLiteral",
                Editor = new NodeEditorInfo(10.5, -20.25),
            },
            new ClickNode { Id = "clickLiteral", Point = new ScreenPoint(100, 200), DoubleClick = true, Next = "clickVar" },
            new ClickNode { Id = "clickVar", PointVar = "cursor", Next = "delay" },
            new DelayNode { Id = "delay", Ms = 250, Next = "addTag" },
            new AddTagNode { Id = "addTag", Tag = "класс-{tag}", Next = "removeTag" },
            new RemoveTagNode
            {
                Id = "removeTag",
                Tag = "боевой",
                Target = new TargetSelector { RequireTags = ["перс"] },
                Next = "icon",
            },
            new SetIconNode { Id = "icon", IconPath = "icons/{tag}.png", Next = "run" },
            new RunMacroNode
            {
                Id = "run",
                MacroName = "под-макрос",
                Target = new TargetSelector { ExcludeTags = ["МАСТЕР"] },
                Await = false,
                Next = "find",
            },
            new FindElementNode
            {
                Id = "find",
                Template = "кнопка",
                Region = new ScreenRect(1, 2, 3, 4),
                FoundPointVar = "btn",
                Found = "wait",
                NotFound = null,
            },
            new WaitForElementNode
            {
                Id = "wait",
                Template = "мир",
                TimeoutMs = 60000,
                FoundPointVar = "pt",
                Found = "recognize",
                Timeout = null,
            },
            new RecognizeTagNode
            {
                Id = "recognize",
                TemplateSet = "классы",
                Region = new ScreenRect(5, 6, 7, 8),
                ApplyTag = false,
                ResultVar = "класс",
                Matched = null,
                NotMatched = null,
            },
        ],
    };

    [Test]
    public async Task RoundTrip_PreservesEveryNodeTriggerAndEditorCoordinate()
    {
        var original = BuildFullGraph();

        var json = MacroGraphJson.Serialize(original);
        var reloaded = MacroGraphJson.Deserialize(json);

        // Full-fidelity check: a second serialization of the reloaded graph must be
        // byte-identical (records with List members don't value-compare, so the JSON
        // form IS the equality witness).
        await Assert.That(MacroGraphJson.Serialize(reloaded)).IsEqualTo(json);

        // Spot checks on the typed model.
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

        // Enums serialize as names, not numbers.
        await Assert.That(json).Contains("\"F8\"");
        await Assert.That(json).Contains("Control, Shift");
        await Assert.That(json).Contains("XButton1");
    }

    [Test]
    public async Task Deserialize_AppliesDocumentedDefaults()
    {
        // Await, ApplyTag, ResultVar, Triggers omitted — defaults must kick in.
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
