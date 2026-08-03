using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: семантика RunMacroNode — дождаться или запустить и забыть, предел глубины,
// обнаружение цикла по именам, под-прогоны на каждое окно с подошедшим окном в контексте и
// изоляция переменных копированием, а не разделением.
public class RunMacroNodeTests
{
    /// <summary>Под-макрос, нажимающий F9 в своём контекстном окне.</summary>
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

        var result = await h.Executor.RunAsync(ParentRunning("суб"), h.Context(ExecutorHarness.Window),
            CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        // Сперва клавиша под-прогона, потом Next родителя — вот и доказательство, что родитель
        // дождался.
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

        var runTask = h.Executor.RunAsync(ParentRunning("суб"), h.Context(ExecutorHarness.Window),
            CancellationToken.None);
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

        // Собственное нажатие клавиши «после» у родителя тоже идёт через примитив с затвором,
        // поэтому ставим затвор ДО запуска и смотрим на состояние завершения: при Await=false
        // родитель обязан дойти до своей ноды Next (и записать её), даже если потомок висит.
        var runTask = h.Executor.RunAsync(
            ParentRunning("суб", await_: false), h.Context(ExecutorHarness.Window), CancellationToken.None);

        // Родитель завершится, только если он НЕ ждал потомка за затвором… но и его собственное
        // нажатие F1 тоже за затвором. Открываем затвор и проверяем, что оба нажатия дошли, а
        // родитель завершился, — в отличие от теста с Await=true выше, здесь порядок не важен.
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

        // Родитель БЕЗ ноды-действия «после»: его обход вообще не трогает примитив с затвором,
        // так что завершение доказывает именно «запустил и забыл», и ничего кроме.
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
        // м0 → м1 → м2 → м3 → м4 → м5: запуск м5 требует глубины 5 > MaxDepth(4).
        for (var i = 0; i < 5; i++)
        {
            h.Resolver.Add(ExecutorHarness.Graph($"м{i}", "r",
                new RunMacroNode { Id = "r", MacroName = $"м{i + 1}", Next = null }));
        }

        h.Resolver.Add(SubPressingF9("м5"));

        var result = await h.Executor.RunAsync(
            h.Resolver.TryGet("м0")!, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("предел вложенности");
        // Нажатия клавиши в м5 не случилось.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }

    [Test]
    public async Task DepthWithinLimit_Runs()
    {
        var h = new ExecutorHarness();
        // м0 → м1 → м2 → м3 → м4(действие): у самого глубокого под-прогона глубина 4 = MaxDepth,
        // это законно.
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
        await Assert.That(result.Error!).Contains("цикл вызовов");
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
        await Assert.That(result.Error!).Contains("цикл вызовов");
    }

    [Test]
    public async Task Variables_AreCopiedIntoSubRun_NotShared()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        h.Primitives.RecognizeHandler = (_, _, _) => "жрец";
        // Потомок ЧИТАЕТ переменную родителя (в тег) и ПИШЕТ свою собственную (ResultVar).
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
        // Чтение унаследовано: потомок увидел п="снаружи".
        await Assert.That(h.Registry.HasTag(ExecutorHarness.Window, "из-родителя-снаружи")).IsTrue();
        // Запись изолирована: "tag" потомка до родителя не добрался.
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
        // Ноды-продолжения без цели здесь нет: родитель идёт без контекстного окна, так что всё
        // после веера обязано нести селектор тоже (или завершать прогон).
        var parent = ExecutorHarness.Graph("родитель", "r",
            new RunMacroNode
            {
                Id = "r", MacroName = "суб",
                Target = new TargetSelector { RequireTags = ["перс"] }, Next = null,
            });

        // Контекстного окна у родителя нет вовсе — окна потомкам выдаёт селектор.
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

        var result = await h.Executor.RunAsync(parent, h.Context(ExecutorHarness.Window, variables),
            CancellationToken.None);

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
        // Потомок прерывается: подстановка неопределённой переменной.
        h.Resolver.Add(ExecutorHarness.Graph("суб", "t",
            new AddTagNode { Id = "t", Tag = "{нет}", Next = null }));

        var result = await h.Executor.RunAsync(
            ParentRunning("суб"), h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("суб");
        // Next родителя так и не отработал.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }
}
