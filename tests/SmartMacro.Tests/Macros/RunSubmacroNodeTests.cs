using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.Tests.Macros;

// Семантика RunSubmacroNode: дождаться или запустить и забыть, под-прогоны на каждое подошедшее
// окно с этим окном в контексте, изоляция переменных копированием, а не разделением.
//
// Волна F4 сняла отсюда два теста разом — предел вложенности и обнаружение цикла по именам, —
// потому что оба ловили беду, которой больше нет: под-макросы плоские, звать соседа по библиотеке
// нельзя, и цикл стал невозможен ПО ПОСТРОЕНИЮ. На их месте один тест, проверяющий, что вложенный
// вызов (его способна оставить только правка файла руками) обрывает прогон.
public class RunSubmacroNodeTests
{
    /// <summary>Под-макрос, нажимающий F9 в своём контекстном окне.</summary>
    private static MacroGraph SubPressingF9(string name = "суб") =>
        ExecutorHarness.Graph(name, Ids.Of("k"),
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.F9, Next = null });

    private static MacroGraph ParentRunning(Guid submacroId, bool awaited = true, TargetSelector? target = null) =>
        ExecutorHarness.Graph("родитель", Ids.Of("r"),
            new RunSubmacroNode
            {
                Id = Ids.Of("r"), DisplayName = "r", SubmacroId = submacroId, Await = awaited, Target = target,
                Next = Ids.Of("after")
            },
            new KeyPressNode { Id = Ids.Of("after"), DisplayName = "after", Key = VirtualKey.F1, Next = null });

    [Test]
    public async Task AwaitTrue_RunsSubMacroThenContinues()
    {
        var h = new ExecutorHarness();
        var sub = h.AddSubmacro(SubPressingF9());

        var result = await h.Executor.RunAsync(ParentRunning(sub), h.Context(ExecutorHarness.Window),
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
        var sub = h.AddSubmacro(SubPressingF9());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Primitives.PressKeyGate = () =>
        {
            entered.TrySetResult();
            return gate.Task;
        };

        var runTask = h.Executor.RunAsync(ParentRunning(sub), h.Context(ExecutorHarness.Window),
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
        var sub = h.AddSubmacro(SubPressingF9());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Primitives.PressKeyGate = () => gate.Task;

        // Собственное нажатие клавиши «после» у родителя тоже идёт через примитив с затвором,
        // поэтому ставим затвор ДО запуска и смотрим на состояние завершения: при Await=false
        // родитель обязан дойти до своей ноды Next (и записать её), даже если потомок висит.
        var runTask = h.Executor.RunAsync(
            ParentRunning(sub, awaited: false), h.Context(ExecutorHarness.Window), CancellationToken.None);

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
        var sub = h.AddSubmacro(SubPressingF9());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Primitives.PressKeyGate = () => gate.Task;

        // Родитель БЕЗ ноды-действия «после»: его обход вообще не трогает примитив с затвором,
        // так что завершение доказывает именно «запустил и забыл», и ничего кроме.
        var parent = ExecutorHarness.Graph("родитель", Ids.Of("r"),
            new RunSubmacroNode { Id = Ids.Of("r"), DisplayName = "r", SubmacroId = sub, Await = false, Next = null });

        var result = await h.Executor
            .RunAsync(parent, h.Context(ExecutorHarness.Window), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);

        gate.TrySetResult(); // release the detached child before the test ends
    }

    /// <summary>
    /// Плоскость — это ЕДИНСТВЕННАЯ страховка от циклов, оставшаяся после F4, и вот она.
    ///
    /// Валидатор такой граф отвергает ошибкой, так что дойти сюда может только файл, правленный
    /// руками; обрыв прогона — правильный ответ, потому что «функция зовёт функцию» ниоткуда
    /// больше не следует.
    /// </summary>
    [Test]
    public async Task SubmacroCallingSubmacro_AbortsRun()
    {
        var h = new ExecutorHarness();
        var inner = h.AddSubmacro(SubPressingF9("внутренний"));
        var outer = h.AddSubmacro(ExecutorHarness.Graph("внешний", Ids.Of("r2"),
            new RunSubmacroNode { Id = Ids.Of("r2"), DisplayName = "r2", SubmacroId = inner, Next = null }));

        var result = await h.Executor.RunAsync(
            ParentRunning(outer), h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        // Сообщение ДВУСЛОЙНОЕ, и прежнее Contains("вложенность плоская") этого не показывало:
        // снаружи — «родитель оборвался из-за функции», внутри — сама причина. Разбираются оба
        // слоя, потому что склеить их обратно в одну проверку значит снова не знать, который врёт.
        var aborted = Msg.Args(result.Error, Strings.Run_Abort_SubmacroAborted);
        await Assert.That(aborted[0]).IsEqualTo("родитель");
        await Assert.That(aborted[1]).IsEqualTo("внешний");
        await Assert.That(Msg.Args(aborted[2], Strings.Run_Abort_NestedSubmacro))
            .IsEquivalentTo(new[] { "родитель", "внешний" });
        // До нажатия клавиши во внутреннем не дошло.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Variables_AreCopiedIntoSubRun_NotShared()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        h.Primitives.RecognizeHandler = (_, _, _) => "жрец";
        // Потомок ЧИТАЕТ переменную родителя (в тег) и ПИШЕТ свою собственную (ResultVar).
        var sub = h.AddSubmacro(ExecutorHarness.Graph("суб", Ids.Of("t"),
            new AddTagNode { Id = Ids.Of("t"), DisplayName = "t", Tag = "из-родителя-{п}", Next = Ids.Of("r") },
            new MatchTemplateSetNode
            {
                Id = Ids.Of("r"), DisplayName = "r", TemplateSet = "классы", Region = new ScreenRect(0, 0, 1, 1),
                ResultVar = "tag", Matched = null, NotMatched = null,
            }));
        var variables = new MacroVariables();
        variables.Set("п", "снаружи");
        var context = h.Context(ExecutorHarness.Window, variables);

        var result = await h.Executor.RunAsync(ParentRunning(sub), context, CancellationToken.None);

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
        var sub = h.AddSubmacro(SubPressingF9());
        // Ноды-продолжения без цели здесь нет: родитель идёт без контекстного окна, так что всё
        // после веера обязано нести селектор тоже (или завершать прогон).
        var parent = ExecutorHarness.Graph("родитель", Ids.Of("r"),
            new RunSubmacroNode
            {
                Id = Ids.Of("r"), DisplayName = "r", SubmacroId = sub,
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

    /// <summary>
    /// Ссылка на под-макрос, которого в бандле нет, ОБРЫВАЕТ прогон, а не уходит куда-то ещё: у
    /// вызова нет ветки «не найдено», и притвориться, что функция отработала, было бы враньём.
    /// </summary>
    [Test]
    public async Task UnknownSubmacro_AbortsRun()
    {
        var h = new ExecutorHarness();

        var result = await h.Executor.RunAsync(
            ParentRunning(Guid.NewGuid()), h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        // Названы макрос и действие вызова — по ним ноду и искать в редакторе.
        await Assert.That(Msg.Args(result.Error, Strings.Run_Abort_SubmacroNotFound))
            .IsEquivalentTo(new[] { "родитель", "r" });
    }

    [Test]
    public async Task AwaitTrue_ChildAbort_PropagatesToParent()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        // Потомок прерывается: подстановка неопределённой переменной.
        var sub = h.AddSubmacro(ExecutorHarness.Graph("суб", Ids.Of("t"),
            new AddTagNode { Id = Ids.Of("t"), DisplayName = "t", Tag = "{нет}", Next = null }));

        var result = await h.Executor.RunAsync(
            ParentRunning(sub), h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        // Родитель называет функцию И ПЕРЕДАЁТ ЕЁ ПРИЧИНУ: «оборван» без причины отправил бы
        // читателя искать её в другом месте. Раньше здесь сверялось одно слово «суб», которое
        // прошло бы и на сообщении вовсе без причины.
        var aborted = Msg.Args(result.Error, Strings.Run_Abort_SubmacroAborted);
        await Assert.That(aborted[0]).IsEqualTo("родитель");
        await Assert.That(aborted[1]).IsEqualTo("суб");
        await Assert.That(Msg.Arg(aborted[2], Strings.Run_Variable_NotDefined)).IsEqualTo("нет");
        // Next родителя так и не отработал.
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }
}
