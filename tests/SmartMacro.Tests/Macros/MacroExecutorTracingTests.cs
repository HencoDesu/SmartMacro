using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// D3b: канал прогресса обходчика.
//
// Набор расширяет тесты обходчика, ничего в них не заменяя: каждый уже написанный тест
// по-прежнему идёт с Observer = null — это путь без трассировки, и именно им демон идёт всякий
// раз, когда никакая панель не смотрит. Здесь покрыт второй путь: о чём сообщают, что такое
// ОБХОД (в отличие от прогона) и та гарантия, на которой держится вся конструкция, — что демон,
// за которым не наблюдают, не платит за это ничем.
public class MacroExecutorTracingTests
{
    private const string Enter = "enter";
    private const string Exit = "exit";
    private const string WalkStart = "walk";
    private const string WalkEnd = "end";

    // ---- гарантия от затопления --------------------------------------------------------

    [Test]
    public async Task WithNobodyListening_NoNodeEventsAreProduced()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver { IsEnabled = false };
        var graph = ExecutorHarness.Graph(
            "тихо",
            Ids.Of("a"),
            new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 0, Next = Ids.Of("b") },
            new KeyPressNode { Id = Ids.Of("b"), DisplayName = "b", Key = VirtualKey.C });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer),
            CancellationToken.None);

        // Это несущая проверка всей волны: демон резидентен, а панель нет, — значит, обычный
        // случай обязан не стоить ничего на горячем пути обходчика.
        await Assert.That(observer.OfKind(Enter)).IsEmpty();
        await Assert.That(observer.OfKind(Exit)).IsEmpty();

        // Пара событий уровня обхода срабатывает всё равно — это дважды за ПРОГОН, и именно она
        // позволяет панели, подключившейся посреди прогона, вообще узнать, что прогон есть.
        await Assert.That(observer.OfKind(WalkStart)).Count().IsEqualTo(1);
        await Assert.That(observer.OfKind(WalkEnd)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ARunWithNoObserverAtAllStillWalksTheGraph()
    {
        var harness = new ExecutorHarness();
        var graph = ExecutorHarness.Graph(
            "без-наблюдателя",
            Ids.Of("a"),
            new KeyPressNode { Id = Ids.Of("a"), DisplayName = "a", Key = VirtualKey.F1 });

        var result =
            await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(harness.Primitives.Calls.Select(call => call.Op)).IsEquivalentTo(new[] { "PressKey" });
    }

    // ---- о чём сообщает нода -----------------------------------------------------------

    [Test]
    public async Task EveryNodeReportsAnEnterAndAnExit_InWalkOrder()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var graph = ExecutorHarness.Graph(
            "цепочка",
            Ids.Of("a"),
            new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 0, Next = Ids.Of("b") },
            new ClickNode { Id = Ids.Of("b"), DisplayName = "b", Point = new ScreenPoint(1192, 1805), Next = Ids.Of("c") },
            new KeyPressNode { Id = Ids.Of("c"), DisplayName = "c", Key = VirtualKey.C });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer),
            CancellationToken.None);

        await Assert.That(observer.Entries.Select(e => $"{e.Kind}:{e.NodeName}")).IsEquivalentTo(new[]
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
            Ids.Of("click"),
            new ClickNode { Id = Ids.Of("click"), DisplayName = "click", Point = new ScreenPoint(1192, 1805), Next = Ids.Of("key") },
            new KeyPressNode { Id = Ids.Of("key"), DisplayName = "key", Key = VirtualKey.C, Next = Ids.Of("wait") },
            new DelayNode { Id = Ids.Of("wait"), DisplayName = "wait", Ms = 500 });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer),
            CancellationToken.None);

        var details = observer.OfKind(Exit).ToDictionary(e => e.NodeName!, e => e.Detail);
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
            Ids.Of("a"),
            new KeyPressNode
                { Id = Ids.Of("a"), DisplayName = "a", Key = VirtualKey.F1, Target = new TargetSelector { RequireTags = ["Жрец"] }, Next = Ids.Of("b") },
            new KeyPressNode
                { Id = Ids.Of("b"), DisplayName = "b", Key = VirtualKey.F2, Target = new TargetSelector { RequireTags = ["Оборотень"] } });

        await harness.Executor.RunAsync(graph, harness.Context(observer: observer), CancellationToken.None);

        var details = observer.OfKind(Exit).ToDictionary(e => e.NodeName!, e => e.Detail);
        await Assert.That(details["a"]).IsEqualTo("F1 ×2");
        // Ноль совпадений — законное «ничего не делаем», и увидеть это можно только в логе.
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
            Ids.Of("found"),
            new WaitForElementNode { Id = Ids.Of("found"), DisplayName = "found", Template = "ChatPanelButtons", TimeoutMs = 1, Found = Ids.Of("lost") },
            new WaitForElementNode { Id = Ids.Of("lost"), DisplayName = "lost", Template = "Nope", TimeoutMs = 1, Timeout = Ids.Of("tag") },
            new RecognizeTagNode { Id = Ids.Of("tag"), DisplayName = "tag", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1) });

        await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer),
            CancellationToken.None);

        var exits = observer.OfKind(Exit);
        await Assert.That(observer.OutcomesOf(Exit)).IsEquivalentTo(new[]
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
        // Ни контекстного окна, ни селектора — обходчик прерывается прямо внутри этой ноды.
        var graph = ExecutorHarness.Graph("падение", Ids.Of("a"), new KeyPressNode { Id = Ids.Of("a"), DisplayName = "a", Key = VirtualKey.C });

        var result =
            await harness.Executor.RunAsync(graph, harness.Context(observer: observer), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        var exit = observer.OfKind(Exit).Single();
        await Assert.That(exit.NodeName).IsEqualTo("a");
        await Assert.That(exit.Outcome).IsEqualTo(RunOutcomes.Error);
        await Assert.That(exit.Detail).Contains("нет селектора Target");
        await Assert.That(observer.OfKind(WalkEnd).Single().Outcome).IsEqualTo(RunOutcomes.Aborted);
    }

    [Test]
    public async Task ACancelledRunEndsAsCancelled_NotAborted()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        using var cts = new CancellationTokenSource();
        harness.Primitives.PressKeyGate = async () => { await cts.CancelAsync(); };

        var graph = ExecutorHarness.Graph(
            "отмена",
            Ids.Of("a"),
            new KeyPressNode { Id = Ids.Of("a"), DisplayName = "a", Key = VirtualKey.C, Next = Ids.Of("b") },
            new DelayNode { Id = Ids.Of("b"), DisplayName = "b", Ms = 5000 });

        var result = await harness.Executor.RunAsync(graph, harness.Context(ExecutorHarness.Window, observer: observer),
            cts.Token);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);
        await Assert.That(observer.OfKind(WalkEnd).Single().Outcome).IsEqualTo(RunOutcomes.Cancelled);
    }

    // ---- обходы против прогонов ----------------------------------------------------------

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

        var one = harness.AddSubmacro(ExecutorHarness.Graph(
            "pw-identify-one",
            Ids.Of("press"),
            new KeyPressNode { Id = Ids.Of("press"), DisplayName = "press", Key = VirtualKey.C }));
        var parent = ExecutorHarness.Graph(
            "pw-identify",
            Ids.Of("fan"),
            new RunSubmacroNode
            {
                Id = Ids.Of("fan"), DisplayName = "fan",
                SubmacroId = one,
                Target = new TargetSelector { RequireTags = ["клиент"] },
            });

        await harness.Executor.RunAsync(parent, harness.Context(observer: observer, runId: runId),
            CancellationToken.None);

        // Четыре обхода: родитель плюс по одному на окно. Ровно поэтому панель и следит за
        // ОБХОДОМ: следи она за прогоном — три окна дрались бы за одну подсветку.
        var walks = observer.Walks;
        await Assert.That(walks).Count().IsEqualTo(4);
        await Assert.That(walks.Select(w => w.WalkId).Distinct()).Count().IsEqualTo(4);
        await Assert.That(walks.All(w => w.RunId == runId)).IsTrue();

        await Assert.That(walks[0].MacroName).IsEqualTo("pw-identify");
        await Assert.That(walks[0].Depth).IsEqualTo(0);
        await Assert.That(walks[0].ContextWindow).IsNull();

        var children = walks.Skip(1).ToList();
        // Обход функции докладывает о себе МАКРОСОМ, а подпись функции едет отдельным полем
        // (F4): по имени макроса панель отбирает обходы открытого макроса, а по подписи их
        // различает в переключателе.
        await Assert.That(children.All(w => w.MacroName == "pw-identify")).IsTrue();
        await Assert.That(children.All(w => w.SubmacroName == "pw-identify-one")).IsTrue();
        await Assert.That(children.All(w => w.SubmacroId == one)).IsTrue();
        await Assert.That(children.All(w => w.Depth == 1)).IsTrue();
        await Assert.That(children.Select(w => w.ContextWindow!.Value.ToInt64()).OrderBy(h => h))
            .IsEquivalentTo(new long[] { 0x11, 0x12, 0x13 });

        // А собственная строка ноды родителя говорит, на кого он развернулся веером.
        await Assert.That(observer.OfKind(Exit).Single(e => e.NodeName == "fan").Detail)
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

        var sub = harness.AddSubmacro(ExecutorHarness.Graph(
            "sub",
            Ids.Of("press"),
            new KeyPressNode { Id = Ids.Of("press"), DisplayName = "press", Key = VirtualKey.C }));
        var parent = ExecutorHarness.Graph(
            "parent",
            Ids.Of("fan"),
            new RunSubmacroNode
                { Id = Ids.Of("fan"), DisplayName = "fan", SubmacroId = sub, Target = new TargetSelector { RequireTags = ["клиент"] } });

        await harness.Executor.RunAsync(parent, harness.Context(observer: observer), CancellationToken.None);

        // Сообщено о двух нодах 'press' под РАЗНЫМИ id обходов — иначе панель не отличила бы друг
        // от друга десять загрузок партии.
        var presses = observer.OfKind(Enter).Where(e => e.NodeName == "press").ToList();
        await Assert.That(presses).Count().IsEqualTo(2);
        await Assert.That(presses.Select(e => e.WalkId).Distinct()).Count().IsEqualTo(2);
    }

    [Test]
    public async Task ASubMacroRunReportsItsOwnWalkEvenWhenNothingIsListening()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver { IsEnabled = false };
        var sub = harness.AddSubmacro(ExecutorHarness.Graph("sub", Ids.Of("press"),
            new KeyPressNode { Id = Ids.Of("press"), DisplayName = "press", Key = VirtualKey.C }));
        var parent = ExecutorHarness.Graph("parent", Ids.Of("call"), new RunSubmacroNode { Id = Ids.Of("call"), DisplayName = "call", SubmacroId = sub });

        await harness.Executor.RunAsync(parent, harness.Context(ExecutorHarness.Window, observer: observer),
            CancellationToken.None);

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
            Ids.Of("a"),
            new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 0, Next = Ids.Of("b") },
            new DelayNode { Id = Ids.Of("b"), DisplayName = "b", Ms = 0 });

        await harness.Executor.RunAsync(
            graph,
            harness.Context(ExecutorHarness.Window, onNodeEntered: entered.Add, observer: observer),
            CancellationToken.None);

        // Крючок текущей ноды у реестра старше D3b и по-прежнему питает «Прогоны»; эти два канала
        // намеренно независимы.
        await Assert.That(entered).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(observer.NodeIdsOf(Enter)).IsEquivalentTo(new[] { "a", "b" });
    }

    // ---- учёт обхода переживает ЛЮБОЕ исключение -------------------------------------------

    /// <summary>
    /// Граф с ПУСТЫМ тегом — живой пример четвёртого исключения: <c>WindowRegistry.AddTag</c>
    /// начинается с <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>, а исполнитель ловит ровно три
    /// исключения, и <see cref="ArgumentException"/> среди них нет.
    ///
    /// Панель заводит ноду «Добавить тег» именно с пустым тегом и своим <c>SaveAsync</c> такой
    /// макрос не запишет — но правило живёт только у неё в view-model'и: общий
    /// <c>MacroGraphValidator</c> пустого тега не проверяет вовсе, так что бандл, попавший в
    /// <c>macros/</c> импортом или правкой руками, демон вооружит и запустит.
    /// </summary>
    private static MacroGraph EmptyTagGraph() => ExecutorHarness.Graph(
        "пустой-тег",
        Ids.Of("a"),
        new AddTagNode { Id = Ids.Of("a"), DisplayName = "a", Tag = string.Empty });

    [Test]
    public async Task AnUnexpectedExceptionStillClosesTheWalk()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();

        // Исключение по-прежнему летит наружу: его ловит Orchestrator.RunAsync и пишет как БАГ.
        // Превращать его в обычный «обрыв» значило бы стереть разницу между ошибкой автора графа и
        // ошибкой в движке.
        await Assert.That(async () => await harness.Executor.RunAsync(
                EmptyTagGraph(),
                harness.Context(ExecutorHarness.Window, observer: observer),
                CancellationToken.None))
            .Throws<ArgumentException>();

        // А вот это — сам дефект: RunEventPublisher снимает запись о живом обходе только по
        // WalkFinished, поэтому обход, потерянный на непойманном исключении, оставался «живым» до
        // перезапуска демона. Каждая следующая подписка панели получала фантом, переключатель
        // показывал его идущим, конца по нему не приходило никогда, а список рос с каждым сбоем.
        await Assert.That(observer.OfKind(WalkStart)).Count().IsEqualTo(1);
        await Assert.That(observer.OfKind(WalkEnd)).Count().IsEqualTo(1);
        await Assert.That(observer.OutcomesOf(WalkEnd)).IsEquivalentTo(new[] { RunOutcomes.Aborted });
        // Причина названа: обрыв без объяснения в полосе лога неотличим от «нода ничего не сделала».
        await Assert.That(observer.OfKind(WalkEnd)[0].Detail ?? string.Empty).IsNotEmpty();
    }

    [Test]
    public async Task AnUnexpectedExceptionAlsoDeregistersTheWalkWithTheDebugger()
    {
        var harness = new ExecutorHarness();
        var observer = new RecordingObserver();
        var debugger = new RecordingDebugger();

        await Assert.That(async () => await harness.Executor.RunAsync(
                EmptyTagGraph(),
                harness.Context(ExecutorHarness.Window, observer: observer, debugger: debugger),
                CancellationToken.None))
            .Throws<ArgumentException>();

        // Вторая половина той же бухгалтерии: сессия отладчика держит состояние по обходу и
        // забывает его исключительно здесь. Обход, о конце которого не сказали, остался бы в её
        // словарях навсегда — у демона, который живёт неделями.
        await Assert.That(debugger.Finished).Count().IsEqualTo(1);
        await Assert.That(debugger.Finished[0]).IsEqualTo(observer.Walks[0].WalkId);
    }
}
