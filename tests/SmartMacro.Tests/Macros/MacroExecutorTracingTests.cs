using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// D3b: the walker's progress channel.
//
// Extends the walker suite rather than replacing any of it — every existing test still runs
// with Observer = null, which is the untraced path and the one the daemon takes whenever no
// panel is watching. These cover the other path: what is reported, what a WALK is (as
// opposed to a run), and the guarantee the whole design rests on — that an unwatched daemon
// pays for none of it.
public class MacroExecutorTracingTests
{
    private const string Enter = "enter";
    private const string Exit = "exit";
    private const string WalkStart = "walk";
    private const string WalkEnd = "end";

    // ---- the flooding guarantee ------------------------------------------------------

    [Test]
    public async Task WithNobodyListening_NoNodeEventsAreProduced()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver { IsEnabled = false };
        var graph = ExecutorHarness.Graph(
            "тихо",
            "a",
            new DelayNode { Id = "a", Ms = 0, Next = "b" },
            new KeyPressNode { Id = "b", Key = VirtualKey.C });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer), CancellationToken.None);

        // This is the load-bearing assertion of the wave: the daemon is resident and the
        // panel is not, so the common case must cost nothing on the walker's hot path.
        await Assert.That(observer.OfKind(Enter)).IsEmpty();
        await Assert.That(observer.OfKind(Exit)).IsEmpty();

        // The walk-level pair still fires — it is twice per RUN, and it is what lets a panel
        // connecting mid-run discover that a run exists at all.
        await Assert.That(observer.OfKind(WalkStart)).Count().IsEqualTo(1);
        await Assert.That(observer.OfKind(WalkEnd)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ARunWithNoObserverAtAllStillWalksTheGraph()
    {
        var harness = new ExecutorHarness();
        var graph = ExecutorHarness.Graph(
            "без-наблюдателя",
            "a",
            new KeyPressNode { Id = "a", Key = VirtualKey.F1 });

        var result = await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(harness.Primitives.Calls.Select(call => call.Op)).IsEquivalentTo(new[] { "PressKey" });
    }

    // ---- what a node reports ---------------------------------------------------------

    [Test]
    public async Task EveryNodeReportsAnEnterAndAnExit_InWalkOrder()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var graph = ExecutorHarness.Graph(
            "цепочка",
            "a",
            new DelayNode { Id = "a", Ms = 0, Next = "b" },
            new ClickNode { Id = "b", Point = new ScreenPoint(1192, 1805), Next = "c" },
            new KeyPressNode { Id = "c", Key = VirtualKey.C });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer), CancellationToken.None);

        await Assert.That(observer.Entries.Select(e => $"{e.Kind}:{e.NodeId}")).IsEquivalentTo(new[]
        {
            "walk:", "enter:a", "exit:a", "enter:b", "exit:b", "enter:c", "exit:c", "end:",
        });
        await Assert.That(observer.OfKind(WalkEnd)[0].Outcome).IsEqualTo(RunOutcomes.Completed);
    }

    [Test]
    public async Task ActionNodesCarryTheDetailTheMockupPrints()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var graph = ExecutorHarness.Graph(
            "детали",
            "click",
            new ClickNode { Id = "click", Point = new ScreenPoint(1192, 1805), Next = "key" },
            new KeyPressNode { Id = "key", Key = VirtualKey.C, Next = "wait" },
            new DelayNode { Id = "wait", Ms = 500 });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer), CancellationToken.None);

        var details = observer.OfKind(Exit).ToDictionary(e => e.NodeId!, e => e.Detail);
        await Assert.That(details["click"]).IsEqualTo("1192,1805");
        await Assert.That(details["key"]).IsEqualTo("C");
        await Assert.That(details["wait"]).IsEqualTo("500 мс");
        await Assert.That(observer.OfKind(Exit).All(e => e.Outcome == RunOutcomes.Ok)).IsTrue();
    }

    [Test]
    public async Task ASelectorFanOutSaysHowManyWindowsItHit()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        harness.Registry.Register(0x1, "elementclient");
        harness.Registry.Register(0x2, "elementclient");
        harness.Registry.Register(0x3, "elementclient");
        harness.Registry.AddTag(0x1, "Жрец");
        harness.Registry.AddTag(0x2, "Жрец");

        var graph = ExecutorHarness.Graph(
            "веер",
            "a",
            new KeyPressNode { Id = "a", Key = VirtualKey.F1, Target = new TargetSelector { RequireTags = ["Жрец"] }, Next = "b" },
            new KeyPressNode { Id = "b", Key = VirtualKey.F2, Target = new TargetSelector { RequireTags = ["Оборотень"] } });

        await harness.Executor.RunAsync(graph, harness.Context(observer: observer), CancellationToken.None);

        var details = observer.OfKind(Exit).ToDictionary(e => e.NodeId!, e => e.Detail);
        await Assert.That(details["a"]).IsEqualTo("F1 ×2");
        // Zero matches is a legal no-op, and the log is the only place it is visible.
        await Assert.That(details["b"]).IsEqualTo("F2 ×0");
    }

    [Test]
    public async Task ConditionalNodesReportWhichEdgeTheyTook()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        harness.Primitives.WaitHandler = (_, template, _, _) =>
            template == "ChatPanelButtons" ? new ScreenPoint(1190, 1802) : null;
        harness.Primitives.RecognizeHandler = (_, _, _) => null;

        var graph = ExecutorHarness.Graph(
            "условия",
            "found",
            new WaitForElementNode { Id = "found", Template = "ChatPanelButtons", TimeoutMs = 1, Found = "lost" },
            new WaitForElementNode { Id = "lost", Template = "Nope", TimeoutMs = 1, Timeout = "tag" },
            new RecognizeTagNode { Id = "tag", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1) });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer), CancellationToken.None);

        var exits = observer.OfKind(Exit);
        await Assert.That(exits.Select(e => e.Outcome)).IsEquivalentTo(new[]
        {
            RunOutcomes.Found, RunOutcomes.Timeout, RunOutcomes.NotMatched,
        });
        await Assert.That(exits[0].Detail).IsEqualTo("ChatPanelButtons @ 1190,1802");
        await Assert.That(exits[1].Detail).IsEqualTo("Nope · лимит 1 мс");
    }

    [Test]
    public async Task ANodeThatBlowsUpIsNamedInTheLog_AndTheWalkReportsTheReason()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        // No context window and no selector — the walker aborts inside this node.
        var graph = ExecutorHarness.Graph("падение", "a", new KeyPressNode { Id = "a", Key = VirtualKey.C });

        var result = await harness.Executor.RunAsync(graph, harness.Context(observer: observer), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        var exit = observer.OfKind(Exit).Single();
        await Assert.That(exit.NodeId).IsEqualTo("a");
        await Assert.That(exit.Outcome).IsEqualTo(RunOutcomes.Error);
        await Assert.That(exit.Detail).Contains("no Target selector");
        await Assert.That(observer.OfKind(WalkEnd).Single().Outcome).IsEqualTo(RunOutcomes.Aborted);
    }

    [Test]
    public async Task ACancelledRunEndsAsCancelled_NotAborted()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        using var cts = new CancellationTokenSource();
        harness.Primitives.PressKeyGate = async () =>
        {
            await cts.CancelAsync();
        };

        var graph = ExecutorHarness.Graph(
            "отмена",
            "a",
            new KeyPressNode { Id = "a", Key = VirtualKey.C, Next = "b" },
            new DelayNode { Id = "b", Ms = 5000 });

        var result = await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer), cts.Token);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(observer.OfKind(WalkEnd).Single().Outcome).IsEqualTo(RunOutcomes.Cancelled);
    }

    // ---- walks vs runs ---------------------------------------------------------------

    [Test]
    public async Task AFanOutOfSubMacrosIsOneWalkPerWindow_AllSharingTheRunId()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var runId = Guid.NewGuid();
        harness.Registry.Register(0x11, "elementclient");
        harness.Registry.Register(0x12, "elementclient");
        harness.Registry.Register(0x13, "elementclient");
        foreach (var hwnd in new[] { 0x11, 0x12, 0x13 })
        {
            harness.Registry.AddTag(hwnd, "клиент");
        }

        harness.Resolver.Add(ExecutorHarness.Graph(
            "pw-identify-one",
            "press",
            new KeyPressNode { Id = "press", Key = VirtualKey.C }));
        var parent = ExecutorHarness.Graph(
            "pw-identify",
            "fan",
            new RunMacroNode
            {
                Id = "fan",
                MacroName = "pw-identify-one",
                Target = new TargetSelector { RequireTags = ["клиент"] },
            });

        await harness.Executor.RunAsync(parent, harness.Context(observer: observer, runId: runId), CancellationToken.None);

        // Four walks: the parent plus one per window. This is exactly why the panel follows a
        // WALK — following the run would mean three windows fighting over one highlight.
        var walks = observer.Walks;
        await Assert.That(walks).Count().IsEqualTo(4);
        await Assert.That(walks.Select(w => w.WalkId).Distinct()).Count().IsEqualTo(4);
        await Assert.That(walks.All(w => w.RunId == runId)).IsTrue();

        await Assert.That(walks[0].MacroName).IsEqualTo("pw-identify");
        await Assert.That(walks[0].Depth).IsEqualTo(0);
        await Assert.That(walks[0].ContextWindow).IsNull();

        var children = walks.Skip(1).ToList();
        await Assert.That(children.All(w => w.MacroName == "pw-identify-one")).IsTrue();
        await Assert.That(children.All(w => w.Depth == 1)).IsTrue();
        await Assert.That(children.Select(w => w.ContextWindow!.Value.ToInt64()).OrderBy(h => h))
            .IsEquivalentTo(new long[] { 0x11, 0x12, 0x13 });

        // And the parent's own node line says what it fanned out to.
        await Assert.That(observer.OfKind(Exit).Single(e => e.NodeId == "fan").Detail)
            .IsEqualTo("pw-identify-one ×3");
    }

    [Test]
    public async Task EachChildWalksNodesUnderItsOwnWalkId()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        harness.Registry.Register(0x21, "elementclient");
        harness.Registry.Register(0x22, "elementclient");
        harness.Registry.AddTag(0x21, "клиент");
        harness.Registry.AddTag(0x22, "клиент");

        harness.Resolver.Add(ExecutorHarness.Graph(
            "sub",
            "press",
            new KeyPressNode { Id = "press", Key = VirtualKey.C }));
        var parent = ExecutorHarness.Graph(
            "parent",
            "fan",
            new RunMacroNode { Id = "fan", MacroName = "sub", Target = new TargetSelector { RequireTags = ["клиент"] } });

        await harness.Executor.RunAsync(parent, harness.Context(observer: observer), CancellationToken.None);

        // Two 'press' nodes reported, under two DIFFERENT walk ids — otherwise the panel
        // could not tell the ten boots of a party apart.
        var presses = observer.OfKind(Enter).Where(e => e.NodeId == "press").ToList();
        await Assert.That(presses).Count().IsEqualTo(2);
        await Assert.That(presses.Select(e => e.WalkId).Distinct()).Count().IsEqualTo(2);
    }

    [Test]
    public async Task ASubMacroRunReportsItsOwnWalkEvenWhenNothingIsListening()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver { IsEnabled = false };
        harness.Resolver.Add(ExecutorHarness.Graph("sub", "press", new KeyPressNode { Id = "press", Key = VirtualKey.C }));
        var parent = ExecutorHarness.Graph("parent", "call", new RunMacroNode { Id = "call", MacroName = "sub" });

        await harness.Executor.RunAsync(parent, harness.Context(ExecutorHarness.Window, observer: observer), CancellationToken.None);

        await Assert.That(observer.Walks).Count().IsEqualTo(2);
        await Assert.That(observer.OfKind(WalkEnd)).Count().IsEqualTo(2);
        await Assert.That(observer.OfKind(Enter)).IsEmpty();
    }

    [Test]
    public async Task TheOldOnNodeEnteredHookStillFires_AlongsideTheObserver()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var entered = new List<string>();
        var graph = ExecutorHarness.Graph(
            "оба",
            "a",
            new DelayNode { Id = "a", Ms = 0, Next = "b" },
            new DelayNode { Id = "b", Ms = 0 });

        await harness.Executor.RunAsync(
            graph,
            harness.Context(ExecutorHarness.Window, onNodeEntered: entered.Add, observer: observer),
            CancellationToken.None);

        // The registry's current-node hook predates D3b and still feeds «Прогоны»; the two
        // channels are deliberately independent.
        await Assert.That(entered).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(observer.OfKind(Enter).Select(e => e.NodeId)).IsEquivalentTo(new[] { "a", "b" });
    }
}
