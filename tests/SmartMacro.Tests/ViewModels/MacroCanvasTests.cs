using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D3a: the canvas editor's logic, headless.
//
// Rendering is not testable here and is not the risk. The risk is everything the canvas
// COMPUTES — where a box lands, which way an edge is routed, what counts as an end state,
// which library section a macro falls into — because all of it is invisible in a diff and
// all of it is what a user would notice first.
public class MacroCanvasTests
{
    private static MacroEditorViewModel CreateEditor(FakeIpcClient? client = null) =>
        new(client ?? new FakeIpcClient(), null, null, ImmediateUiDispatcher.Instance, @"C:\smartmacro\macros");

    /// <summary>Same, but hands back the fake daemon so a test can push run events at it.</summary>
    private static MacroEditorViewModel CreateEditor(out FakeIpcClient daemon)
    {
        daemon = new FakeIpcClient();
        return CreateEditor(daemon);
    }

    /// <summary>The shape of pw-boot: a chain with one branch, and no coordinates anywhere.</summary>
    private static MacroGraph BootLike() => new()
    {
        Name = "pw-boot",
        StartNodeId = "wait-server",
        Nodes =
        [
            new WaitForElementNode { Id = "wait-server", Template = "A", TimeoutMs = 1000, Found = "click-server" },
            new ClickNode { Id = "click-server", Point = new ScreenPoint(1, 2), Next = "wait-char" },
            new WaitForElementNode { Id = "wait-char", Template = "B", TimeoutMs = 1000, Found = "click-char" },
            new ClickNode { Id = "click-char", Point = new ScreenPoint(3, 4), Next = "open-stats" },
            new KeyPressNode { Id = "open-stats", Key = VirtualKey.C, Next = "await-stats" },
            new DelayNode { Id = "await-stats", Ms = 500, Next = "recognize" },
            new RecognizeTagNode
            {
                Id = "recognize",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 160, 35),
                Matched = "set-icon",
                NotMatched = "close-stats",
            },
            new SetIconNode { Id = "set-icon", IconPath = "x.png", Next = "close-stats" },
            new KeyPressNode { Id = "close-stats", Key = VirtualKey.C },
        ],
    };

    private static List<NodeRowViewModel> Rows(MacroGraph graph) =>
        [.. graph.Nodes.Select(NodeRowViewModel.FromNode)];

    // ---- layout --------------------------------------------------------------------------

    [Test]
    public async Task AutoLayout_WalksTheGraphDepthFirst_AndWrapsEveryThreeBoxes()
    {
        var rows = Rows(BootLike());

        MacroGraphLayout.Apply(rows, "wait-server");

        // Depth-first from the start, following each node's outcomes in order. That is what
        // keeps a chain in reading order: the mockup's pw-boot is laid out exactly so.
        string[] expected =
        [
            "wait-server", "click-server", "wait-char",
            "click-char", "open-stats", "await-stats",
            "recognize", "set-icon", "close-stats",
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            var row = rows.Single(node => node.NodeId == expected[i]);
            await Assert.That(row.X).IsEqualTo((i % 3) * CanvasMetrics.ColumnPitch);
            await Assert.That(row.Y).IsEqualTo((i / 3) * CanvasMetrics.RowPitch);
        }
    }

    [Test]
    public async Task AutoLayout_PutsUnreachableNodesAfterEverythingItCouldReach()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "с-сиротой",
            StartNodeId = "a",
            Nodes =
            [
                new DelayNode { Id = "orphan", Ms = 1 },
                new DelayNode { Id = "a", Ms = 1, Next = "b" },
                new DelayNode { Id = "b", Ms = 1 },
            ],
        });

        MacroGraphLayout.Apply(rows, "a");

        await Assert.That(rows.Single(r => r.NodeId == "a").X).IsEqualTo(0d);
        await Assert.That(rows.Single(r => r.NodeId == "b").X).IsEqualTo(CanvasMetrics.ColumnPitch);
        await Assert.That(rows.Single(r => r.NodeId == "orphan").X).IsEqualTo(CanvasMetrics.ColumnPitch * 2);
    }

    [Test]
    public async Task EnsurePositions_LaysOutAGraphThatHasNoneAtAll()
    {
        var rows = Rows(BootLike());

        var moved = MacroGraphLayout.EnsurePositions(rows, "wait-server");

        await Assert.That(moved).IsTrue();
        await Assert.That(rows.All(row => row.HasPosition)).IsTrue();
        // Not a pile at the origin: exactly one box may sit there.
        await Assert.That(rows.Count(row => row is { X: 0, Y: 0 })).IsEqualTo(1);
    }

    [Test]
    public async Task EnsurePositions_LeavesAFullyPlacedGraphAlone()
    {
        var rows = Rows(BootLike());
        MacroGraphLayout.Apply(rows, "wait-server");
        var before = rows.Select(row => (row.X, row.Y)).ToList();

        var moved = MacroGraphLayout.EnsurePositions(rows, "wait-server");

        await Assert.That(moved).IsFalse();
        await Assert.That(rows.Select(row => (row.X, row.Y))).IsEquivalentTo(before);
    }

    [Test]
    public async Task EnsurePositions_KeepsHandPlacedBoxes_AndDropsTheStraysUnderneath()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "полу-разложенный",
            StartNodeId = "placed",
            Nodes =
            [
                new DelayNode { Id = "placed", Ms = 1, Next = "stray", Editor = new NodeEditorInfo(600, 300) },
                new DelayNode { Id = "stray", Ms = 1 },
            ],
        });

        MacroGraphLayout.EnsurePositions(rows, "placed");

        var placed = rows.Single(row => row.NodeId == "placed");
        var stray = rows.Single(row => row.NodeId == "stray");
        await Assert.That(placed.X).IsEqualTo(600d);
        await Assert.That(placed.Y).IsEqualTo(300d);
        // Below the lowest existing box, so it cannot land on top of one.
        await Assert.That(stray.Y).IsGreaterThan(placed.Y + placed.LayoutHeight);
    }

    [Test]
    public async Task NextFreeSlot_FillsTheFirstHoleInTheGrid()
    {
        var rows = Rows(BootLike());
        MacroGraphLayout.Apply(rows, "wait-server");
        // Nine nodes = rows 0..2 full; the next box belongs on row 3, column 0.
        var (x, y) = MacroGraphLayout.NextFreeSlot(rows);

        await Assert.That(x).IsEqualTo(0d);
        await Assert.That(y).IsEqualTo(CanvasMetrics.RowPitch * 3);
    }

    // ---- edge routing ----------------------------------------------------------------------

    [Test]
    public async Task Router_DrawsNothingForAnUnwiredOutcome()
    {
        // The mockup's own emphasis: "в конец" must not materialise a node, and it must not
        // materialise a dangling line either.
        var rows = Rows(new MacroGraph
        {
            Name = "конец",
            StartNodeId = "wait",
            Nodes = [new WaitForElementNode { Id = "wait", Template = "A", TimeoutMs = 1, Found = null, Timeout = null }],
        });

        await Assert.That(CanvasEdgeRouter.BuildAll(rows)).IsEmpty();
        await Assert.That(rows[0].Edges.All(edge => edge.IsEnd)).IsTrue();
        await Assert.That(rows[0].Edges[1].BoxLabel).IsEqualTo("таймаут → конец");
    }

    [Test]
    public async Task Router_DrawsNothingForAnOutcomeNamingAMissingNode()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "битая-ссылка",
            StartNodeId = "a",
            Nodes = [new DelayNode { Id = "a", Ms = 1, Next = "которого-нет" }],
        });

        await Assert.That(CanvasEdgeRouter.BuildAll(rows)).IsEmpty();
    }

    [Test]
    public async Task Router_UsesTheSideEntryForTheNextBoxOnTheSameRow()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "пара",
            StartNodeId = "a",
            Nodes =
            [
                new DelayNode { Id = "a", Ms = 1, Next = "b", Editor = new NodeEditorInfo(0, 0) },
                new DelayNode { Id = "b", Ms = 1, Editor = new NodeEditorInfo(CanvasMetrics.ColumnPitch, 0) },
            ],
        });

        var edge = CanvasEdgeRouter.BuildAll(rows).Single();

        await Assert.That(edge.IsDirect).IsTrue();
        await Assert.That(edge.Waypoints).Count().IsEqualTo(2);
        await Assert.That(edge.Waypoints[0].X).IsEqualTo(CanvasMetrics.NodeWidth);
        await Assert.That(edge.Waypoints[^1].X).IsEqualTo(CanvasMetrics.ColumnPitch);
        await Assert.That(edge.Waypoints[^1].Y).IsEqualTo(CanvasMetrics.SideEntryInset);
    }

    [Test]
    public async Task Router_SendsAWrappingEdgeThroughTheGutterAboveTheTargetRow()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "перенос",
            StartNodeId = "a",
            Nodes =
            [
                new DelayNode { Id = "a", Ms = 1, Next = "b", Editor = new NodeEditorInfo(CanvasMetrics.ColumnPitch * 2, 0) },
                new DelayNode { Id = "b", Ms = 1, Editor = new NodeEditorInfo(0, CanvasMetrics.RowPitch) },
            ],
        });

        var edge = CanvasEdgeRouter.BuildAll(rows).Single();

        await Assert.That(edge.IsDirect).IsFalse();
        await Assert.That(edge.Waypoints).Count().IsEqualTo(5);
        // The horizontal leg runs ABOVE the target row, not across the boxes of either row.
        var gutterY = CanvasMetrics.RowPitch - CanvasMetrics.GutterOffset;
        await Assert.That(edge.Waypoints[2].Y).IsEqualTo(gutterY);
        await Assert.That(edge.Waypoints[3].Y).IsEqualTo(gutterY);
        // …and it drops into the TOP edge of the target.
        await Assert.That(edge.Waypoints[^1].Y).IsEqualTo(CanvasMetrics.RowPitch);
        await Assert.That(edge.Waypoints[^1].X).IsEqualTo(CanvasMetrics.NodeWidth / 2);
    }

    [Test]
    public async Task Router_SpreadsSeveralEdgesArrivingAtTheSameBox()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "схождение",
            StartNodeId = "a",
            Nodes =
            [
                new DelayNode { Id = "a", Ms = 1, Next = "target", Editor = new NodeEditorInfo(0, 0) },
                new DelayNode { Id = "b", Ms = 1, Next = "target", Editor = new NodeEditorInfo(CanvasMetrics.ColumnPitch, 0) },
                new DelayNode { Id = "target", Ms = 1, Editor = new NodeEditorInfo(0, CanvasMetrics.RowPitch) },
            ],
        });

        var edges = CanvasEdgeRouter.BuildAll(rows);

        await Assert.That(edges).Count().IsEqualTo(2);
        await Assert.That(edges[0].Waypoints[^1].X).IsNotEqualTo(edges[1].Waypoints[^1].X);
    }

    [Test]
    public async Task Router_PutsTheTwoOutcomesOfAConditionalOnDifferentPorts()
    {
        var row = NodeRowViewModel.FromNode(new WaitForElementNode
        {
            Id = "wait",
            Template = "A",
            TimeoutMs = 1,
            Editor = new NodeEditorInfo(0, 0),
        });

        var found = CanvasEdgeRouter.OutcomePort(row, 0);
        var timeout = CanvasEdgeRouter.OutcomePort(row, 1);

        await Assert.That(found.X).IsEqualTo(CanvasMetrics.NodeWidth);
        await Assert.That(timeout.Y - found.Y).IsEqualTo(CanvasMetrics.OutcomeRowHeight);
        // Both inside the box, above its bottom edge.
        await Assert.That(timeout.Y).IsLessThan(row.LayoutHeight);
    }

    // ---- library grouping ---------------------------------------------------------------------

    [Test]
    public async Task Grouping_ReproducesTheMockupsOwnSections()
    {
        // Straight out of opt-1d.html, including the case the obvious rule gets wrong:
        // "Баг госта" has no dash yet belongs with "Баг госта-Лучник".
        var items = Items(
            "pw-boot", "pw-immunity", "pw-assist",
            "Баг госта", "Баг госта-Лучник", "Баг госта-Жрец",
            "Портал в столицу", "Сбор наград");

        var groups = MacroLibraryGrouping.Build(items);

        await Assert.That(groups.Select(g => g.Header))
            .IsEquivalentTo(new[] { "pw · 3", "баг госта · 3", "прочее · 2" });
    }

    [Test]
    public async Task Grouping_PutsOtherLast_EvenWhenItWouldSortEarlier()
    {
        var groups = MacroLibraryGrouping.Build(Items("я-раз", "я-два", "Абсолютно один"));

        await Assert.That(groups[^1].Prefix).IsEqualTo(MacroLibraryGrouping.OtherGroup);
        await Assert.That(groups[^1].Items.Single().Name).IsEqualTo("Абсолютно один");
    }

    [Test]
    public async Task Grouping_SplitsOnTheFirstDashOnly()
    {
        await Assert.That(MacroLibraryGrouping.PrefixOf("pw-cursor-click")).IsEqualTo("pw");
        await Assert.That(MacroLibraryGrouping.PrefixOf("Сбор наград")).IsEqualTo("Сбор наград");
        // A leading dash is not a prefix boundary — that would produce an empty heading.
        await Assert.That(MacroLibraryGrouping.PrefixOf("-странное")).IsEqualTo("-странное");
    }

    [Test]
    public async Task Library_IsGroupedAndFilterable()
    {
        var daemon = new FakeIpcClient();
        daemon.Respond(IpcMessageTypes.GetMacros, new[]
        {
            Graph("pw-boot"), Graph("pw-assist"), Graph("Сбор наград"),
        });
        using var vm = CreateEditor(daemon);

        await Assert.That(vm.MacroGroups.Select(g => g.Header))
            .IsEquivalentTo(new[] { "pw · 2", "прочее · 1" });

        vm.LibrarySearch = "сбор";

        await Assert.That(vm.MacroGroups.Select(g => g.Header)).IsEquivalentTo(new[] { "прочее · 1" });
    }

    // ---- the editor over the canvas -------------------------------------------------------------

    [Test]
    public async Task LoadGraph_PlacesEveryBox_WithoutMakingTheEditorDirty()
    {
        using var vm = CreateEditor();

        vm.LoadGraph(BootLike());

        await Assert.That(vm.Nodes.All(node => node.HasPosition)).IsTrue();
        // The layout is baked into the load baseline: opening an old macro is not an edit.
        await Assert.That(vm.IsDirty()).IsFalse();
    }

    [Test]
    public async Task MovingABox_IsAnEdit_AndRe_routesItsEdges()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var before = vm.CanvasEdges.Single(edge => edge.Source.NodeId == "click-server").Waypoints[0];

        vm.MoveNode(vm.Nodes.Single(node => node.NodeId == "click-server"), 1000, 900);

        await Assert.That(vm.IsDirty()).IsTrue();
        var after = vm.CanvasEdges.Single(edge => edge.Source.NodeId == "click-server").Waypoints[0];
        await Assert.That(after.X).IsNotEqualTo(before.X);
        await Assert.That(vm.BuildGraph().Nodes.Single(node => node.Id == "click-server").Editor)
            .IsEqualTo(new NodeEditorInfo(1000, 900));
    }

    [Test]
    public async Task RewiringAnOutcome_MovesTheLine()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var delay = vm.Nodes.Single(node => node.NodeId == "await-stats");

        vm.RewireEdge(delay.Edges[0], "close-stats");

        await Assert.That(vm.CanvasEdges.Single(edge => edge.Source.NodeId == "await-stats").Target.NodeId)
            .IsEqualTo("close-stats");
        await Assert.That(((DelayNode)vm.BuildGraph().Nodes.Single(n => n.Id == "await-stats")).Next)
            .IsEqualTo("close-stats");
    }

    [Test]
    public async Task RewiringToNothing_EndsTheRun_AndRemovesTheLine()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var delay = vm.Nodes.Single(node => node.NodeId == "await-stats");

        vm.RewireEdge(delay.Edges[0], null);

        await Assert.That(delay.Edges[0].IsEnd).IsTrue();
        await Assert.That(vm.CanvasEdges.Any(edge => edge.Source.NodeId == "await-stats")).IsFalse();
        await Assert.That(((DelayNode)vm.BuildGraph().Nodes.Single(n => n.Id == "await-stats")).Next).IsNull();
    }

    [Test]
    public async Task RewiringANodeToItself_IsRefused()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var delay = vm.Nodes.Single(node => node.NodeId == "await-stats");

        vm.RewireEdge(delay.Edges[0], "await-stats");

        await Assert.That(delay.Edges[0].TargetId).IsEqualTo("recognize");
    }

    [Test]
    public async Task RenamingANode_KeepsItsEdgesDrawn()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var before = vm.CanvasEdges.Count;

        vm.Nodes.Single(node => node.NodeId == "await-stats").NodeId = "пауза";

        await Assert.That(vm.CanvasEdges).Count().IsEqualTo(before);
        await Assert.That(vm.CanvasEdges.Any(edge => edge.Target.NodeId == "пауза")).IsTrue();
    }

    [Test]
    public async Task DeletingANode_DropsItsLines_AndTheOnesPointingAtIt()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        vm.DeleteNode(vm.Nodes.Single(node => node.NodeId == "await-stats"));

        await Assert.That(vm.CanvasEdges.Any(edge =>
            edge.Source.NodeId == "await-stats" || edge.Target.NodeId == "await-stats")).IsFalse();
    }

    [Test]
    public async Task AddNode_LandsOnAFreeSlot_NotOnTopOfAnything()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        var added = vm.AddNode(MacroNodeKind.Delay);

        await Assert.That(added.HasPosition).IsTrue();
        var occupied = vm.Nodes
            .Where(node => !ReferenceEquals(node, added))
            .Any(node => Math.Abs(node.X - added.X) < 1 && Math.Abs(node.Y - added.Y) < 1);
        await Assert.That(occupied).IsFalse();
    }

    [Test]
    public async Task AutoLayout_TidiesAHandMovedGraph()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        vm.MoveNode(vm.Nodes[0], 1500, 1500);

        vm.AutoLayout();

        await Assert.That(vm.Nodes.Single(node => node.NodeId == "wait-server").X).IsEqualTo(0d);
        await Assert.That(vm.Nodes.Single(node => node.NodeId == "wait-server").Y).IsEqualTo(0d);
    }

    [Test]
    public async Task Zoom_IsClamped()
    {
        using var vm = CreateEditor();

        vm.Zoom = 99;
        await Assert.That(vm.Zoom).IsEqualTo(MacroEditorViewModel.MaxZoom);

        vm.Zoom = 0;
        await Assert.That(vm.Zoom).IsEqualTo(MacroEditorViewModel.MinZoom);

        vm.Zoom = 1;
        await Assert.That(vm.ZoomText).IsEqualTo("100%");
    }

    [Test]
    public async Task OpeningAMacro_ResetsTheView()
    {
        using var vm = CreateEditor();
        vm.Zoom = 1.8;
        vm.PanX = -400;

        vm.LoadGraph(BootLike());

        await Assert.That(vm.Zoom).IsEqualTo(1d);
        await Assert.That(vm.PanX).IsGreaterThanOrEqualTo(0d);
    }

    [Test]
    public async Task ExpandingABox_ClosesTheOtherOne_AndSelectsIt()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var first = vm.Nodes[0];
        var second = vm.Nodes[1];

        vm.ExpandNode(first);
        vm.ExpandNode(second);

        await Assert.That(first.IsExpanded).IsFalse();
        await Assert.That(second.IsExpanded).IsTrue();
        await Assert.That(vm.SelectedNode).IsEqualTo(second);

        vm.CollapseNodes();
        await Assert.That(vm.Nodes.Any(node => node.IsExpanded)).IsFalse();
    }

    [Test]
    public async Task SelectingANode_SwitchesTheInspector()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        await Assert.That(vm.HasSelectedNode).IsFalse();
        await Assert.That(vm.InspectorTitle).IsEqualTo("Макрос");

        vm.SelectedNode = vm.Nodes.Single(node => node.NodeId == "await-stats");

        await Assert.That(vm.HasSelectedNode).IsTrue();
        await Assert.That(vm.InspectorTitle).IsEqualTo("Пауза");
    }

    // ---- the surface wave D3b has to fill ----------------------------------------------------

    [Test]
    public async Task RunLog_IsEmptyUntilTheDaemonReportsAnything()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        await Assert.That(vm.RunLog).IsEmpty();
        await Assert.That(vm.HasRunLog).IsFalse();
        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.ExecutingNodeId).IsNull();
        await Assert.That(vm.Nodes.Any(node => node.IsExecuting)).IsFalse();
        // The empty text distinguishes "nothing ran" from "nothing is being recorded" — the
        // strip is only fed while «Макросы» holds the subscription.
        await Assert.That(vm.RunLogEmptyText).IsEqualTo("лог пишется, пока открыт режим «Макросы»");
    }

    [Test]
    public async Task ExecutingNodeId_LightsExactlyOneBox()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        vm.ExecutingNodeId = "recognize";

        await Assert.That(vm.Nodes.Where(node => node.IsExecuting).Select(node => node.NodeId))
            .IsEquivalentTo(new[] { "recognize" });
        await Assert.That(vm.CanvasEdges.Single(edge => edge.Source.NodeId == "recognize" && edge.OutcomeIndex == 0).IsActive)
            .IsTrue();

        vm.ExecutingNodeId = null;
        await Assert.That(vm.Nodes.Any(node => node.IsExecuting)).IsFalse();
    }

    [Test]
    public async Task ARealRunFillsTheStripAndWalksTheHighlightAcrossTheCanvas()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x140804);

        daemon.Push(RunEvents.Started(walk));
        await Assert.That(vm.HasRuns).IsTrue();
        await Assert.That(vm.RunChipText).IsEqualTo("0x140804");
        await Assert.That(vm.SelectedRunIsLive).IsTrue();

        // Entering a node opens a row and lights the box; the row has no outcome yet.
        daemon.Push(RunEvents.Entered(walk, 0, "wait-server"));
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);
        await Assert.That(vm.RunLog[0].Outcome).IsEqualTo(RunLogRowViewModel.PendingOutcome);
        await Assert.That(vm.RunLog[0].IsCurrent).IsTrue();
        await Assert.That(vm.ExecutingNodeId).IsEqualTo("wait-server");
        await Assert.That(vm.Nodes.Single(node => node.IsExecuting).NodeId).IsEqualTo("wait-server");

        // Leaving it completes the SAME row in place — the strip must not flicker a new one.
        var openRow = vm.RunLog[0];
        daemon.Push(RunEvents.Exited(walk, 1200, "wait-server", RunOutcomes.Found, "A @ 1190,1802", 1200));
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);
        await Assert.That(ReferenceEquals(vm.RunLog[0], openRow)).IsTrue();
        await Assert.That(openRow.Elapsed).IsEqualTo("0:00.0");
        await Assert.That(openRow.Outcome).IsEqualTo("нашёл");
        await Assert.That(openRow.OutcomeIsAccent).IsTrue();
        await Assert.That(openRow.Detail).IsEqualTo("A @ 1190,1802 · 1.2 с");
        await Assert.That(openRow.IsCurrent).IsFalse();

        // The highlight follows the walker, one box at a time.
        daemon.Push(RunEvents.Entered(walk, 1200, "click-server"));
        await Assert.That(vm.Nodes.Where(node => node.IsExecuting).Select(node => node.NodeId))
            .IsEquivalentTo(new[] { "click-server" });
        await Assert.That(vm.CanvasEdges.Single(edge => edge.Source.NodeId == "click-server").IsActive).IsTrue();
        daemon.Push(RunEvents.Exited(walk, 1240, "click-server", RunOutcomes.Ok, "PostMessage 1192,1805", 40));
        await Assert.That(vm.RunLog[1].Detail).IsEqualTo("PostMessage 1192,1805 · 40 мс");
        await Assert.That(vm.RunLog[1].Outcome).IsEqualTo("ок");
        await Assert.That(vm.RunLog[1].OutcomeIsAccent).IsFalse();
        await Assert.That(vm.RunLog[1].Elapsed).IsEqualTo("0:01.2");

        // The run ends: the log stays for reading, the canvas goes dark.
        daemon.Push(RunEvents.Finished(walk, 1300, RunOutcomes.Completed));
        await Assert.That(vm.ExecutingNodeId).IsNull();
        await Assert.That(vm.Nodes.Any(node => node.IsExecuting)).IsFalse();
        await Assert.That(vm.RunLog).Count().IsEqualTo(2);
        await Assert.That(vm.SelectedRunIsLive).IsFalse();
    }

    [Test]
    public async Task AFanOutOverTenWindows_LightsOneBox_AndThePickerReachesTheRest()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        // The case the picker exists for: one graph, ten walks, ten different nodes live.
        var walks = Enumerable.Range(0, 10)
            .Select(i => RunEvents.Walk("pw-boot", hwnd: 0x100 + i))
            .ToList();
        var nodes = new[] { "wait-server", "click-server", "wait-char", "click-char", "open-stats" };
        foreach (var (walk, i) in walks.Select((w, i) => (w, i)))
        {
            daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, i * 10, nodes[i % nodes.Length]));
        }

        await Assert.That(vm.Runs).Count().IsEqualTo(10);
        // Exactly one — nine lit boxes on one graph is the noise this wave had to avoid.
        await Assert.That(vm.Nodes.Count(node => node.IsExecuting)).IsEqualTo(1);

        // The first walk keeps the selection: a live run is never stolen by a newer sibling.
        await Assert.That(vm.RunChipText).IsEqualTo("0x100");
        await Assert.That(vm.RunPositionText).IsEqualTo("1 / 10");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo("wait-server");

        vm.SelectNextRun();
        await Assert.That(vm.RunChipText).IsEqualTo("0x101");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo("click-server");
        await Assert.That(vm.Nodes.Count(node => node.IsExecuting)).IsEqualTo(1);
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);

        // ◂ from the first entry wraps to the last rather than dead-ending.
        vm.SelectPreviousRun();
        vm.SelectPreviousRun();
        await Assert.That(vm.RunChipText).IsEqualTo("0x109");
        await Assert.That(vm.RunPositionText).IsEqualTo("10 / 10");
    }

    [Test]
    public async Task AFinishedSelectionStepsAsideForANewRun_ButALiveOneDoesNot()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        var first = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(first), RunEvents.Entered(first, 0, "wait-server"));
        await Assert.That(vm.RunChipText).IsEqualTo("0x1");

        // A live selection holds against a newcomer.
        var second = RunEvents.Walk("pw-boot", hwnd: 0x2);
        daemon.Push(RunEvents.Started(second));
        await Assert.That(vm.RunChipText).IsEqualTo("0x1");

        // Once it is finished, the next run takes over — the user is otherwise left
        // watching a log that has stopped moving.
        daemon.Push(RunEvents.Finished(first, 100, RunOutcomes.Completed));
        var third = RunEvents.Walk("pw-boot", hwnd: 0x3);
        daemon.Push(RunEvents.Started(third), RunEvents.Entered(third, 0, "open-stats"));
        await Assert.That(vm.RunChipText).IsEqualTo("0x3");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo("open-stats");
    }

    [Test]
    public async Task OpeningAnotherMacroMidRun_SwapsTheLogAndComesBackToIt()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        var boot = RunEvents.Walk("pw-boot", hwnd: 0xA);
        var other = RunEvents.Walk("pw-assist", hwnd: 0xB);
        daemon.Push(
            RunEvents.Started(boot), RunEvents.Entered(boot, 0, "recognize"),
            RunEvents.Started(other), RunEvents.Entered(other, 0, "n1"));

        // Walks of another graph are tracked but change nothing on screen.
        await Assert.That(vm.Runs).Count().IsEqualTo(1);
        await Assert.That(vm.ExecutingNodeId).IsEqualTo("recognize");

        vm.LoadGraph(new MacroGraph
        {
            Name = "pw-assist",
            StartNodeId = "n1",
            Nodes = [new DelayNode { Id = "n1", Ms = 5 }],
        });
        await Assert.That(vm.RunChipText).IsEqualTo("0xB");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo("n1");
        await Assert.That(vm.Nodes.Single(node => node.IsExecuting).NodeId).IsEqualTo("n1");

        // Back again: the first walk's log survived rather than being discarded.
        vm.LoadGraph(BootLike());
        await Assert.That(vm.RunChipText).IsEqualTo("0xA");
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);
        await Assert.That(vm.ExecutingNodeId).IsEqualTo("recognize");
    }

    [Test]
    public async Task APartialLogSaysSo_WhetherItJoinedLateOrTheDaemonDroppedEvents()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.SubscribeRunEvents, new[]
            {
                // What the daemon answers with: walks already in flight, flagged as such.
                new RunWalkDto(Guid.NewGuid(), Guid.NewGuid(), "pw-boot", 0x140804, 0, DateTimeOffset.UtcNow, FromStart: false),
            });
        using var vm = CreateEditor(client);
        vm.LoadGraph(BootLike());

        await vm.SetRunEventSubscriptionAsync(true);

        await Assert.That(vm.HasRuns).IsTrue();
        await Assert.That(vm.HasRunLogNotice).IsTrue();
        await Assert.That(vm.RunLogNotice).IsEqualTo("начало прогона не записано");
        await Assert.That(vm.RunLogEmptyText).IsEqualTo("прогонов ещё не было");

        // A dropped batch is reported too — the alternative is a log with an invisible hole.
        // Both reasons stack: the selection is still the mid-run walk (a live one is never
        // stolen), so its head is missing AND the daemon has since discarded events.
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x2);
        client.RaiseEvent(IpcMessageTypes.RunEvents, new RunEventBatch([RunEvents.Started(walk)], Dropped: 12));
        await Assert.That(vm.RunLogNotice).IsEqualTo("начало прогона не записано · пропущено событий: 12");
    }

    [Test]
    public async Task ReconnectDropsTheHistory_AndReSubscribes()
    {
        var client = new FakeIpcClient().Respond(IpcMessageTypes.SubscribeRunEvents, Array.Empty<RunWalkDto>());
        using var vm = CreateEditor(client);
        vm.LoadGraph(BootLike());
        await vm.SetRunEventSubscriptionAsync(true);

        var walk = RunEvents.Walk("pw-boot", hwnd: 0x7);
        client.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "wait-server"));
        await Assert.That(vm.HasRunLog).IsTrue();

        client.RaiseConnected();

        // The daemon forgets a subscription with its connection, and the gap is unknowable —
        // so the panel re-asks and starts from nothing rather than resuming a broken log.
        await Assert.That(vm.HasRunLog).IsFalse();
        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.ExecutingNodeId).IsNull();
        await Assert.That(client.CountOf(IpcMessageTypes.SubscribeRunEvents)).IsEqualTo(2);
    }

    [Test]
    public async Task AnEventForAnUnknownWalkIsIgnoredRatherThanInventingARun()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        // Only possible when the WalkStarted was in a batch the daemon had to discard. A run
        // synthesised here would have no macro name and could not be filed under a graph.
        var orphan = RunEvents.Walk("pw-boot", hwnd: 0x9);
        daemon.Push(RunEvents.Entered(orphan, 0, "wait-server"));

        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.ExecutingNodeId).IsNull();
    }

    [Test]
    public async Task ClearRunLog_ForgetsEverythingWithoutStoppingAnything()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x5);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "wait-server"));

        vm.ClearRunLog();

        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.RunLog).IsEmpty();
        await Assert.That(vm.ExecutingNodeId).IsNull();
    }

    [Test]
    public async Task ALogCappedAtItsRowLimitDropsTheOldestRows()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x6);
        daemon.Push(RunEvents.Started(walk));

        // A macro that loops must not grow the panel without bound.
        for (var i = 0; i < MacroRunViewModel.MaxRows + 25; i++)
        {
            daemon.Push(RunEvents.Entered(walk, i, "await-stats"), RunEvents.Exited(walk, i, "await-stats", RunOutcomes.Ok, null, 1));
        }

        await Assert.That(vm.RunLog).Count().IsEqualTo(MacroRunViewModel.MaxRows);
        await Assert.That(vm.RunLog[0].Elapsed).IsEqualTo(RunLogRowViewModel.FormatElapsed(25));
    }

    // ---- box chrome --------------------------------------------------------------------------

    [Test]
    public async Task ConditionalBoxesAreTallerThanActionBoxes()
    {
        var action = NodeRowViewModel.FromNode(new DelayNode { Id = "d", Ms = 1 });
        var conditional = NodeRowViewModel.FromNode(new FindElementNode { Id = "f", Template = "t" });

        await Assert.That(action.IsConditional).IsFalse();
        await Assert.That(conditional.IsConditional).IsTrue();
        await Assert.That(action.LayoutHeight).IsEqualTo(CanvasMetrics.ActionNodeHeight);
        await Assert.That(conditional.LayoutHeight).IsEqualTo(CanvasMetrics.ConditionalNodeHeight);
    }

    [Test]
    public async Task BoxSummary_FollowsTheFieldsItDescribes()
    {
        var row = (RecognizeTagNodeRowViewModel)NodeRowViewModel.FromNode(new RecognizeTagNode
        {
            Id = "r",
            TemplateSet = "classes",
            Region = new ScreenRect(0, 0, 160, 35),
        });
        await Assert.That(row.Summary).IsEqualTo("набор classes · 160×35");

        var seen = false;
        row.PropertyChanged += (_, e) => seen |= e.PropertyName == nameof(NodeRowViewModel.Summary);

        // A region lives in its own view-model; without the forwarding its change would
        // never reach the box and the caption would silently go stale.
        row.Region.WidthText = "320";

        await Assert.That(seen).IsTrue();
        await Assert.That(row.Summary).IsEqualTo("набор classes · 320×35");
    }

    // D4 replaced the box's plain text chip with the live badge; Summary is now only the
    // SELECTOR half of it (the badge puts the window count in front — see TargetBadgeTests).
    [Test]
    public async Task TargetSummary_DescribesTheSelectorInWords()
    {
        var row = NodeRowViewModel.FromNode(new KeyPressNode { Id = "k", Key = VirtualKey.A });
        await Assert.That(row.Target!.Summary).IsEqualTo("контекст-окно");

        row.Target.UseSelector = true;
        await Assert.That(row.Target.Summary).IsEqualTo("все окна");

        row.Target.ExcludeText = "Склад";
        await Assert.That(row.Target.Summary).IsEqualTo("кроме Склад");
    }

    // The chip is on the box only when the node departs from the default. Wave D4 kept that
    // rule (a 210px header has no room for both a type label and a badge), so it stays
    // pinned.
    [Test]
    public async Task TargetChip_IsOnlyDrawnWhenTheNodeRoutesByTags()
    {
        var row = NodeRowViewModel.FromNode(new KeyPressNode { Id = "k", Key = VirtualKey.A });
        await Assert.That(row.ShowsTargetChip).IsFalse();

        var seen = false;
        row.PropertyChanged += (_, e) => seen |= e.PropertyName == nameof(NodeRowViewModel.ShowsTargetChip);

        row.Target!.UseSelector = true;

        await Assert.That(row.ShowsTargetChip).IsTrue();
        await Assert.That(seen).IsTrue();
    }

    private static List<MacroListItemViewModel> Items(params string[] names) =>
        [.. names.Select(name => new MacroListItemViewModel(Graph(name)))];

    private static MacroGraph Graph(string name) => new()
    {
        Name = name,
        StartNodeId = "n1",
        Nodes = [new DelayNode { Id = "n1", Ms = 1 }],
    };
}
