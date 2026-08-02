using System.Text.Json;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.ViewModels;

// W0.3: the graph ↔ editor-row mapping. This is the highest-stakes code in the wave —
// every save runs a user's graph through NodeRowViewModel.FromNode/ToNode, so a field
// dropped here is silent data loss on a file the user has been hand-tuning for months.
//
// Equality is compared on the canonical JSON rather than on record equality: the model's
// records hold List<string> (TargetSelector tags), whose default equality is by reference,
// so `==` would pass on graphs that differ. The JSON is also what actually lands on disk.
public class NodeRowRoundTripTests
{
    /// <summary>Round-trips one node through the editor rows and returns both JSON forms.</summary>
    private static (string Before, string After) RoundTrip(MacroNode node)
    {
        var row = NodeRowViewModel.FromNode(node);
        var result = row.ToNode();
        return (
            JsonSerializer.Serialize(node, MacroGraphJson.Options),
            JsonSerializer.Serialize(result, MacroGraphJson.Options));
    }

    /// <summary>A graph exercising every node type, both selector shapes, and canvas coordinates.</summary>
    public static MacroGraph EveryNodeType() => new()
    {
        Name = "полный-граф",
        Triggers =
        [
            new HotkeyTrigger(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F19),
            new HotkeyTrigger(HotkeyModifiers.None, default, MouseButton.XButton1),
            new ProcessAppearedTrigger("elementclient_64"),
        ],
        StartNodeId = "key",
        Nodes =
        [
            new KeyPressNode
            {
                Id = "key",
                Key = VirtualKey.F8,
                Target = new TargetSelector { RequireTags = ["Лучник", "в бою"], ExcludeTags = ["Шаман"] },
                Next = "click",
                Editor = new NodeEditorInfo(12.5, -40),
            },
            // Selector present but empty = "every registered window", which is NOT the
            // same as a null selector; the editor's UseSelector flag is what keeps the two
            // apart across a round-trip.
            new ClickNode
            {
                Id = "click",
                Point = new ScreenPoint(285, 456),
                DoubleClick = true,
                Target = new TargetSelector(),
                Next = "click-var",
            },
            new ClickNode { Id = "click-var", PointVar = "cursor", Next = "delay" },
            new DelayNode { Id = "delay", Ms = 1500, Next = "add" },
            new AddTagNode
            {
                Id = "add",
                Tag = "Лучник",
                Target = new TargetSelector { ExcludeTags = ["Шаман"] },
                Next = "remove",
            },
            new RemoveTagNode { Id = "remove", Tag = "{tag}", Next = "icon" },
            new SetIconNode
            {
                Id = "icon",
                IconPath = "Assets/ClassIcons/{tag}.png",
                Target = new TargetSelector { RequireTags = ["Жрец"] },
                Next = "run",
            },
            new RunMacroNode
            {
                Id = "run",
                MacroName = "pw-identify-one",
                Await = false,
                Target = new TargetSelector { ExcludeTags = ["Шаман"] },
                Next = "find",
            },
            new FindElementNode
            {
                Id = "find",
                Template = "ServerSelectButton",
                Region = new ScreenRect(10, 20, 300, 40),
                FoundPointVar = "btn",
                Found = "wait",
                NotFound = null,
            },
            // Region deliberately null — the "search the whole window" shape.
            new WaitForElementNode
            {
                Id = "wait",
                Template = "ChatPanelButtons",
                TimeoutMs = 60_000,
                Found = "recognize",
                Timeout = null,
            },
            new RecognizeTagNode
            {
                Id = "recognize",
                TemplateSet = "classes",
                Region = new ScreenRect(3200, 1060, 160, 35),
                ApplyTag = false,
                ResultVar = "cls",
                Matched = null,
                NotMatched = "key",
            },
        ],
    };

    [Test]
    public async Task EveryNodeType_SurvivesFromNodeThenToNode()
    {
        foreach (var node in EveryNodeType().Nodes)
        {
            var (before, after) = RoundTrip(node);
            await Assert.That(after).IsEqualTo(before);
        }
    }

    [Test]
    public async Task NullSelector_And_EmptySelector_StayDistinct()
    {
        var contextWindow = new KeyPressNode { Id = "n", Key = VirtualKey.A, Target = null };
        var everyWindow = new KeyPressNode { Id = "n", Key = VirtualKey.A, Target = new TargetSelector() };

        var (_, contextAfter) = RoundTrip(contextWindow);
        var (_, everyAfter) = RoundTrip(everyWindow);

        await Assert.That(contextAfter).IsNotEqualTo(everyAfter);
        await Assert.That(((KeyPressNode)NodeRowViewModel.FromNode(contextWindow).ToNode()).Target).IsNull();
        await Assert.That(((KeyPressNode)NodeRowViewModel.FromNode(everyWindow).ToNode()).Target).IsNotNull();
    }

    [Test]
    public async Task CanvasCoordinates_AreCarriedThrough_EvenThoughTheRowsEditorNeverShowsThem()
    {
        var node = new DelayNode { Id = "d", Ms = 250, Editor = new NodeEditorInfo(700, 42) };

        var result = (DelayNode)NodeRowViewModel.FromNode(node).ToNode();

        await Assert.That(result.Editor).IsEqualTo(new NodeEditorInfo(700, 42));
    }

    [Test]
    public async Task ConditionalsAndDelay_HaveNoTargetSelector_EverythingElseDoes()
    {
        var graph = EveryNodeType();
        foreach (var node in graph.Nodes)
        {
            var row = NodeRowViewModel.FromNode(node);
            var expected = node is not (DelayNode or FindElementNode or WaitForElementNode or RecognizeTagNode);
            await Assert.That(row.HasTarget).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task EdgeLabels_MatchTheNodeFamily()
    {
        var find = NodeRowViewModel.FromNode(new FindElementNode { Id = "f", Template = "t" });
        var wait = NodeRowViewModel.FromNode(new WaitForElementNode { Id = "w", Template = "t", TimeoutMs = 1 });
        var key = NodeRowViewModel.FromNode(new KeyPressNode { Id = "k", Key = VirtualKey.A });

        await Assert.That(find.Edges.Select(e => e.Label)).IsEquivalentTo(new[] { "Найдено", "Не найдено" });
        await Assert.That(wait.Edges.Select(e => e.Label)).IsEquivalentTo(new[] { "Найдено", "Таймаут" });
        await Assert.That(key.Edges).Count().IsEqualTo(1);
    }

    // ---- Delay: seconds ↔ milliseconds ----------------------------------------------

    [Test]
    [Arguments(1500, "1.5")]
    [Arguments(2000, "2")]
    [Arguments(200, "0.2")]
    [Arguments(0, "0")]
    public async Task Delay_MsRendersAsSeconds(int ms, string expected)
    {
        var row = (DelayNodeRowViewModel)NodeRowViewModel.FromNode(new DelayNode { Id = "d", Ms = ms });

        await Assert.That(row.SecondsText).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1.5", 1500)]
    [Arguments("2", 2000)]
    // A Russian numpad emits a comma; invariant parsing would read "1,5" as 15 seconds.
    [Arguments("1,5", 1500)]
    [Arguments("0", 0)]
    [Arguments("", 0)]
    public async Task Delay_SecondsParseBackToMs(string text, int expected)
    {
        var row = (DelayNodeRowViewModel)NodeRowViewModel.FromNode(new DelayNode { Id = "d", Ms = 0 });
        row.SecondsText = text;

        await Assert.That(((DelayNode)row.ToNode()).Ms).IsEqualTo(expected);
        await Assert.That(row.GetInputErrors()).IsEmpty();
    }

    [Test]
    [Arguments("abc")]
    [Arguments("-1")]
    public async Task Delay_RejectsNonsense(string text)
    {
        var row = (DelayNodeRowViewModel)NodeRowViewModel.FromNode(new DelayNode { Id = "пауза", Ms = 0 });
        row.SecondsText = text;

        await Assert.That(row.GetInputErrors()).IsNotEmpty();
    }

    // ---- Click: exactly one of Point / PointVar ---------------------------------------

    [Test]
    public async Task Click_LiteralPoint_LeavesPointVarUnset()
    {
        var row = (ClickNodeRowViewModel)NodeRowViewModel.FromNode(
            new ClickNode { Id = "c", PointVar = "cursor" });

        row.UseVariable = false;
        row.XText = "640";
        row.YText = "480";
        var node = (ClickNode)row.ToNode();

        await Assert.That(node.PointVar).IsNull();
        await Assert.That(node.Point).IsEqualTo(new ScreenPoint(640, 480));
        await Assert.That(row.UseLiteralPoint).IsTrue();
    }

    [Test]
    public async Task Click_Variable_LeavesPointUnset()
    {
        var row = (ClickNodeRowViewModel)NodeRowViewModel.FromNode(
            new ClickNode { Id = "c", Point = new ScreenPoint(1, 2) });

        row.UseVariable = true;
        row.PointVar = "btn";
        var node = (ClickNode)row.ToNode();

        await Assert.That(node.Point).IsNull();
        await Assert.That(node.PointVar).IsEqualTo("btn");
        await Assert.That(row.UseLiteralPoint).IsFalse();
    }

    [Test]
    public async Task Click_ReportsUnparseableCoordinates()
    {
        var row = (ClickNodeRowViewModel)NodeRowViewModel.FromNode(
            new ClickNode { Id = "клик", Point = default(ScreenPoint) });
        row.XText = "сто";

        await Assert.That(row.GetInputErrors()).IsNotEmpty();
    }

    // ---- regions ----------------------------------------------------------------------

    [Test]
    public async Task Region_ZeroSize_MeansWholeWindow()
    {
        var row = (FindElementNodeRowViewModel)NodeRowViewModel.FromNode(
            new FindElementNode { Id = "f", Template = "t", Region = new ScreenRect(5, 6, 7, 8) });

        row.Region.WidthText = "0";

        await Assert.That(((FindElementNode)row.ToNode()).Region).IsNull();
    }

    [Test]
    public async Task Region_IsMandatoryForRecognize_AndKeepsZeroes()
    {
        var row = (RecognizeTagNodeRowViewModel)NodeRowViewModel.FromNode(
            new RecognizeTagNode { Id = "r", TemplateSet = "classes", Region = default });

        await Assert.That(((RecognizeTagNode)row.ToNode()).Region).IsEqualTo(default(ScreenRect));
    }

    // ---- triggers -----------------------------------------------------------------------

    [Test]
    public async Task Triggers_RoundTrip()
    {
        foreach (var trigger in EveryNodeType().Triggers)
        {
            var result = TriggerRowViewModel.FromTrigger(trigger).ToTrigger();
            await Assert.That(result).IsEqualTo(trigger);
        }
    }

    [Test]
    public async Task UnboundHotkey_IsReportedAsAnInputError()
    {
        var row = TriggerRowViewModel.Create(MacroTriggerKind.Hotkey);

        await Assert.That(row.GetInputErrors()).IsNotEmpty();

        ((HotkeyTriggerRowViewModel)row).KeyName = "F13";

        await Assert.That(row.GetInputErrors()).IsEmpty();
    }

    [Test]
    public async Task EveryKind_CanBeCreatedFromTheAddMenu()
    {
        await Assert.That(NodeRowViewModel.Kinds).Count().IsEqualTo(Enum.GetValues<MacroNodeKind>().Length);

        foreach (var option in NodeRowViewModel.Kinds)
        {
            var row = NodeRowViewModel.Create(option.Kind, "n1");
            await Assert.That(row.NodeId).IsEqualTo("n1");
            // A freshly created row must be serialisable straight away, or "add node" would
            // put the graph into a state that cannot even be written to disk.
            await Assert.That(JsonSerializer.Serialize(row.ToNode(), MacroGraphJson.Options)).IsNotEmpty();
        }
    }
}
