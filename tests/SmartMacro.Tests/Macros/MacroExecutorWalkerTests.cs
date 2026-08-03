using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: сам обходчик графа — прохождение нода за нодой, рёбра исходов, обрыв на пустом ребре,
// веер по селектору, подстановка контекстного окна по умолчанию и пути прерывания и отмены.
public class MacroExecutorWalkerTests
{
    [Test]
    public async Task Chain_ExecutesNodesInEdgeOrder()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("цепочка", "k1",
            new KeyPressNode { Id = "k1", Key = VirtualKey.F1, Next = "d" },
            new DelayNode { Id = "d", Ms = 0, Next = "k2" },
            new KeyPressNode { Id = "k2", Key = VirtualKey.F2, Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(2);
        await Assert.That(h.Primitives.Calls[0])
            .IsEqualTo(new RecordingPrimitives.Call("PressKey", ExecutorHarness.Window, VirtualKey.F1));
        await Assert.That(h.Primitives.Calls[1])
            .IsEqualTo(new RecordingPrimitives.Call("PressKey", ExecutorHarness.Window, VirtualKey.F2));
    }

    [Test]
    public async Task NodeEnteredHook_ReportsEveryNodeInOrder()
    {
        var h = new ExecutorHarness();
        var entered = new List<string>();
        var graph = ExecutorHarness.Graph("м", "k1",
            new KeyPressNode { Id = "k1", Key = VirtualKey.F1, Next = "k2" },
            new KeyPressNode { Id = "k2", Key = VirtualKey.F2, Next = null });

        await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window, onNodeEntered: entered.Add),
            CancellationToken.None);

        await Assert.That(string.Join(",", entered)).IsEqualTo("k1,k2");
    }

    [Test]
    public async Task FindElement_Found_TakesFoundEdge_AndWritesPointVariable()
    {
        var h = new ExecutorHarness();
        h.Primitives.FindHandler = (_, _, _) => new ScreenPoint(50, 60);
        var context = h.Context(ExecutorHarness.Window);
        var graph = ExecutorHarness.Graph("м", "f",
            new FindElementNode
                { Id = "f", Template = "кнопка", FoundPointVar = "btn", Found = "yes", NotFound = "no" },
            new KeyPressNode { Id = "yes", Key = VirtualKey.F1, Next = null },
            new KeyPressNode { Id = "no", Key = VirtualKey.F2, Next = null });

        var result = await h.Executor.RunAsync(graph, context, CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls[1].A).IsEqualTo(VirtualKey.F1);
        await Assert.That(context.Variables.GetPoint("btn")).IsEqualTo(new ScreenPoint(50, 60));
    }

    [Test]
    public async Task FindElement_NotFound_TakesNotFoundEdge_AndWritesNothing()
    {
        var h = new ExecutorHarness();
        var context = h.Context(ExecutorHarness.Window);
        var graph = ExecutorHarness.Graph("м", "f",
            new FindElementNode
                { Id = "f", Template = "кнопка", FoundPointVar = "btn", Found = "yes", NotFound = "no" },
            new KeyPressNode { Id = "yes", Key = VirtualKey.F1, Next = null },
            new KeyPressNode { Id = "no", Key = VirtualKey.F2, Next = null });

        var result = await h.Executor.RunAsync(graph, context, CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls[1].A).IsEqualTo(VirtualKey.F2);
        await Assert.That(context.Variables.TryGet("btn", out _)).IsFalse();
    }

    [Test]
    public async Task WaitForElement_Timeout_TakesTimeoutEdge_NullEdgeEndsRun()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", "w",
            new WaitForElementNode { Id = "w", Template = "мир", TimeoutMs = 5000, Found = "yes", Timeout = null },
            new KeyPressNode { Id = "yes", Key = VirtualKey.F1, Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        // Только вызов Wait: ребро Timeout пустое, так что прогон на нём и кончился.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(1);
        await Assert.That(h.Primitives.Calls[0].Op).IsEqualTo("Wait");
    }

    [Test]
    public async Task WaitForElement_Found_WritesPointVariable()
    {
        var h = new ExecutorHarness();
        h.Primitives.WaitHandler = (_, _, _, _) => new ScreenPoint(7, 8);
        var context = h.Context(ExecutorHarness.Window);
        var graph = ExecutorHarness.Graph("м", "w",
            new WaitForElementNode
                { Id = "w", Template = "мир", TimeoutMs = 5000, FoundPointVar = "pt", Found = null, Timeout = null });

        var result = await h.Executor.RunAsync(graph, context, CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(context.Variables.GetPoint("pt")).IsEqualTo(new ScreenPoint(7, 8));
    }

    [Test]
    public async Task RecognizeTag_Matched_WritesVariable_AppliesTag_TakesMatchedEdge()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        h.Primitives.RecognizeHandler = (_, _, _) => "виз";
        var context = h.Context(ExecutorHarness.Window);
        var graph = ExecutorHarness.Graph("м", "r",
            new RecognizeTagNode
            {
                Id = "r", TemplateSet = "классы", Region = new ScreenRect(0, 0, 10, 10),
                ResultVar = "класс", Matched = "hit", NotMatched = "miss",
            },
            new KeyPressNode { Id = "hit", Key = VirtualKey.F1, Next = null },
            new KeyPressNode { Id = "miss", Key = VirtualKey.F2, Next = null });

        var result = await h.Executor.RunAsync(graph, context, CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls[1].A).IsEqualTo(VirtualKey.F1);
        await Assert.That(context.Variables.Get("класс")).IsEqualTo((VariableValue)"виз");
        await Assert.That(h.Registry.HasTag(ExecutorHarness.Window, "виз")).IsTrue();
    }

    [Test]
    public async Task RecognizeTag_NotMatched_NoTagNoVariable_TakesNotMatchedEdge()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        var context = h.Context(ExecutorHarness.Window);
        var graph = ExecutorHarness.Graph("м", "r",
            new RecognizeTagNode
            {
                Id = "r", TemplateSet = "классы", Region = new ScreenRect(0, 0, 10, 10),
                Matched = "hit", NotMatched = null,
            },
            new KeyPressNode { Id = "hit", Key = VirtualKey.F1, Next = null });

        var result = await h.Executor.RunAsync(graph, context, CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(1);
        await Assert.That(context.Variables.TryGet("tag", out _)).IsFalse();
        await Assert.That(h.Registry.GetTags(ExecutorHarness.Window)).Count().IsEqualTo(0);
    }

    [Test]
    public async Task RecognizeTag_ApplyTagFalse_WritesVariableButNoTag()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        h.Primitives.RecognizeHandler = (_, _, _) => "жрец";
        var context = h.Context(ExecutorHarness.Window);
        var graph = ExecutorHarness.Graph("м", "r",
            new RecognizeTagNode
            {
                Id = "r", TemplateSet = "классы", Region = new ScreenRect(0, 0, 10, 10),
                ApplyTag = false, Matched = null, NotMatched = null,
            });

        await h.Executor.RunAsync(graph, context, CancellationToken.None);

        await Assert.That(context.Variables.Get("tag")).IsEqualTo((VariableValue)"жрец");
        await Assert.That(h.Registry.GetTags(ExecutorHarness.Window)).Count().IsEqualTo(0);
    }

    [Test]
    public async Task FanOut_HitsEveryMatchedWindowExactlyOnce_WithoutNeedingContext()
    {
        var h = new ExecutorHarness();
        IntPtr w1 = new(1), w2 = new(2), w3 = new(3);
        h.Registry.Register(w1, "elementclient");
        h.Registry.AddTag(w1, "перс");
        h.Registry.Register(w2, "elementclient");
        h.Registry.AddTag(w2, "перс");
        h.Registry.Register(w3, "elementclient");
        var graph = ExecutorHarness.Graph("м", "k",
            new KeyPressNode
            {
                Id = "k", Key = VirtualKey.F8,
                Target = new TargetSelector { RequireTags = ["перс"] }, Next = null,
            });

        // ContextWindow намеренно пуст: действиям, нацеленным селектором, он не нужен.
        var result = await h.Executor.RunAsync(graph, h.Context(window: null), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        var hwnds = h.Primitives.Calls.Select(c => c.Hwnd).OrderBy(p => p.ToInt64()).ToList();
        await Assert.That(hwnds).Count().IsEqualTo(2);
        await Assert.That(hwnds[0]).IsEqualTo(w1);
        await Assert.That(hwnds[1]).IsEqualTo(w2);
    }

    [Test]
    public async Task FanOut_ZeroMatches_IsNoOp_RunContinues()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", "k",
            new KeyPressNode
            {
                Id = "k", Key = VirtualKey.F8,
                Target = new TargetSelector { RequireTags = ["нет-таких"] }, Next = "k2",
            },
            new KeyPressNode { Id = "k2", Key = VirtualKey.F9, Target = new TargetSelector(), Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(window: null), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }

    [Test]
    public async Task NoTarget_DefaultsToContextWindow()
    {
        var h = new ExecutorHarness();
        // Другие окна есть — действие без цели НЕ имеет права разворачиваться веером на них.
        h.Registry.Register(new IntPtr(1), "elementclient");
        var graph = ExecutorHarness.Graph("м", "k",
            new KeyPressNode { Id = "k", Key = VirtualKey.F8, Next = null });

        await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(1);
        await Assert.That(h.Primitives.Calls[0].Hwnd).IsEqualTo(ExecutorHarness.Window);
    }

    [Test]
    public async Task NoTargetNoContext_AbortsWithoutTouchingPrimitives()
    {
        // Подделка FakeItEasy: самый чистый способ проверить, что не звали вообще ничего.
        var primitives = A.Fake<IMacroPrimitives>();
        var h = new ExecutorHarness();
        var executor = new MacroExecutor(primitives, h.Registry, h.Resolver, NullLogger<MacroExecutor>.Instance);
        var graph = ExecutorHarness.Graph("м", "k",
            new KeyPressNode { Id = "k", Key = VirtualKey.F8, Next = null });

        var result = await executor.RunAsync(graph, h.Context(window: null), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("нет контекстного окна");
        A.CallTo(primitives).MustNotHaveHappened();
    }

    [Test]
    public async Task ConditionalWithoutContext_Aborts()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", "f",
            new FindElementNode { Id = "f", Template = "кнопка", Found = null, NotFound = null });

        var result = await h.Executor.RunAsync(graph, h.Context(window: null), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("нужно контекстное окно");
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }

    [Test]
    public async Task EdgeToUnknownNode_Aborts()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", "k",
            new KeyPressNode { Id = "k", Key = VirtualKey.F8, Next = "призрак" });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("призрак");
    }

    [Test]
    public async Task DuplicateNodeIds_Abort()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", "k",
            new KeyPressNode { Id = "k", Key = VirtualKey.F8, Next = null },
            new DelayNode { Id = "k", Ms = 1, Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("дубликат id ноды");
    }

    [Test]
    public async Task AddAndRemoveTagNodes_MutateRegistry()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        h.Registry.AddTag(ExecutorHarness.Window, "старый");
        var graph = ExecutorHarness.Graph("м", "add",
            new AddTagNode { Id = "add", Tag = "новый", Next = "rm" },
            new RemoveTagNode { Id = "rm", Tag = "старый", Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Registry.HasTag(ExecutorHarness.Window, "новый")).IsTrue();
        await Assert.That(h.Registry.HasTag(ExecutorHarness.Window, "старый")).IsFalse();
    }

    [Test]
    public async Task Cancellation_DuringDelay_EndsRunSilently()
    {
        var h = new ExecutorHarness();
        using var cts = new CancellationTokenSource();
        var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = h.Context(ExecutorHarness.Window, onNodeEntered: id =>
        {
            if (id == "d")
            {
                enteredDelay.TrySetResult();
            }
        });
        var graph = ExecutorHarness.Graph("м", "d",
            new DelayNode { Id = "d", Ms = 60_000, Next = "k" },
            new KeyPressNode { Id = "k", Key = VirtualKey.F8, Next = null });

        var runTask = h.Executor.RunAsync(graph, context, cts.Token);
        await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var result = await runTask;

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }
}
