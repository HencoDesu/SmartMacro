using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// D5: pause, step, run-to-node and breakpoints inside the walker.
//
// Extends the executor suite rather than touching it — every pre-D5 test still runs with
// Debugger = null, which is the undebugged path and the one the daemon takes whenever no
// panel is attached. These cover the other path, and the three hazards it introduces:
//
//   · a walk parked with nobody attached would hold its single-flight slot forever;
//   · a pause inside a node would strand a woken game client;
//   · Stop has to be honest about whether it stops a walk or a run.
//
// The third is a protocol/UI decision (StopMacro cancels the RUN) and is asserted here only
// as "cancellation unparks a paused walk".
public class MacroDebuggerTests
{
    private const string Enter = "enter";
    private const string Exit = "exit";

    private static MacroDebugSession Session(bool attached = true)
    {
        var session = new MacroDebugSession(NullLogger<MacroDebugSession>.Instance);
        if (attached)
        {
            session.Acquire();
        }
        return session;
    }

    // Three keys, so a pause between the second and the third is unambiguous.
    private static MacroGraph Chain(string name = "цепочка") => ExecutorHarness.Graph(
        name,
        "a",
        new KeyPressNode { Id = "a", Key = VirtualKey.F1, Next = "b" },
        new KeyPressNode { Id = "b", Key = VirtualKey.F2, Next = "c" },
        new KeyPressNode { Id = "c", Key = VirtualKey.F3 });

    /// <summary>Spins until <paramref name="condition"/> holds; fails the test rather than hanging forever.</summary>
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        await Assert.That(condition()).IsTrue().Because(what);
    }

    private static Task<Guid> WalkId(RecordingObserver observer) =>
        Task.FromResult(observer.Walks.Count > 0 ? observer.Walks[0].WalkId : Guid.Empty);

    // ---- nothing attached: the walker must not notice the debugger exists ---------------

    [Test]
    public async Task WithNoDebuggerAttached_BreakpointsDoNotBite()
    {
        var harness = new ExecutorHarness();
        var session = Session(attached: false);
        session.SetBreakpoints("цепочка", ["b"]);

        // The daemon is resident: a breakpoint that halted a walk nobody is watching would
        // wedge the macro's hotkey until a restart. Storage survives; the halt does not.
        var result = await harness.Executor
            .RunAsync(Chain(), harness.Context(ExecutorHarness.Window, debugger: session), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(3);
    }

    [Test]
    public async Task WithNoDebuggerAtAll_TheWalkIsUnchanged()
    {
        var harness = new ExecutorHarness();

        var result = await harness.Executor
            .RunAsync(Chain(), harness.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(3);
    }

    [Test]
    public async Task AnInactiveDebuggerIsNeverAsked()
    {
        var harness = new ExecutorHarness();
        var spy = new CountingDebugger { IsActive = false };

        await harness.Executor.RunAsync(Chain(), harness.Context(ExecutorHarness.Window, debugger: spy), CancellationToken.None);

        // The IsActive gate is the whole cost model, same as the observer's IsEnabled: an
        // undebugged walk must not reach a lock or a dictionary per node.
        await Assert.That(spy.ArmCalls).IsEqualTo(0);
    }

    // ---- breakpoints ---------------------------------------------------------------------

    [Test]
    public async Task ABreakpointParksTheWalkBeforeTheNodeRuns()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["b"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count > 0, "the walk should park");

        // BEFORE, not during: exactly one key has been sent, and it is the one from node 'a'.
        // This is hazard 2 in assertion form — the node the walk is parked at has not started,
        // so nothing has woken a game window that is now waiting on a human.
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);
        var paused = observer.OfKind(RecordingObserver.PausedKind).Single();
        await Assert.That(paused.NodeId).IsEqualTo("b");
        await Assert.That(paused.Outcome).IsEqualTo(nameof(DebugPauseReason.Breakpoint));

        // And the announcement lands AFTER the node's enter event, so the canvas has already
        // lit the box the walk is standing on by the time it says «на паузе».
        var kinds = observer.Entries.Select(e => $"{e.Kind}:{e.NodeId}").ToList();
        await Assert.That(kinds.IndexOf("paused:b")).IsGreaterThan(kinds.IndexOf("enter:b"));

        session.Command(await WalkId(observer), DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(3);
    }

    [Test]
    public async Task ClearingABreakpointStopsItBiting()
    {
        var harness = new ExecutorHarness();
        var session = Session();
        session.SetBreakpoints("цепочка", ["b"]);
        session.SetBreakpoints("цепочка", []);

        var result = await harness.Executor
            .RunAsync(Chain(), harness.Context(ExecutorHarness.Window, debugger: session), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(session.Breakpoints()).IsEmpty();
    }

    [Test]
    public async Task BreakpointsAreKeyedByMacro_NotJustByNodeId()
    {
        var harness = new ExecutorHarness();
        // 'b' exists in both graphs; the breakpoint belongs to the other one.
        var session = Session();
        session.SetBreakpoints("другой", ["b"]);

        var result = await harness.Executor
            .RunAsync(Chain(), harness.Context(ExecutorHarness.Window, debugger: session), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
    }

    [Test]
    public async Task BreakpointsSurviveAWalkAndApplyToTheNextOne()
    {
        // The ergonomic half of "session, in the daemon": the panel can come and go, and so
        // can runs — the red dot stays put until someone clears it.
        var harness = new ExecutorHarness();
        var session = Session();
        session.SetBreakpoints("цепочка", ["c"]);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var observer = new RecordingObserver();
            var run = harness.Executor.RunAsync(
                Chain(),
                harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
                CancellationToken.None);

            await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count > 0, $"attempt {attempt} should park");
            session.Command(await WalkId(observer), DebugCommand.Resume, null);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Assert.That(session.Breakpoints().Single().NodeIds).IsEquivalentTo(new[] { "c" });
    }

    // ---- step / run-to-node / pause -------------------------------------------------------

    [Test]
    public async Task StepAdvancesExactlyOneNode()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["a"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at a");
        var walkId = await WalkId(observer);

        session.Command(walkId, DebugCommand.Step, null);

        // One node ran, and the walk is parked again — this time because of the step, which
        // the panel renders differently from a breakpoint.
        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 2, "park at b");
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);
        var second = observer.OfKind(RecordingObserver.PausedKind)[1];
        await Assert.That(second.NodeId).IsEqualTo("b");
        await Assert.That(second.Outcome).IsEqualTo(nameof(DebugPauseReason.Step));

        session.Command(walkId, DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(3);
    }

    [Test]
    public async Task RunToNodeSkipsPastTheNodesInBetween()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["a"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at a");
        session.Command(await WalkId(observer), DebugCommand.RunToNode, "c");

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 2, "park at c");
        var second = observer.OfKind(RecordingObserver.PausedKind)[1];
        await Assert.That(second.NodeId).IsEqualTo("c");
        await Assert.That(second.Outcome).IsEqualTo(nameof(DebugPauseReason.Cursor));
        // 'a' and 'b' both ran; 'c' has not.
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(2);

        session.Command(await WalkId(observer), DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RunToANodeTheWalkNeverReaches_JustLetsItFinish()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["a"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at a");
        session.Command(await WalkId(observer), DebugCommand.RunToNode, "не-существует");

        // Not an error and not a hang: a branch that never goes there is a legitimate outcome.
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
    }

    [Test]
    public async Task RunToNodeWithoutANodeIdIsRefused_RatherThanBeingASilentResume()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["a"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at a");
        var walkId = await WalkId(observer);

        var ack = session.Command(walkId, DebugCommand.RunToNode, null);

        await Assert.That(ack.Accepted).IsFalse();
        // Still parked — the refusal did not release it by accident.
        await Assert.That(ack.Paused).IsTrue();
        await Assert.That(harness.Primitives.Calls).IsEmpty();

        session.Command(walkId, DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task PauseIsARequestHonouredAtTheNextNodeBoundary()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();

        // Node 'a' blocks until we let it go, which is the window in which the Pause arrives.
        var inNode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Primitives.PressKeyGate = async () =>
        {
            inNode.TrySetResult();
            await releaseNode.Task;
        };

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await inNode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var walkId = await WalkId(observer);

        var ack = session.Command(walkId, DebugCommand.Pause, null);
        await Assert.That(ack.Accepted).IsTrue();
        // Not parked YET — the walk is inside a node that may run for a minute, and the panel
        // has to say «пауза…» rather than «на паузе» until the event confirms it.
        await Assert.That(ack.Paused).IsFalse();
        await Assert.That(ack.PauseRequested).IsTrue();

        harness.Primitives.PressKeyGate = null;
        releaseNode.SetResult();

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at the next node");
        var paused = observer.OfKind(RecordingObserver.PausedKind).Single();
        await Assert.That(paused.NodeId).IsEqualTo("b");
        await Assert.That(paused.Outcome).IsEqualTo(nameof(DebugPauseReason.Requested));

        session.Command(walkId, DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task PausingAWalkThatHasAlreadyFinishedIsRefused()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();

        await harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        var ack = session.Command(await WalkId(observer), DebugCommand.Pause, null);

        // «Accepted = false» is how the panel learns to stop lighting a Pause button for a
        // walk that ended while the click was in flight.
        await Assert.That(ack.Accepted).IsFalse();
    }

    [Test]
    public async Task ResumeAndPausedEventsBracketTheStall()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["b"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at b");
        session.Command(await WalkId(observer), DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // paused → resumed → the node actually runs. Without the resume event the toolbar
        // would keep saying «на паузе» through a node that can legitimately take 60 seconds.
        var kinds = observer.Entries.Select(e => $"{e.Kind}:{e.NodeId}").ToList();
        var paused = kinds.IndexOf($"{RecordingObserver.PausedKind}:b");
        var resumed = kinds.IndexOf($"{RecordingObserver.ResumedKind}:b");
        await Assert.That(resumed).IsGreaterThan(paused);
        await Assert.That(kinds.IndexOf($"{Exit}:b")).IsGreaterThan(resumed);
    }

    // ---- hazard 1: a parked walk must never outlive its audience -------------------------

    [Test]
    public async Task TheLastDebuggerDetaching_AutoResumesEveryParkedWalk()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["b"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at b");

        // The panel crashed, or the user just closed the window. Nothing can press resume.
        session.Release();

        // The walk RUNS ON rather than aborting: it was started legitimately and abandoning a
        // macro halfway can leave the game worse off than letting it finish.
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(3);
        await Assert.That(session.AttachedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ASecondDebuggerKeepsTheWalkParkedWhenTheFirstLeaves()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.Acquire(); // two panels attached
        session.SetBreakpoints("цепочка", ["b"]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at b");
        session.Release();

        await Task.Delay(120);
        await Assert.That(run.IsCompleted).IsFalse();
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);

        session.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task BreakpointsStopBitingWhileNobodyIsAttached_ButAreNotForgotten()
    {
        var harness = new ExecutorHarness();
        var session = Session();
        session.SetBreakpoints("цепочка", ["b"]);
        session.Release();

        var result = await harness.Executor
            .RunAsync(Chain(), harness.Context(ExecutorHarness.Window, debugger: session), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);

        // Still there for the next panel — the daemon outliving the panel is exactly why
        // session-scoped storage is enough.
        await Assert.That(session.Breakpoints().Single().NodeIds).IsEquivalentTo(new[] { "b" });
    }

    // ---- hazard 3: stop -------------------------------------------------------------------

    [Test]
    public async Task CancellationUnparksAPausedWalk()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        session.SetBreakpoints("цепочка", ["b"]);
        using var cts = new CancellationTokenSource();

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(ExecutorHarness.Window, observer: observer, debugger: session),
            cts.Token);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "park at b");

        // ■ Стоп cancels the RUN's token — and daemon shutdown cancels every run's. Either
        // way a parked walk has to come out of the gate rather than holding its single-flight
        // slot until the process dies.
        await cts.CancelAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);
    }

    // ---- fan-out: the unit is the walk ----------------------------------------------------

    [Test]
    public async Task EachWalkOfAFanOutIsPausedIndependently()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var session = Session();
        harness.Registry.Register(0x31, "elementclient");
        harness.Registry.Register(0x32, "elementclient");
        harness.Registry.AddTag(0x31, "клиент");
        harness.Registry.AddTag(0x32, "клиент");

        harness.Resolver.Add(ExecutorHarness.Graph(
            "sub",
            "press",
            new KeyPressNode { Id = "press", Key = VirtualKey.C }));
        var parent = ExecutorHarness.Graph(
            "parent",
            "fan",
            new RunMacroNode { Id = "fan", MacroName = "sub", Target = new TargetSelector { RequireTags = ["клиент"] } });
        session.SetBreakpoints("sub", ["press"]);

        var run = harness.Executor.RunAsync(
            parent,
            harness.Context(observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 2, "both sub-walks park");
        var parked = observer.OfKind(RecordingObserver.PausedKind);
        await Assert.That(parked.Select(e => e.WalkId).Distinct()).Count().IsEqualTo(2);

        // Releasing one leaves the other parked — which is the point of the walk picker: the
        // user is looking at one of ten windows and steps that one.
        session.Command(parked[0].WalkId, DebugCommand.Resume, null);
        await Task.Delay(120);
        await Assert.That(run.IsCompleted).IsFalse();
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);

        session.Command(parked[1].WalkId, DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(2);
    }

    // ---- variable reporting ----------------------------------------------------------------

    [Test]
    public async Task TheTriggerSeedIsReportedAtTheHeadOfTheWalk()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var variables = MacroVariables.ForTrigger(new ScreenPoint(1804, 902));

        await harness.Executor.RunAsync(
            ExecutorHarness.Graph("сид", "a", new DelayNode { Id = "a", Ms = 0 }),
            harness.Context(ExecutorHarness.Window, variables: variables, observer: observer),
            CancellationToken.None);

        // No node ever writes `cursor`, so without this the variables panel could only ever
        // show it as «нет значения» — the one value that is always available.
        var seed = observer.OfKind(RecordingObserver.VariableKind).Single();
        await Assert.That(seed.Outcome).IsEqualTo("cursor");
        await Assert.That(seed.Detail).IsEqualTo(new ScreenPoint(1804, 902).ToString());
        await Assert.That(seed.NodeId).IsNull();

        // And it is reported before the first node, so a walk paused on node one already has it.
        await Assert.That(observer.Entries[1].Kind).IsEqualTo(RecordingObserver.VariableKind);
    }

    [Test]
    public async Task ConditionalNodesReportWhatTheyWrote()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        harness.Primitives.RecognizeHandler = (_, _, _) => "Жрец";
        harness.Primitives.FindHandler = (_, _, _) => new ScreenPoint(1190, 1802);

        var graph = ExecutorHarness.Graph(
            "запись",
            "r",
            new RecognizeTagNode
            {
                Id = "r",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 1, 1),
                ApplyTag = false,
                Matched = "f",
            },
            new FindElementNode { Id = "f", Template = "X", FoundPointVar = "точка" });

        await harness.Executor.RunAsync(
            graph,
            harness.Context(ExecutorHarness.Window, observer: observer),
            CancellationToken.None);

        var writes = observer.OfKind(RecordingObserver.VariableKind);
        await Assert.That(writes.Select(e => e.Outcome)).IsEquivalentTo(new[] { "tag", "точка" });
        await Assert.That(writes[0].Detail).IsEqualTo("Жрец");
        await Assert.That(writes[0].NodeId).IsEqualTo("r");
        await Assert.That(writes[1].Detail).IsEqualTo(new ScreenPoint(1190, 1802).ToString());
        await Assert.That(writes[1].NodeId).IsEqualTo("f");
    }

    [Test]
    public async Task WithNobodyListening_NoVariableEventsAreProduced()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver { IsEnabled = false };
        harness.Primitives.RecognizeHandler = (_, _, _) => "Жрец";

        await harness.Executor.RunAsync(
            ExecutorHarness.Graph(
                "тихо",
                "r",
                new RecognizeTagNode { Id = "r", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1), ApplyTag = false }),
            harness.Context(ExecutorHarness.Window, variables: MacroVariables.ForTrigger(default), observer: observer),
            CancellationToken.None);

        await Assert.That(observer.OfKind(RecordingObserver.VariableKind)).IsEmpty();
    }

    [Test]
    public async Task AVariableWriteStillHappensWhenNobodyIsListening()
    {
        // The report is instrumentation; the assignment is behaviour. Routing both through one
        // helper is only safe if the gate covers the first and not the second.
        var harness = new ExecutorHarness();
        harness.Primitives.RecognizeHandler = (_, _, _) => "Жрец";

        var graph = ExecutorHarness.Graph(
            "запись-без-наблюдателя",
            "r",
            new RecognizeTagNode
            {
                Id = "r",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 1, 1),
                ApplyTag = false,
                Matched = "i",
            },
            new SetIconNode { Id = "i", IconPath = "icons/{tag}.png" });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(harness.Primitives.Calls.Single(c => c.Op == "SetIcon").A).IsEqualTo("icons/Жрец.png");
    }

    /// <summary>Counts <see cref="IMacroDebugger.Arm"/> calls, to prove the gate is skipped when inactive.</summary>
    private sealed class CountingDebugger : IMacroDebugger
    {
        public bool IsActive { get; set; }

        public int ArmCalls { get; private set; }

        public MacroDebugGate? Arm(Guid walkId, string macroName, string nodeId)
        {
            ArmCalls++;
            return null;
        }

        public void Disarm(MacroDebugGate gate)
        {
        }

        public void WalkFinished(Guid walkId)
        {
        }
    }
}
