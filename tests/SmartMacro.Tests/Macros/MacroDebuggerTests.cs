using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// D5: пауза, шаг, «до ноды» и точки останова внутри walker'а.
//
// Набор расширяет тесты исполнителя, а не переписывает их: каждый тест, написанный до D5,
// по-прежнему гоняется с Debugger = null — это путь без отладчика, и именно им демон идёт всякий
// раз, когда панель не подключена. Здесь покрыт второй путь и три опасности, которые он вносит:
//
//   · обход, припаркованный, когда никто не подключён, держал бы свой слот single-flight вечно;
//   · пауза внутри ноды бросила бы разбуженного клиента игры в этом состоянии;
//   · «Стоп» обязан честно говорить, что именно он останавливает — обход или прогон.
//
// Третья — решение уровня протокола и интерфейса (StopMacro отменяет ПРОГОН), и здесь она
// проверяется только как «отмена распускает припаркованный обход».
public class MacroDebuggerTests
{
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

    // Три клавиши — тогда пауза между второй и третьей однозначна.
    private static MacroGraph Chain(string name = "цепочка") => ExecutorHarness.Graph(
        name,
        "a",
        new KeyPressNode { Id = "a", Key = VirtualKey.F1, Next = "b" },
        new KeyPressNode { Id = "b", Key = VirtualKey.F2, Next = "c" },
        new KeyPressNode { Id = "c", Key = VirtualKey.F3 });

    /// <summary>Крутится, пока не выполнится <paramref name="condition"/>; роняет тест, а не виснет навсегда.</summary>
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

    // ---- никто не подключён: обходчик не должен и заметить, что отладчик существует -------

    [Test]
    public async Task WithNoDebuggerAttached_BreakpointsDoNotBite()
    {
        var harness = new ExecutorHarness();
        var session = Session(attached: false);
        session.SetBreakpoints("цепочка", ["b"]);

        // Демон резидентен: точка останова, застопорившая обход, за которым никто не смотрит,
        // заклинила бы хоткей макроса до самого перезапуска. Хранение переживает уход панели,
        // остановка — нет.
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

        await harness.Executor.RunAsync(Chain(), harness.Context(ExecutorHarness.Window, debugger: spy),
            CancellationToken.None);

        // Затвор IsActive и есть вся модель расходов, ровно как IsEnabled у наблюдателя: обход
        // без отладчика не имеет права дотягиваться до блокировки или словаря на каждой ноде.
        await Assert.That(spy.ArmCalls).IsEqualTo(0);
    }

    // ---- точки останова --------------------------------------------------------------------

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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count > 0, "обход должен встать на паузу");

        // ДО, а не во время: отправлена ровно одна клавиша, и это та, что из ноды 'a'. Это
        // опасность номер два, записанная проверкой: нода, на которой обход припаркован, ещё не
        // начиналась, а значит, никто не разбудил окно игры, которое теперь ждало бы человека.
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);
        var paused = observer.OfKind(RecordingObserver.PausedKind).Single();
        await Assert.That(paused.NodeId).IsEqualTo("b");
        await Assert.That(paused.Outcome).IsEqualTo(nameof(DebugPauseReason.Breakpoint));

        // А объявление приходит ПОСЛЕ события входа в ноду, так что к моменту, когда канва скажет
        // «на паузе», она уже подсветила ту коробку, на которой обход и стоит.
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
        // 'b' есть в обоих графах; точка останова принадлежит другому.
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
        // Эргономическая половина решения «сессия, и притом в демоне»: панель может приходить и
        // уходить, прогоны тоже, — а красная точка стоит на месте, пока её кто-нибудь не снимет.
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

            await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count > 0,
                $"попытка {attempt} должна встать на паузу");
            session.Command(await WalkId(observer), DebugCommand.Resume, null);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Assert.That(session.Breakpoints().Single().NodeIds).IsEquivalentTo(new[] { "c" });
    }

    // ---- шаг, «до ноды», пауза ---------------------------------------------------------------

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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде a");
        var walkId = await WalkId(observer);

        session.Command(walkId, DebugCommand.Step, null);

        // Одна нода отработала, и обход снова припаркован — на этот раз из-за шага, а его панель
        // рисует не так, как точку останова.
        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 2, "пауза на ноде b");
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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде a");
        session.Command(await WalkId(observer), DebugCommand.RunToNode, "c");

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 2, "пауза на ноде c");
        var second = observer.OfKind(RecordingObserver.PausedKind)[1];
        await Assert.That(second.NodeId).IsEqualTo("c");
        await Assert.That(second.Outcome).IsEqualTo(nameof(DebugPauseReason.Cursor));
        // 'a' и 'b' отработали обе; 'c' — нет.
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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде a");
        session.Command(await WalkId(observer), DebugCommand.RunToNode, "не-существует");

        // Ни ошибка, ни зависание: ветка, которая туда так и не заходит, — законный исход.
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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде a");
        var walkId = await WalkId(observer);

        var ack = session.Command(walkId, DebugCommand.RunToNode, null);

        await Assert.That(ack.Accepted).IsFalse();
        // По-прежнему припаркован: отказ не распустил его ненароком.
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

        // Нода 'a' стоит, пока мы её не отпустим, — вот в это окно «Пауза» и приходит.
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
        // ЕЩЁ не припаркован: обход внутри ноды, которая может отрабатывать минуту, и панель
        // обязана говорить «пауза…», а не «на паузе», пока событие этого не подтвердит.
        await Assert.That(ack.Paused).IsFalse();
        await Assert.That(ack.PauseRequested).IsTrue();

        harness.Primitives.PressKeyGate = null;
        releaseNode.SetResult();

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на следующей ноде");
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

        // «Accepted = false» — это то, из чего панель узнаёт, что пора гасить кнопку «Пауза» для
        // обхода, закончившегося, пока щелчок был в пути.
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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде b");
        session.Command(await WalkId(observer), DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // На паузе → распущен → нода и правда отрабатывает. Без события о возобновлении панель
        // инструментов твердила бы «на паузе» всё то время, пока идёт нода, которой законно
        // требуется 60 секунд.
        var kinds = observer.Entries.Select(e => $"{e.Kind}:{e.NodeId}").ToList();
        var paused = kinds.IndexOf($"{RecordingObserver.PausedKind}:b");
        var resumed = kinds.IndexOf($"{RecordingObserver.ResumedKind}:b");
        await Assert.That(resumed).IsGreaterThan(paused);
        await Assert.That(kinds.IndexOf($"{Exit}:b")).IsGreaterThan(resumed);
    }

    // ---- опасность 1: припаркованный обход не должен пережить свою аудиторию ---------------

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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде b");

        // Панель упала — или пользователь просто закрыл окно. Нажать «Дальше» нечему.
        session.Release();

        // Обход ИДЁТ ДАЛЬШЕ, а не прерывается: запустили его законно, а брошенный на полпути
        // макрос способен оставить игру в худшем состоянии, чем если бы он доработал.
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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде b");
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

        // На месте и для следующей панели: именно потому, что демон переживает панель, хранения
        // в пределах сессии и достаточно.
        await Assert.That(session.Breakpoints().Single().NodeIds).IsEquivalentTo(new[] { "b" });
    }

    // ---- опасность 3: стоп ---------------------------------------------------------------------

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

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 1, "пауза на ноде b");

        // «■ Стоп» отменяет токен ПРОГОНА, а выключение демона отменяет токены всех прогонов
        // разом. И в том и в другом случае припаркованный обход обязан выйти из затвора, а не
        // держать свой слот single-flight до самой смерти процесса.
        await cts.CancelAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);
    }

    // ---- веер: единица здесь — обход ----------------------------------------------------------

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
            new RunMacroNode
                { Id = "fan", MacroName = "sub", Target = new TargetSelector { RequireTags = ["клиент"] } });
        session.SetBreakpoints("sub", ["press"]);

        var run = harness.Executor.RunAsync(
            parent,
            harness.Context(observer: observer, debugger: session),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count == 2,
            "оба вложенных обхода встали на паузу");
        var parked = observer.OfKind(RecordingObserver.PausedKind);
        await Assert.That(parked.Select(e => e.WalkId).Distinct()).Count().IsEqualTo(2);

        // Распустив один, второй оставляем припаркованным — в этом и смысл выбора обхода:
        // пользователь смотрит на одно из десяти окон и шагает именно им.
        session.Command(parked[0].WalkId, DebugCommand.Resume, null);
        await Task.Delay(120);
        await Assert.That(run.IsCompleted).IsFalse();
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(1);

        session.Command(parked[1].WalkId, DebugCommand.Resume, null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(harness.Primitives.Calls).Count().IsEqualTo(2);
    }

    // ---- сообщения о переменных ----------------------------------------------------------------

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

        // `cursor` не пишет ни одна нода, так что без этого панель переменных всегда показывала бы
        // его как «нет значения» — и это при том, что доступно оно всегда.
        var seed = observer.OfKind(RecordingObserver.VariableKind).Single();
        await Assert.That(seed.Outcome).IsEqualTo("cursor");
        await Assert.That(seed.Detail).IsEqualTo(new ScreenPoint(1804, 902).ToString());
        await Assert.That(seed.NodeId).IsNull();

        // И сообщается оно до первой ноды, так что у обхода, вставшего на паузу на первой же,
        // оно уже есть.
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
        await Assert.That(observer.OutcomesOf(RecordingObserver.VariableKind))
            .IsEquivalentTo(new[] { "tag", "точка" });
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
                new RecognizeTagNode
                    { Id = "r", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1), ApplyTag = false }),
            harness.Context(ExecutorHarness.Window, variables: MacroVariables.ForTrigger(default), observer: observer),
            CancellationToken.None);

        await Assert.That(observer.OfKind(RecordingObserver.VariableKind)).IsEmpty();
    }

    [Test]
    public async Task AVariableWriteStillHappensWhenNobodyIsListening()
    {
        // Сообщение — это трассировка, а присваивание — поведение. Пропускать оба через один
        // помощник безопасно лишь при условии, что затвор закрывает первое и не трогает второе.
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

    /// <summary>Считает вызовы <see cref="IMacroDebugger.Arm"/> — чтобы доказать, что неактивный затвор обходят стороной.</summary>
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
