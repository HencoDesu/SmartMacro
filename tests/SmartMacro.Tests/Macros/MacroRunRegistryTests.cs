using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;

namespace SmartMacro.Tests.Macros;

// W0.2a: the run registry — single-flight per macro name, Stop/StopAll cancelling live
// executor runs, snapshots, and RunsChanged notifications.
public class MacroRunRegistryTests
{
    private static MacroRunRegistry NewRegistry() => new(NullLogger<MacroRunRegistry>.Instance);

    [Test]
    public async Task TryBegin_TracksTheRun()
    {
        using var registry = NewRegistry();

        var handle = registry.TryBegin("иммунка");

        await Assert.That(handle).IsNotNull();
        var snapshot = registry.Snapshot();
        await Assert.That(snapshot).Count().IsEqualTo(1);
        await Assert.That(snapshot[0].MacroName).IsEqualTo("иммунка");
        await Assert.That(snapshot[0].RunId).IsEqualTo(handle!.RunId);
        await Assert.That(snapshot[0].CurrentNodeId).IsNull();
    }

    [Test]
    public async Task TryBegin_IsSingleFlightPerName()
    {
        using var registry = NewRegistry();

        var first = registry.TryBegin("иммунка");
        var duplicate = registry.TryBegin("иммунка");
        var other = registry.TryBegin("ассист");

        await Assert.That(first).IsNotNull();
        await Assert.That(duplicate).IsNull();
        await Assert.That(other).IsNotNull();
        await Assert.That(registry.Snapshot()).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Complete_RemovesRun_AndAllowsRestart()
    {
        using var registry = NewRegistry();
        var handle = registry.TryBegin("иммунка")!;

        var completed = registry.Complete(handle.RunId);

        await Assert.That(completed).IsTrue();
        await Assert.That(registry.Snapshot()).Count().IsEqualTo(0);
        await Assert.That(registry.TryBegin("иммунка")).IsNotNull();
    }

    [Test]
    public async Task Complete_UnknownRunId_ReturnsFalse()
    {
        using var registry = NewRegistry();

        await Assert.That(registry.Complete(Guid.NewGuid())).IsFalse();
    }

    [Test]
    public async Task StopAsync_UnknownRunId_CompletesImmediately()
    {
        using var registry = NewRegistry();

        await registry.StopAsync(Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task CurrentNodeId_UpdatesFlowIntoSnapshots()
    {
        using var registry = NewRegistry();
        var handle = registry.TryBegin("иммунка")!;

        handle.CurrentNodeId = "delay-1";

        await Assert.That(registry.Snapshot()[0].CurrentNodeId).IsEqualTo("delay-1");
    }

    [Test]
    public async Task RunsChanged_FiresOnBeginAndComplete_NotOnRejectedDuplicate()
    {
        using var registry = NewRegistry();
        var events = 0;
        registry.RunsChanged += () => Interlocked.Increment(ref events);

        var handle = registry.TryBegin("иммунка")!;
        registry.TryBegin("иммунка"); // rejected — no event
        registry.Complete(handle.RunId);

        await Assert.That(events).IsEqualTo(2);
    }

    [Test]
    public async Task StopAsync_CancelsADelayBlockedRun()
    {
        using var registry = NewRegistry();
        var h = new ExecutorHarness();
        var handle = registry.TryBegin("длинный")!;
        var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = ExecutorHarness.Graph("длинный", "d",
            new DelayNode { Id = "d", Ms = 60_000, Next = null });
        var context = h.Context(ExecutorHarness.Window, onNodeEntered: id =>
        {
            handle.CurrentNodeId = id;
            enteredDelay.TrySetResult();
        });

        // The caller pattern the registry is designed for: run bracketed by TryBegin/Complete.
        var runTask = Task.Run(async () =>
        {
            try
            {
                return await h.Executor.RunAsync(graph, context, handle.Token);
            }
            finally
            {
                registry.Complete(handle.RunId);
            }
        });

        await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(registry.Snapshot()[0].CurrentNodeId).IsEqualTo("d");

        // StopAsync resolves only after the runner acknowledged via Complete.
        await registry.StopAsync(handle.RunId).WaitAsync(TimeSpan.FromSeconds(5));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(registry.Snapshot()).Count().IsEqualTo(0);
    }

    [Test]
    public async Task StopAllAsync_CancelsEveryRun()
    {
        using var registry = NewRegistry();
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("общий", "d",
            new DelayNode { Id = "d", Ms = 60_000, Next = null });

        var runTasks = new List<Task<MacroRunResult>>();
        var enteredCount = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var name in new[] { "первый", "второй" })
        {
            var handle = registry.TryBegin(name)!;
            var context = h.Context(ExecutorHarness.Window, onNodeEntered: _ =>
            {
                if (Interlocked.Increment(ref enteredCount) == 2)
                {
                    bothEntered.TrySetResult();
                }
            });
            runTasks.Add(Task.Run(async () =>
            {
                try
                {
                    return await h.Executor.RunAsync(graph, context, handle.Token);
                }
                finally
                {
                    registry.Complete(handle.RunId);
                }
            }));
        }

        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await registry.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var results = await Task.WhenAll(runTasks).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results[0].Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(results[1].Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(registry.Snapshot()).Count().IsEqualTo(0);
    }
}
