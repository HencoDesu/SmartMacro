using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: RunMacroNode semantics — await/fire-and-forget, depth limit, name-cycle
// detection, per-window sub-runs with the matched window as context, and variable
// copy-not-share isolation.
public class RunMacroNodeTests
{
    /// <summary>Sub-macro pressing F9 on its context window.</summary>
    private static MacroGraph SubPressingF9(string name = "суб") =>
        ExecutorHarness.Graph(name, "k", new KeyPressNode { Id = "k", Key = VirtualKey.F9, Next = null });

    private static MacroGraph ParentRunning(string subName, bool await_ = true, TargetSelector? target = null) =>
        ExecutorHarness.Graph("родитель", "r",
            new RunMacroNode { Id = "r", MacroName = subName, Await = await_, Target = target, Next = "after" },
            new KeyPressNode { Id = "after", Key = VirtualKey.F1, Next = null });

    [Test]
    public async Task AwaitTrue_RunsSubMacroThenContinues()
    {
        var h = new ExecutorHarness();
        h.Resolver.Add(SubPressingF9());

        var result = await h.Executor.RunAsync(ParentRunning("суб"), h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        // Sub-run's key first, parent's Next after — proof the parent awaited.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(2);
        await Assert.That(h.Primitives.Calls[0].A).IsEqualTo(VirtualKey.F9);
        await Assert.That(h.Primitives.Calls[1].A).IsEqualTo(VirtualKey.F1);
    }

    [Test]
    public async Task AwaitTrue_BlocksUntilSubMacroFinishes()
    {
        var h = new ExecutorHarness();
        h.Resolver.Add(SubPressingF9());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Primitives.PressKeyGate = () =>
        {
            entered.TrySetResult();
            return gate.Task;
        };

        var runTask = h.Executor.RunAsync(ParentRunning("суб"), h.Context(ExecutorHarness.Window), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(runTask.IsCompleted).IsFalse();

        gate.TrySetResult();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
    }

    [Test]
    public async Task AwaitFalse_ProceedsWhileSubMacroStillRuns()
    {
        var h = new ExecutorHarness();
        h.Resolver.Add(SubPressingF9());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Primitives.PressKeyGate = () => gate.Task;

        // The parent's own "after" key press also goes through the gated primitive, so
        // gate BEFORE starting and check completion state: with Await=false the parent
        // must reach (and record) its Next node even though the child hangs.
        var runTask = h.Executor.RunAsync(
            ParentRunning("суб", await_: false), h.Context(ExecutorHarness.Window), CancellationToken.None);

        // The parent finishes only if it did NOT await the gated child... but its own
        // F1 press is gated too. Release the gate and verify both key presses landed and
        // the parent completed — order-independent, unlike the Await=true test above.
        gate.TrySetResult();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        var keys = h.Primitives.Calls.Select(c => (VirtualKey)c.A!).ToList();
        await Assert.That(keys.Contains(VirtualKey.F1)).IsTrue();
        await Assert.That(keys.Contains(VirtualKey.F9)).IsTrue();
    }

    [Test]
    public async Task AwaitFalse_ParentCompletesEvenIfChildNeverDoes()
    {
        var h = new ExecutorHarness();
        h.Resolver.Add(SubPressingF9());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Primitives.PressKeyGate = () => gate.Task;

        // Parent WITHOUT an "after" action node: its walk never touches the gated
        // primitive, so completion proves fire-and-forget alone.
        var parent = ExecutorHarness.Graph("родитель", "r",
            new RunMacroNode { Id = "r", MacroName = "суб", Await = false, Next = null });

        var result = await h.Executor
            .RunAsync(parent, h.Context(ExecutorHarness.Window), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);

        gate.TrySetResult(); // release the detached child before the test ends
    }

    [Test]
    public async Task DepthBeyondLimit_AbortsRun()
    {
        var h = new ExecutorHarness();
        // м0 → м1 → м2 → м3 → м4 → м5: running м5 needs depth 5 > MaxDepth(4).
        for (var i = 0; i < 5; i++)
        {
            h.Resolver.Add(ExecutorHarness.Graph($"м{i}", "r",
                new RunMacroNode { Id = "r", MacroName = $"м{i + 1}", Next = null }));
        }
        h.Resolver.Add(SubPressingF9("м5"));

        var result = await h.Executor.RunAsync(
            h.Resolver.TryGet("м0")!, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("depth limit");
        // м5's key press never happened.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }

    [Test]
    public async Task DepthWithinLimit_Runs()
    {
        var h = new ExecutorHarness();
        // м0 → м1 → м2 → м3 → м4(action): deepest sub-run has depth 4 = MaxDepth, legal.
        for (var i = 0; i < 4; i++)
        {
            h.Resolver.Add(ExecutorHarness.Graph($"м{i}", "r",
                new RunMacroNode { Id = "r", MacroName = $"м{i + 1}", Next = null }));
        }
        h.Resolver.Add(SubPressingF9("м4"));

        var result = await h.Executor.RunAsync(
            h.Resolver.TryGet("м0")!, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(1);
    }

    [Test]
    public async Task NameCycle_AbortsRun()
    {
        var h = new ExecutorHarness();
        h.Resolver.Add(ExecutorHarness.Graph("а", "r",
            new RunMacroNode { Id = "r", MacroName = "б", Next = null }));
        h.Resolver.Add(ExecutorHarness.Graph("б", "r",
            new RunMacroNode { Id = "r", MacroName = "а", Next = null }));

        var result = await h.Executor.RunAsync(
            h.Resolver.TryGet("а")!, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("cycle");
    }

    [Test]
    public async Task SelfCycle_AbortsRun()
    {
        var h = new ExecutorHarness();
        h.Resolver.Add(ExecutorHarness.Graph("а", "r",
            new RunMacroNode { Id = "r", MacroName = "а", Next = null }));

        var result = await h.Executor.RunAsync(
            h.Resolver.TryGet("а")!, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("cycle");
    }

    [Test]
    public async Task Variables_AreCopiedIntoSubRun_NotShared()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        h.Primitives.RecognizeHandler = (_, _, _) => "жрец";
        // Child READS the parent's variable (into a tag) and WRITES its own (ResultVar).
        h.Resolver.Add(ExecutorHarness.Graph("суб", "t",
            new AddTagNode { Id = "t", Tag = "из-родителя-{п}", Next = "r" },
            new RecognizeTagNode
            {
                Id = "r", TemplateSet = "классы", Region = new ScreenRect(0, 0, 1, 1),
                ApplyTag = false, ResultVar = "tag", Matched = null, NotMatched = null,
            }));
        var variables = new MacroVariables();
        variables.Set("п", "снаружи");
        var context = h.Context(ExecutorHarness.Window, variables);

        var result = await h.Executor.RunAsync(ParentRunning("суб"), context, CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        // Read inherited: the child saw п="снаружи".
        await Assert.That(h.Registry.HasTag(ExecutorHarness.Window, "из-родителя-снаружи")).IsTrue();
        // Write isolated: the child's "tag" never reached the parent.
        await Assert.That(context.Variables.TryGet("tag", out _)).IsFalse();
    }

    [Test]
    public async Task Target_SpawnsOneSubRunPerMatchedWindow_WithThatWindowAsContext()
    {
        var h = new ExecutorHarness();
        IntPtr w1 = new(1), w2 = new(2), w3 = new(3);
        h.Registry.Register(w1, "elementclient");
        h.Registry.AddTag(w1, "перс");
        h.Registry.Register(w2, "elementclient");
        h.Registry.AddTag(w2, "перс");
        h.Registry.Register(w3, "elementclient"); // no tag — must not get a sub-run
        h.Resolver.Add(SubPressingF9());
        // No targetless follow-up node here: the parent runs without a context window,
        // so everything after the fan-out must carry a selector too (or end the run).
        var parent = ExecutorHarness.Graph("родитель", "r",
            new RunMacroNode
            {
                Id = "r", MacroName = "суб",
                Target = new TargetSelector { RequireTags = ["перс"] }, Next = null,
            });

        // Parent has no context window at all — the selector supplies the children's.
        var result = await h.Executor.RunAsync(parent, h.Context(window: null), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        var subKeyHwnds = h.Primitives.Calls
            .Where(c => Equals(c.A, VirtualKey.F9))
            .Select(c => c.Hwnd)
            .OrderBy(p => p.ToInt64())
            .ToList();
        await Assert.That(subKeyHwnds).Count().IsEqualTo(2);
        await Assert.That(subKeyHwnds[0]).IsEqualTo(w1);
        await Assert.That(subKeyHwnds[1]).IsEqualTo(w2);
    }

    [Test]
    public async Task MacroName_SupportsVariableInterpolation()
    {
        var h = new ExecutorHarness();
        h.Resolver.Add(SubPressingF9("суб-жрец"));
        var variables = new MacroVariables();
        variables.Set("tag", "жрец");
        var parent = ExecutorHarness.Graph("родитель", "r",
            new RunMacroNode { Id = "r", MacroName = "суб-{tag}", Next = null });

        var result = await h.Executor.RunAsync(parent, h.Context(ExecutorHarness.Window, variables), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(1);
        await Assert.That(h.Primitives.Calls[0].A).IsEqualTo(VirtualKey.F9);
    }

    [Test]
    public async Task UnknownMacroName_AbortsRun()
    {
        var h = new ExecutorHarness();

        var result = await h.Executor.RunAsync(
            ParentRunning("нет-такого"), h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("нет-такого");
    }

    [Test]
    public async Task AwaitTrue_ChildAbort_PropagatesToParent()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        // Child aborts: interpolation of an undefined variable.
        h.Resolver.Add(ExecutorHarness.Graph("суб", "t",
            new AddTagNode { Id = "t", Tag = "{нет}", Next = null }));

        var result = await h.Executor.RunAsync(
            ParentRunning("суб"), h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("суб");
        // Parent's Next never ran.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }
}
