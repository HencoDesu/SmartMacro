using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;

namespace SmartMacro.Tests.Ipc;

// D3b: канал событий прогона в том виде, в каком он ведёт себя на настоящем (пусть и
// внутрипамятном) соединении.
//
// Проверяется здесь не «дошло ли событие», а договорённость о защите от затопления: именно она
// решает, будет ли панель ещё подключена в тот момент, когда пользователю это важно. Сервер даёт
// каждому соединению ограниченную очередь на 256 записей и ОТКЛЮЧАЕТ клиента, который её
// забивает, — так что непосредованный поток отцепил бы панель прямо посреди того веера, ради
// наблюдения за которым её и открыли. Отвечают на это три механизма, и на каждый здесь есть тест:
//
//   1. пока соединение не подписалось, не производится ничего;
//   2. то, что производится, склеивается в пачки;
//   3. переполнение считается и о нём сообщают — его никогда не глотают молча и никогда не
//      ждут на движке.
public class RunEventStreamTests
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
        }

        public IpcDispatcherHarness Engine { get; }

        public IpcServer Server { get; }

        public RunEventPublisher Publisher => Engine.RunEvents;

        public async Task<(DuplexStreamPair Client, Task Serve)> ConnectAsync()
        {
            var expected = Server.ConnectionCount + 1;
            var pair = new DuplexStreamPair();
            var serve = Server.ServeConnectionAsync(pair.ServerInput, pair.ServerOutput);
            await WaitUntilAsync(() => Server.ConnectionCount >= expected);
            return (pair, serve);
        }

        /// <summary>
        /// Подписывает клиента и возвращает обходы, о которых демон сказал, что они уже идут.
        /// </summary>
        public async Task<RunWalkDto[]> SubscribeAsync(DuplexStreamPair client, int id = 1, bool enabled = true)
        {
            await client.SendAsync(new IpcRequest(id, IpcMessageTypes.SubscribeRunEvents,
                IpcJson.Write(new SubscribeRunEventsRequest(enabled))));
            var reply = await ReadReplyAsync(client, id);
            if (!reply.GetProperty("Ok").GetBoolean())
            {
                throw new InvalidOperationException(reply.GetProperty("Error").GetString());
            }

            return IpcJson.Read<RunWalkDto[]>(reply.GetProperty("Payload"))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Engine.Dispose();
        }
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    private static string TypeOf(JsonElement element) => element.GetProperty("Type").GetString()!;

    /// <summary>
    /// Читает строки, пока не придёт ОТВЕТ с нужным <paramref name="id"/>; пуши, встреченные по
    /// дороге, пропускаются.
    ///
    /// ⚠️ <b>Не «прочитать следующую строку».</b> Первый закон этого протокола — «ответы могут
    /// прийти не по порядку, соотносить по <c>Id</c>, никогда по приходу», и настоящий клиент в
    /// панели именно так и устроен; оснастка же читала позиционно и тем нарушала правило того
    /// самого кода, который проверяет. Стоило это плавающего теста: пачка событий, вылетевшая
    /// между запросом и ответом, доставалась вместо ответа — редко (для этого надо попасть в
    /// окно склейки) и потому невоспроизводимо.
    ///
    /// Ответ от пуша отличается наличием <c>Id</c>: у <c>IpcEvent</c> такого поля нет по
    /// определению.
    /// </summary>
    private static async Task<JsonElement> ReadReplyAsync(DuplexStreamPair client, int id, int timeoutMs = 5000)
    {
        while (true)
        {
            var line = Parse(await client.ReadLineAsync(timeoutMs));
            if (line.TryGetProperty("Id", out var actual) && actual.GetInt32() == id)
            {
                return line;
            }
        }
    }

    /// <summary>Зеркало <see cref="ReadReplyAsync"/>: читает, пока не придёт ПУШ.</summary>
    private static async Task<JsonElement> ReadPushAsync(DuplexStreamPair client, int timeoutMs = 5000)
    {
        while (true)
        {
            var line = Parse(await client.ReadLineAsync(timeoutMs));
            if (!line.TryGetProperty("Id", out _))
            {
                return line;
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Дёргает наблюдателя так же, как это сделал бы обход по нодам <paramref name="nodes"/>.</summary>
    private static Guid Walk(RunEventPublisher publisher, string macroName, long hwnd, params string[] nodes)
    {
        var walkId = Guid.NewGuid();
        publisher.WalkStarted(new MacroWalkStart(walkId, Guid.NewGuid(), macroName, null, null,
            hwnd == 0 ? null : new IntPtr(hwnd), 0));
        foreach (var node in nodes)
        {
            publisher.NodeEntered(walkId, 0, Ids.Of(node), node);
            publisher.NodeExited(walkId, 1, Ids.Of(node), node, RunOutcomes.Ok, "деталь", 1);
        }

        return walkId;
    }

    // ---- только по подписке --------------------------------------------------------------

    [Test]
    public async Task WithNoSubscriber_TheEngineIsNotEvenInstrumented()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await Assert.That(fixture.Publisher.IsEnabled).IsFalse();
        Walk(fixture.Publisher, "pw-boot", 0x10, "a", "b");

        // Не «события отфильтровали» — их вообще не производили. Единственная строка, которую
        // это соединение способно получить, — ответ на посланный следом запрос.
        await client.SendAsync(new IpcRequest(7, IpcMessageTypes.GetWindows));
        var line = await ReadReplyAsync(client, 7);
        await Assert.That(line.GetProperty("Id").GetInt32()).IsEqualTo(7);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task SubscribingTurnsTheStreamOn_AndUnsubscribingTurnsItOff()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await fixture.SubscribeAsync(client);
        await Assert.That(fixture.Publisher.IsEnabled).IsTrue();
        await Assert.That(fixture.Publisher.SubscriberCount).IsEqualTo(1);

        Walk(fixture.Publisher, "pw-boot", 0x10, "a");
        var evt = await ReadPushAsync(client);
        await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.RunEvents);

        await fixture.SubscribeAsync(client, id: 2, enabled: false);
        await Assert.That(fixture.Publisher.IsEnabled).IsFalse();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task AskingTwiceDoesNotDoubleCount_SoOneUnsubscribeIsEnough()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await fixture.SubscribeAsync(client, id: 1);
        await fixture.SubscribeAsync(client, id: 2);
        await Assert.That(fixture.Publisher.SubscriberCount).IsEqualTo(1);

        await fixture.SubscribeAsync(client, id: 3, enabled: false);
        // Утёкшая здесь ссылка навсегда оставила бы исполнитель с включённой трассировкой, хотя
        // читать её некому, — а ровно от этих расходов подписка и избавляет.
        await Assert.That(fixture.Publisher.SubscriberCount).IsEqualTo(0);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task ADisconnectReleasesTheSubscriptionItNeverGaveBack()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        await fixture.SubscribeAsync(client);
        await Assert.That(fixture.Publisher.SubscriberCount).IsEqualTo(1);

        client.CloseClient();
        await serve;

        await Assert.That(await WaitUntilAsync(() => fixture.Publisher.SubscriberCount == 0)).IsTrue();
        await Assert.That(fixture.Publisher.IsEnabled).IsFalse();
    }

    [Test]
    public async Task AClientThatNeverAskedIsNotSentTheStream()
    {
        await using var fixture = new Fixture();
        var (watcher, watcherServe) = await fixture.ConnectAsync();
        var (bystander, bystanderServe) = await fixture.ConnectAsync();

        await fixture.SubscribeAsync(watcher);
        Walk(fixture.Publisher, "pw-boot", 0x10, "a");

        await Assert.That(TypeOf(await ReadPushAsync(watcher))).IsEqualTo(IpcMessageTypes.RunEvents);

        // Посторонний получает обычные события и ничего сверх того. Вывалив на него всплеск, мы
        // стоили бы ему соединения ради потока, который ему совершенно не нужен.
        fixture.Engine.Windows.Register(0x99, "elementclient");
        var seen = await ReadPushAsync(bystander);
        await Assert.That(TypeOf(seen)).IsEqualTo(IpcMessageTypes.WindowAppeared);

        watcher.CloseClient();
        bystander.CloseClient();
        await Task.WhenAll(watcherServe, bystanderServe);
    }

    // ---- подписка посреди прогона ----------------------------------------------------------

    [Test]
    public async Task SubscribingMidRun_AnswersWithTheLiveWalks_FlaggedAsIncomplete()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        // Два обхода уже идут, а слушать их ещё некому.
        var first = Walk(fixture.Publisher, "pw-boot", 0x140804);
        Walk(fixture.Publisher, "pw-assist", 0);

        var live = await fixture.SubscribeAsync(client);

        await Assert.That(live).Count().IsEqualTo(2);
        await Assert.That(live.Select(w => w.MacroName)).IsEquivalentTo(new[] { "pw-boot", "pw-assist" });
        // В этом весь смысл: их начальные строки по нодам прошли, когда никто ничего не
        // записывал, и протокол так и говорит, вместо того чтобы позволить панели нарисовать
        // хвост как полный лог.
        await Assert.That(live.All(w => !w.FromStart)).IsTrue();
        await Assert.That(live.Single(w => w.MacroName == "pw-boot").Hwnd).IsEqualTo(0x140804L);
        await Assert.That(live.Single(w => w.MacroName == "pw-assist").Hwnd).IsEqualTo(0L);

        // Обход, успевший с тех пор завершиться, уже не предлагают.
        fixture.Publisher.WalkFinished(first, 10, RunOutcomes.Completed, null);
        var again = await fixture.SubscribeAsync(client, id: 2);
        await Assert.That(again.Select(w => w.MacroName)).IsEquivalentTo(new[] { "pw-assist" });

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task AWalkStartedWhileSubscribedIsFlaggedComplete()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        await fixture.SubscribeAsync(client);

        Walk(fixture.Publisher, "pw-boot", 0x1, "a");

        var batch = IpcJson.Read<RunEventBatch>((await ReadPushAsync(client)).GetProperty("Payload"))!;
        var started = batch.Events.First(e => e.Kind == RunEventKind.WalkStarted);
        await Assert.That(started.Walk!.FromStart).IsTrue();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task SubscribeWithoutAConnectionIsRejectedRatherThanSilentlyIgnored()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(
            IpcMessageTypes.SubscribeRunEvents,
            new SubscribeRunEventsRequest(true));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(harness.RunEvents.IsEnabled).IsFalse();
    }

    // ---- всплеск ---------------------------------------------------------------------------

    [Test]
    public async Task ATenWindowFanOutIsCoalesced_AndThePanelIsStillConnectedAfterwards()
    {
        const int walks = 10;
        const int nodesPerWalk = 12;
        // 10 × (1 начало обхода + 12 × 2 события по нодам + 1 конец обхода) = 260 событий. Без
        // склейки это уже больше, чем очередь соединения на 256 событий, — тот самый отказ,
        // вокруг которого этой волне и пришлось проектировать, и случился бы он ровно тогда,
        // когда пользователь смотрит на экран.
        const int expected = walks * (2 + (nodesPerWalk * 2));

        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        await fixture.SubscribeAsync(client);

        var nodes = Enumerable.Range(0, nodesPerWalk).Select(i => $"n{i}").ToArray();
        // Производятся с десяти потоков разом — именно так это и делает веер по селектору целей.
        await Task.WhenAll(Enumerable.Range(0, walks).Select(i => Task.Run(() =>
        {
            var walkId = Walk(fixture.Publisher, "pw-identify-one", 0x100 + i, nodes);
            fixture.Publisher.WalkFinished(walkId, 100, RunOutcomes.Completed, null);
        })));

        var events = new List<RunEventDto>();
        var envelopes = 0;
        var dropped = 0;
        var deadline = Environment.TickCount64 + 15000;
        while (events.Count + dropped < expected && Environment.TickCount64 < deadline)
        {
            var line = await ReadPushAsync(client);
            await Assert.That(TypeOf(line)).IsEqualTo(IpcMessageTypes.RunEvents);
            var batch = IpcJson.Read<RunEventBatch>(line.GetProperty("Payload"))!;
            events.AddRange(batch.Events);
            dropped += batch.Dropped;
            envelopes++;
        }

        await Assert.That(events.Count + dropped).IsEqualTo(expected);
        // Очередь глубиной 4096, так что всплеск такого размера не теряет ничего.
        await Assert.That(dropped).IsEqualTo(0);
        // Обещание про склейку, выраженное числом: сотни событий — горстка строк в проводе,
        // с большим запасом ниже тех 256, которые стоили бы панели соединения.
        await Assert.That(envelopes).IsLessThan(64);
        await Assert.That(events.Count(e => e.Kind == RunEventKind.WalkStarted)).IsEqualTo(walks);
        await Assert.That(events.Select(e => e.WalkId).Distinct()).Count().IsEqualTo(walks);

        // И то, ради чего всё затевалось: соединение это пережило.
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(1);
        await client.SendAsync(new IpcRequest(99, IpcMessageTypes.GetWindows));
        var reply = await ReadReplyAsync(client, 99);
        await Assert.That(reply.GetProperty("Id").GetInt32()).IsEqualTo(99);
        await Assert.That(reply.GetProperty("Ok").GetBoolean()).IsTrue();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task OverflowIsCountedAndReported_NeverSilent()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        await fixture.SubscribeAsync(client);

        // Намеренно больше, чем собственная очередь публикатора на 4096 событий. Движку ждать
        // нельзя — прогон макроса идёт между двумя сообщениями Win32, — поэтому излишек
        // выбрасывается, а счёт уезжает со следующей пачкой, чтобы панель могла признаться в
        // дыре.
        var walkId = Guid.NewGuid();
        fixture.Publisher.WalkStarted(new MacroWalkStart(walkId, Guid.NewGuid(), "шторм", null, null, new IntPtr(0x1), 0));
        for (var i = 0; i < 20_000; i++)
        {
            fixture.Publisher.NodeEntered(walkId, i, Ids.Of("n"), "n");
        }

        var dropped = 0;
        var deadline = Environment.TickCount64 + 15000;
        while (dropped == 0 && Environment.TickCount64 < deadline)
        {
            var batch = IpcJson.Read<RunEventBatch>((await ReadPushAsync(client)).GetProperty("Payload"))!;
            dropped += batch.Dropped;
        }

        await Assert.That(dropped).IsGreaterThan(0);
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(1);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task TheLastUnsubscribeDrainsTheQueue_SoTheNextSubscriberGetsNoHistory()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await fixture.SubscribeAsync(client);
        Walk(fixture.Publisher, "pw-boot", 0x1, "a", "b", "c");
        await fixture.SubscribeAsync(client, id: 2, enabled: false);

        // Всё, что осталось в очереди, адресовалось подписчику, которого больше нет. Доставить
        // это следующему — значит открыть ему лог всплеском из прогона, которого он не видел.
        await fixture.SubscribeAsync(client, id: 3);
        Walk(fixture.Publisher, "pw-assist", 0x2, "z");

        var batch = IpcJson.Read<RunEventBatch>((await ReadPushAsync(client)).GetProperty("Payload"))!;

        // OfType<string>(), а не Where(name => name is not null): здесь null — законное значение
        // (у событий уровня обхода ноды нет) и его действительно надо отбросить, но через Where
        // компилятор тип не сужает, и на выходе оставался IEnumerable<string?> — отсюда CS8631.
        // OfType и отфильтровывает, и сужает, то есть говорит ровно то, что тут и происходит.
        await Assert.That(batch.Events.Select(e => e.NodeName).OfType<string>())
            .IsEquivalentTo(new[] { "z", "z" });

        client.CloseClient();
        await serve;
    }
}
