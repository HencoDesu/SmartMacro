using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;
using SmartMacro.Tests.Macros;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Ipc;

// D5: отладчик в том виде, в каком он ведёт себя на настоящем (пусть и внутрипамятном)
// соединении, когда на другом конце настоящий исполнитель обходит настоящий граф.
//
// Три вещи, которые способен показать только сквозной тест:
//
//   1. точка останова, поставленная через провод, и правда паркует обходчик;
//   2. ОБРЫВ СОЕДИНЕНИЯ при припаркованном обходе его распускает — это опасность номер один и
//      причина, по которой счёт подключений едет на подписке о событиях прогона, а не живёт сам
//      по себе;
//   3. собственные события отладчика доходят до панели, НЕ выжидая окно склейки в 50 мс, потому
//      что запаздывающий шаг ощущается сломанной кнопкой.
public class DebuggerProtocolTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Engine = new IpcDispatcherHarness();
            Server = new IpcServer(
                Engine.Dispatcher,
                Engine.Windows,
                Engine.Macros,
                Engine.Runs,
                Engine.RunEvents,
                Engine.Log,
                Engine.Debug,
                Engine.Hotkeys,
                Engine.SettingsSnapshots,
                NullLogger<IpcServer>.Instance);
            Server.SubscribeToEngine();
            Engine.RunEvents.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Primitives = new RecordingPrimitives();
            Templates = new FakeTemplateSource();
            Executor = new MacroExecutor(
                Primitives,
                new WindowRegistry(NullLogger<WindowRegistry>.Instance),
                NullLogger<MacroExecutor>.Instance);
        }

        public IpcDispatcherHarness Engine { get; }

        public IpcServer Server { get; }

        public RecordingPrimitives Primitives { get; }

        /// <summary>Порунный источник шаблонов (F2): в бою его ставит Orchestrator.RunAsync.</summary>
        public FakeTemplateSource Templates { get; }

        public MacroExecutor Executor { get; }

        public async Task<(DuplexStreamPair Client, Task Serve)> ConnectAsync()
        {
            var expected = Server.ConnectionCount + 1;
            var pair = new DuplexStreamPair();
            var serve = Server.ServeConnectionAsync(pair.ServerInput, pair.ServerOutput);
            await WaitUntilAsync(() => Server.ConnectionCount >= expected);
            return (pair, serve);
        }

        /// <summary>Обход графа <paramref name="graph"/>, подключённый к настоящему публикатору и сессии отладки.</summary>
        public Task<MacroRunResult> RunAsync(MacroGraph graph, CancellationToken ct = default) =>
            Executor.RunAsync(
                graph,
                new MacroRunContext
                {
                    ContextWindow = new IntPtr(0x140804),
                    Variables = MacroVariables.ForTrigger(new ScreenPoint(1804, 902)),
                    Templates = Templates,
                    Observer = Engine.RunEvents,
                    Debugger = Engine.Debug,
                },
                ct);

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Engine.Dispose();
        }
    }

    private static MacroGraph Chain() => new()
    {
        Name = "pw-boot",
        StartNodeId = Ids.Of("a"),
        Nodes =
        [
            new KeyPressNode { Id = Ids.Of("a"), DisplayName = "a", Key = VirtualKey.F1, Next = Ids.Of("b") },
            new KeyPressNode { Id = Ids.Of("b"), DisplayName = "b", Key = VirtualKey.F2, Next = Ids.Of("c") },
            new KeyPressNode { Id = Ids.Of("c"), DisplayName = "c", Key = VirtualKey.F3 },
        ],
    };

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    // Возвращала bool, который единственный вызывающий игнорировал: истёкшее ожидание молча
    // ехало дальше и падало позже и не там. Теперь бросает сама.
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new TimeoutException($"Условие не выполнилось за {timeoutMs} мс.");
        }
    }

    /// <summary>
    /// Одна строка из провода, разложенная по адресатам: ответ уходит тому, кто спрашивал под
    /// этим id, пачка событий дописывается в <see cref="Events"/>.
    ///
    /// Всё дело именно в этом разделении, и ошибка в нём — то, из-за чего висел первый черновик
    /// этих тестов: сопоставление ответа способом «читать строки, пока id не совпадёт»
    /// ВЫБРАСЫВАЕТ приехавшие между делом события прогона, и та пауза, которой ждала более
    /// поздняя проверка, была уже позади. У настоящего <c>IpcClient</c> ровно такая же форма и
    /// ровно по той же причине: ответы и события делят один поток, и ни одно не вправе съесть
    /// другое.
    /// </summary>
    private sealed class Reader(DuplexStreamPair client)
    {
        public List<RunEventDto> Events { get; } = [];

        public async Task<JsonElement> RequestAsync(int id, string type, object? payload = null)
        {
            await client.SendAsync(new IpcRequest(id, type, payload is null ? null : IpcJson.Write(payload)));
            while (true)
            {
                var line = await NextAsync();
                if (line.TryGetProperty("Id", out var idElement) && idElement.GetInt32() == id)
                {
                    return line;
                }
            }
        }

        /// <summary>Читает, пока <paramref name="predicate"/> не станет истинным для всего увиденного к этому моменту.</summary>
        public async Task<List<RunEventDto>> UntilAsync(Func<List<RunEventDto>, bool> predicate)
        {
            while (!predicate(Events))
            {
                await NextAsync();
            }

            return Events;
        }

        public async Task<DebugAckDto> CommandAsync(int id, Guid walkId, DebugCommand command, Guid? nodeId = null)
        {
            var reply = await RequestAsync(id, IpcMessageTypes.DebugCommand,
                new DebugCommandRequest(walkId, command, nodeId));
            return IpcJson.Read<DebugAckDto>(reply.GetProperty("Payload"))!;
        }

        public Task SubscribeAsync(int id, bool enabled = true) =>
            RequestAsync(id, IpcMessageTypes.SubscribeRunEvents, new SubscribeRunEventsRequest(enabled));

        private async Task<JsonElement> NextAsync()
        {
            var line = Parse(await client.ReadLineAsync());
            if (line.TryGetProperty("Type", out var type)
                && type.GetString() == IpcMessageTypes.RunEvents)
            {
                Events.AddRange(IpcJson.Read<RunEventBatch>(line.GetProperty("Payload"))!.Events);
            }

            return line;
        }
    }

    // ---- точки останова через провод -----------------------------------------------------

    [Test]
    public async Task BreakpointsCanBeSetWhileNothingIsRunning_AndAreReadBackWhole()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);

        // Взвести её до нажатия «Запустить» — это обычный способ ею пользоваться, так что живой
        // обход здесь не требуется, как и сохранённый макрос.
        await reader.RequestAsync(1, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("b"), Ids.Of("c")]));
        var reply = await reader.RequestAsync(2, IpcMessageTypes.GetBreakpoints);

        var sets = IpcJson.Read<BreakpointSetDto[]>(reply.GetProperty("Payload"))!;
        await Assert.That(sets).Count().IsEqualTo(1);
        await Assert.That(sets[0].MacroName).IsEqualTo("pw-boot");
        await Assert.That(sets[0].NodeIds).IsEquivalentTo(new[] { Ids.Of("b"), Ids.Of("c") });

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task AnEmptyBreakpointSetClearsTheMacroEntirely()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);

        await reader.RequestAsync(1, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("b")]));
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", []));
        var reply = await reader.RequestAsync(3, IpcMessageTypes.GetBreakpoints);

        await Assert.That(IpcJson.Read<BreakpointSetDto[]>(reply.GetProperty("Payload"))!).IsEmpty();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task BreakpointsSurviveAClientReconnecting()
    {
        // Эргономическая причина, по которой точки останова живут в демоне, а не в файле
        // макроса: демон переживает панель, поэтому «в пределах сессии» уже означает «переживает
        // перезапуск панели», — а именно этого от сохранения на самом деле и хотели.
        await using var fixture = new Fixture();
        var (first, firstServe) = await fixture.ConnectAsync();
        await new Reader(first).RequestAsync(1, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("b")]));
        first.CloseClient();
        await firstServe;

        var (second, secondServe) = await fixture.ConnectAsync();
        var reply = await new Reader(second).RequestAsync(1, IpcMessageTypes.GetBreakpoints);

        await Assert.That(IpcJson.Read<BreakpointSetDto[]>(reply.GetProperty("Payload"))!.Single().NodeIds)
            .IsEquivalentTo(new[] { Ids.Of("b") });

        second.CloseClient();
        await secondServe;
    }

    // ---- полный круг: припарковать, шагнуть, распустить ------------------------------------

    [Test]
    public async Task ABreakpointParksTheWalk_AndTheStepCommandAdvancesItOneNode()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("a")]));

        var run = fixture.RunAsync(Chain());

        var events = await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));
        var hit = events.Single(e => e.Kind == RunEventKind.BreakpointHit);
        await Assert.That(hit.NodeName).IsEqualTo("a");
        // Причина паузы — выбор из четырёх (точка останова / пауза / шаг / до курсора), и панель
        // печатает её как есть. Утверждается именно выбор, а не слова.
        await Assert.That(hit.Detail).IsEqualTo(Strings.Run_PauseReason_Breakpoint);
        await Assert.That(hit.Detail).IsNotEqualTo(Strings.Run_PauseReason_Step);
        await Assert.That(fixture.Primitives.Calls).IsEmpty();

        var ack = await reader.CommandAsync(3, hit.WalkId, DebugCommand.Step);
        await Assert.That(ack.Accepted).IsTrue();

        events = await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.Paused));
        var stepped = events.Single(e => e.Kind == RunEventKind.Paused);
        await Assert.That(stepped.NodeName).IsEqualTo("b");
        // …а после «Шага» — уже другая: остановились не на точке останова, а по команде.
        await Assert.That(stepped.Detail).IsEqualTo(Strings.Run_PauseReason_Step);
        await Assert.That(stepped.Detail).IsNotEqualTo(Strings.Run_PauseReason_Breakpoint);
        await Assert.That(events.Any(e => e.Kind == RunEventKind.Resumed && e.NodeName == "a")).IsTrue();

        await reader.CommandAsync(4, hit.WalkId, DebugCommand.Resume);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(fixture.Primitives.Calls).Count().IsEqualTo(3);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task TheTriggerSeedAndEveryNodeWriteArriveAsVariableEvents()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        fixture.Primitives.RecognizeHandler = (_, _, _) => "Жрец";

        var graph = new MacroGraph
        {
            Name = "pw-identify-one",
            StartNodeId = Ids.Of("recognize-class"),
            Nodes =
            [
                new RecognizeTagNode
                {
                    Id = Ids.Of("recognize-class"), DisplayName = "recognize-class",
                    TemplateSet = "classes",
                    Region = new ScreenRect(0, 0, 160, 35),
                    ApplyTag = false,
                },
            ],
        };

        var run = fixture.RunAsync(graph);
        var events = await reader.UntilAsync(all => all.Count(e => e.Kind == RunEventKind.VariableSet) >= 2);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        var vars = events.Where(e => e.Kind == RunEventKind.VariableSet).ToList();
        // cursor приходит от триггера и ноды не называет; tag приходит от той ноды, которая его
        // и записала.
        var cursor = vars.Single(e => e.Variable == MacroVariableNames.Cursor);
        await Assert.That(cursor.NodeId).IsNull();
        await Assert.That(cursor.Detail).IsEqualTo(new ScreenPoint(1804, 902).ToString());

        var tag = vars.Single(e => e.Variable == "tag");
        await Assert.That(tag.NodeName).IsEqualTo("recognize-class");
        await Assert.That(tag.Detail).IsEqualTo("Жрец");

        client.CloseClient();
        await serve;
    }

    // ---- опасность 1: панель уходит, а обход остаётся припаркованным -----------------------

    [Test]
    public async Task DroppingTheConnectionReleasesAPausedWalk()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("b")]));

        var run = fixture.RunAsync(Chain());
        await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));
        await Assert.That(fixture.Engine.Debug.AttachedCount).IsEqualTo(1);

        // Панель упала или пользователь закрыл окно. Нажать «Дальше» больше некому, а обход,
        // оставленный в затворе, держал бы слот single-flight своего макроса, — то есть хоткей
        // этого макроса был бы мёртв до самого перезапуска демона.
        client.CloseClient();
        await serve;

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(fixture.Primitives.Calls).Count().IsEqualTo(3);
        await Assert.That(fixture.Engine.Debug.AttachedCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnsubscribingWithoutDisconnectingAlsoReleasesAPausedWalk()
    {
        // Та же опасность, но с другой стороны: панель на месте, однако ушла из «Макросов», а
        // подписка живёт именно там. Показать паузу она больше не может — значит, и держать паузу
        // больше не должна.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("b")]));

        var run = fixture.RunAsync(Chain());
        await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));

        await reader.SubscribeAsync(3, enabled: false);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task AConnectionThatNeverSubscribedCannotIssueDebugCommands()
    {
        // Счёт подключений и есть то свойство, которое обеспечивает безопасность; команда извне
        // него способна припарковать обход, распустить который, по счёту, некому.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        var reply = await new Reader(client).RequestAsync(
            1,
            IpcMessageTypes.DebugCommand,
            new DebugCommandRequest(Guid.NewGuid(), DebugCommand.Pause));

        await Assert.That(reply.GetProperty("Ok").GetBoolean()).IsFalse();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task StoppingTheRunUnparksTheWalk()
    {
        // Опасность 3, половина со стороны движка: «■ Стоп» отменяет токен ПРОГОНА, и
        // припаркованный обход обязан выйти из затвора, а не сидеть в нём, удерживая его.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("b")]));

        using var cts = new CancellationTokenSource();
        var run = fixture.RunAsync(Chain(), cts.Token);
        await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));

        await cts.CancelAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);

        client.CloseClient();
        await serve;
    }

    // ---- задержки --------------------------------------------------------------------------

    [Test]
    public async Task ADebuggerEventDoesNotWaitOutTheCoalescingWindow()
    {
        // Окно склейки — 50 мс, и для лога это правильно. Для шага же это разница между
        // мгновенной кнопкой и залипающей, поэтому пауза и попадание в точку останова
        // выдержку пропускают.
        //
        // Замеряется НЕ абсолютное время, а разница с контрольным прогоном без точки
        // останова. Так пробовали раньше — сравнением с порогом 45 мс по Environment
        // .TickCount64, — и тест плавал: у этого счётчика шаг около 15,6 мс, так что при
        // запасе в 5 мс одно выравнивание тика давало 46 без всякой регрессии, а под
        // нагрузкой (скажем, параллельная сборка) в 45 мс не укладывалась и честная
        // работа. Нагрузка замедляет оба прогона одинаково, поэтому разница между ними
        // устойчива там, где абсолютное время не устойчиво.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);

        // Контроль: точек останова нет, значит первая пачка честно ждёт выдержку целиком.
        var control = Stopwatch.StartNew();
        await fixture.RunAsync(Chain()).WaitAsync(TimeSpan.FromSeconds(5));
        await reader.UntilAsync(all => all.Count > 0);
        control.Stop();

        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints,
            new SetBreakpointsRequest("pw-boot", [Ids.Of("a")]));

        // Тот же путь, но событие срочное: оно обязано прервать выдержку, а не досидеть её.
        var mark = reader.Events.Count;
        var urgent = Stopwatch.StartNew();
        var run = fixture.RunAsync(Chain());
        var events = await reader.UntilAsync(all => all.Skip(mark).Any(e => e.Kind == RunEventKind.BreakpointHit));
        urgent.Stop();

        // Разрыв должен быть порядка полной выдержки. Половины хватает, чтобы отличить
        // «прервали» от «досидели», и остаётся запас на дрожание планировщика.
        await Assert.That(urgent.Elapsed).IsLessThan(control.Elapsed - TimeSpan.FromMilliseconds(25));

        await reader.CommandAsync(3, events.Single(e => e.Kind == RunEventKind.BreakpointHit).WalkId,
            DebugCommand.Resume);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        client.CloseClient();
        await serve;
    }
}
