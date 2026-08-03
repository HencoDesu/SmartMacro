using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;

namespace SmartMacro.Tests.Ipc;

// Лента журнала демона: вторая подписка на русле волны D3b.
//
// Три меры от затопления здесь те же, что у событий прогона, и на каждую есть тест. Но проверять
// стоит прежде всего то, чего у событий прогона НЕ было:
//
//   · КОЛЬЦО. Журнал пишется независимо от того, смотрит ли кто-нибудь, и панель, подключившаяся
//     к работающему демону, обязана увидеть предысторию, а не пустой экран. У D3b буфера нет по
//     обратной причине, и перепутать эти два случая — самый вероятный способ сделать здесь
//     неправильно.
//   · РЕКУРСИЯ. Сток, который шлёт записи по IPC, живёт внутри процесса, который логирует. Тест
//     ReentrantBroadcaster воспроизводит ровно ту петлю, которая иначе раскрутилась бы сама.
public class LogStreamTests
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
                Engine.SettingsSnapshots,
                NullLogger<IpcServer>.Instance);
            Server.SubscribeToEngine();
            Engine.Log.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public IpcDispatcherHarness Engine { get; }

        public IpcServer Server { get; }

        public LogEventPublisher Publisher => Engine.Log;

        public async Task<(DuplexStreamPair Client, Task Serve)> ConnectAsync()
        {
            var expected = Server.ConnectionCount + 1;
            var pair = new DuplexStreamPair();
            var serve = Server.ServeConnectionAsync(pair.ServerInput, pair.ServerOutput);
            await WaitUntilAsync(() => Server.ConnectionCount >= expected);
            return (pair, serve);
        }

        /// <summary>Подписывает клиента и возвращает предысторию, которой ответил демон.</summary>
        public async Task<LogEntryDto[]> SubscribeAsync(DuplexStreamPair client, int id = 1, bool enabled = true)
        {
            await client.SendAsync(new IpcRequest(id, IpcMessageTypes.SubscribeLog,
                IpcJson.Write(new SubscribeLogRequest(enabled))));
            var reply = Parse(await client.ReadLineAsync());
            if (!reply.GetProperty("Ok").GetBoolean())
            {
                throw new InvalidOperationException(reply.GetProperty("Error").GetString());
            }

            return IpcJson.Read<LogEntryDto[]>(reply.GetProperty("Payload"))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Engine.Dispose();
        }
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    private static string TypeOf(JsonElement element) => element.GetProperty("Type").GetString()!;

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

    /// <summary>Пишет запись ровно так же, как это делает сток Serilog в демоне.</summary>
    private static void Write(
        LogEventPublisher publisher,
        string message,
        LogLevelDto level = LogLevelDto.Information,
        string? source = "SmartMacro.Windows.WindowRegistry",
        string? exception = null) =>
        publisher.Append(DateTimeOffset.Now, level, source, message, exception);

    // ---- кольцо ------------------------------------------------------------------------------

    [Test]
    public async Task WithNoSubscriber_NothingIsPushed_ButTheRingStillFills()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await Assert.That(fixture.Publisher.IsEnabled).IsFalse();
        Write(fixture.Publisher, "пока никто не смотрит");

        // Единственная строка, которую это соединение способно получить, — ответ на посланный
        // следом запрос.
        await client.SendAsync(new IpcRequest(7, IpcMessageTypes.GetWindows));
        var line = Parse(await client.ReadLineAsync());
        await Assert.That(line.GetProperty("Id").GetInt32()).IsEqualTo(7);

        // А вот кольцо наполнялось всё это время, и в этом отличие от событий прогона.
        var history = await fixture.SubscribeAsync(client, id: 8);
        await Assert.That(history.Select(e => e.Message)).IsEquivalentTo(new[] { "пока никто не смотрит" });

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task TheRingKeepsOnlyTheLastN_AndKeepsThemInOrder()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        const int extra = 25;
        for (var i = 1; i <= LogLimits.HistoryCapacity + extra; i++)
        {
            Write(fixture.Publisher, $"строка {i}");
        }

        var history = await fixture.SubscribeAsync(client);

        await Assert.That(history).Count().IsEqualTo(LogLimits.HistoryCapacity);
        await Assert.That(history[0].Message).IsEqualTo($"строка {extra + 1}");
        await Assert.That(history[^1].Message).IsEqualTo($"строка {LogLimits.HistoryCapacity + extra}");
        // Номера строго возрастают — на этом держится отбрасывание повтора у панели.
        await Assert.That(history.Zip(history.Skip(1)).All(pair => pair.Second.Seq > pair.First.Seq)).IsTrue();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task UnsubscribingAnswersWithNothing()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        Write(fixture.Publisher, "что-то было");

        var history = await fixture.SubscribeAsync(client, enabled: false);

        // Предыстория без ленты никому не нужна — и главное, не должна выглядеть как лента.
        await Assert.That(history).IsEmpty();
        await Assert.That(fixture.Publisher.IsEnabled).IsFalse();

        client.CloseClient();
        await serve;
    }

    // ---- подписка ---------------------------------------------------------------------------

    [Test]
    public async Task SubscribingTurnsTheStreamOn_AndUnsubscribingTurnsItOff()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await fixture.SubscribeAsync(client);
        await Assert.That(fixture.Publisher.IsEnabled).IsTrue();

        Write(fixture.Publisher, "живая строка");
        var evt = Parse(await client.ReadLineAsync(timeoutMs: 5000));
        await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.LogEntries);

        var batch = IpcJson.Read<LogEntryBatch>(evt.GetProperty("Payload"))!;
        await Assert.That(batch.Entries.Select(e => e.Message)).Contains("живая строка");

        await fixture.SubscribeAsync(client, id: 2, enabled: false);
        await Assert.That(fixture.Publisher.SubscriberCount).IsEqualTo(0);

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
    }

    [Test]
    public async Task TheTwoSubscriptionsAreIndependent()
    {
        // Панель включает события прогона в «Макросах», а ленту журнала — на всю свою жизнь.
        // Общий флаг заставил бы каждый из режимов оплачивать трафик другого.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await fixture.SubscribeAsync(client);

        await Assert.That(fixture.Publisher.IsEnabled).IsTrue();
        await Assert.That(fixture.Engine.RunEvents.IsEnabled).IsFalse();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task AClientThatNeverAskedIsNotSentTheLog()
    {
        await using var fixture = new Fixture();
        var (watcher, watcherServe) = await fixture.ConnectAsync();
        var (bystander, bystanderServe) = await fixture.ConnectAsync();

        await fixture.SubscribeAsync(watcher);
        Write(fixture.Publisher, "только для подписчика");

        await Assert.That(TypeOf(Parse(await watcher.ReadLineAsync(timeoutMs: 5000))))
            .IsEqualTo(IpcMessageTypes.LogEntries);

        // Посторонний получает обычные события и ничего сверх того.
        fixture.Engine.Windows.Register(0x99, "elementclient");
        var seen = Parse(await bystander.ReadLineAsync());
        await Assert.That(TypeOf(seen)).IsEqualTo(IpcMessageTypes.WindowAppeared);

        watcher.CloseClient();
        bystander.CloseClient();
        await Task.WhenAll(watcherServe, bystanderServe);
    }

    [Test]
    public async Task SubscribeWithoutAConnectionIsRejectedRatherThanSilentlyIgnored()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.SubscribeLog, new SubscribeLogRequest(true));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(harness.Log.IsEnabled).IsFalse();
    }

    // ---- всплеск -----------------------------------------------------------------------------

    [Test]
    public async Task ABurstIsCoalesced_AndThePanelIsStillConnectedAfterwards()
    {
        // SmartMacro.GameWindows крутится на Debug, так что всплеск здесь — обычное дело, а не
        // патология. Очередь соединения — 256 записей, и клиента, который её забил, ОТКЛЮЧАЮТ:
        // без склейки панель отваливалась бы ровно в тот момент, когда на неё смотрят.
        const int count = 1200;

        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        await fixture.SubscribeAsync(client);

        // С нескольких потоков разом — так это и происходит при разветвлении макроса по окнам.
        // Публикатор вынут в локальную переменную, чтобы замыкание не держало сам fixture,
        // который освобождается снаружи.
        var publisher = fixture.Publisher;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < count / 4; i++)
            {
                Write(publisher, $"поток {worker} строка {i}", LogLevelDto.Debug);
            }
        })));

        var seen = 0;
        var dropped = 0;
        var envelopes = 0;
        var deadline = Environment.TickCount64 + 15000;
        while (seen + dropped < count && Environment.TickCount64 < deadline)
        {
            var line = Parse(await client.ReadLineAsync(timeoutMs: 5000));
            await Assert.That(TypeOf(line)).IsEqualTo(IpcMessageTypes.LogEntries);
            var batch = IpcJson.Read<LogEntryBatch>(line.GetProperty("Payload"))!;
            seen += batch.Entries.Count;
            dropped += batch.Dropped;
            envelopes++;
        }

        await Assert.That(seen + dropped).IsEqualTo(count);
        // Очередь глубиной 4096 — всплеск такого размера не теряет ничего.
        await Assert.That(dropped).IsEqualTo(0);
        // Обещание про склейку числом: тысяча с лишним записей — единицы строк в проводе, с
        // огромным запасом ниже тех 256, которые стоили бы панели соединения.
        await Assert.That(envelopes).IsLessThan(32);

        // И то, ради чего всё затевалось.
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(1);
        await client.SendAsync(new IpcRequest(99, IpcMessageTypes.GetWindows));
        var reply = Parse(await client.ReadLineAsync());
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

        // Заведомо больше собственной очереди публикатора. Ждать пишущему нельзя — логирует в
        // том числе обход макроса между двумя сообщениями живой игре, — поэтому излишек
        // выбрасывается, а счёт уезжает со следующей пачкой.
        for (var i = 0; i < 30_000; i++)
        {
            Write(fixture.Publisher, "шторм", LogLevelDto.Debug);
        }

        var dropped = 0;
        var deadline = Environment.TickCount64 + 15000;
        while (dropped == 0 && Environment.TickCount64 < deadline)
        {
            var batch = IpcJson.Read<LogEntryBatch>(Parse(await client.ReadLineAsync(timeoutMs: 5000))
                .GetProperty("Payload"))!;
            dropped += batch.Dropped;
        }

        await Assert.That(dropped).IsGreaterThan(0);
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(1);

        client.CloseClient();
        await serve;
    }

    // ---- рекурсия ----------------------------------------------------------------------------

    /// <summary>
    /// Рассылка, которая сама пишет в журнал, — воспроизведение той самой петли, ради которой
    /// существует подавление. В настоящем демоне так делает <c>IpcServer</c>, предупреждая об
    /// отставшем клиенте.
    /// </summary>
    private sealed class ReentrantBroadcaster : IIpcBroadcaster
    {
        private LogEventPublisher? _publisher;

        public int LogBroadcasts { get; private set; }

        public void Attach(LogEventPublisher publisher) => _publisher = publisher;

        public void Broadcast(IpcEvent evt)
        {
        }

        public void BroadcastToRunSubscribers(IpcEvent evt)
        {
        }

        public void BroadcastToLogSubscribers(IpcEvent evt)
        {
            LogBroadcasts++;
            _publisher?.Append(
                DateTimeOffset.Now,
                LogLevelDto.Warning,
                "SmartMacro.Ipc.IpcServer",
                "клиент не поспевает, отключаем",
                null);
        }
    }

    [Test]
    public async Task ARecordWrittenWhilePublishingDoesNotComeBackAround()
    {
        var broadcaster = new ReentrantBroadcaster();
        await using var publisher = new LogEventPublisher();
        broadcaster.Attach(publisher);
        publisher.AttachBroadcaster(broadcaster);
        await publisher.StartAsync(CancellationToken.None);
        publisher.Acquire();

        publisher.Append(DateTimeOffset.Now, LogLevelDto.Information, "тест", "первая", null);

        await Assert.That(await WaitUntilAsync(() => broadcaster.LogBroadcasts >= 1)).IsTrue();
        // Полсекунды — это пять окон склейки. Без подавления рассылок к этому моменту было бы
        // около шести, и дальше их число росло бы линейно, без конца.
        await Task.Delay(500);

        await Assert.That(broadcaster.LogBroadcasts).IsEqualTo(1);

        // И запись, порождённую самой отправкой, не видно ни в ленте, ни в кольце: иначе она
        // приехала бы следующей предысторией и получила бы второй шанс раскрутить петлю.
        await Assert.That(publisher.History().Select(e => e.Message)).IsEquivalentTo(new[] { "первая" });
    }

    [Test]
    public async Task SuppressionIsScopedToThePublish_NotToTheWholeProcess()
    {
        // Обратная половина того же: подавление снимается сразу после рассылки, иначе первая же
        // публикация заткнула бы журнал навсегда.
        var broadcaster = new ReentrantBroadcaster();
        await using var publisher = new LogEventPublisher();
        broadcaster.Attach(publisher);
        publisher.AttachBroadcaster(broadcaster);
        await publisher.StartAsync(CancellationToken.None);
        publisher.Acquire();

        publisher.Append(DateTimeOffset.Now, LogLevelDto.Information, "тест", "первая", null);
        await Assert.That(await WaitUntilAsync(() => broadcaster.LogBroadcasts >= 1)).IsTrue();

        publisher.Append(DateTimeOffset.Now, LogLevelDto.Information, "тест", "вторая", null);

        await Assert.That(await WaitUntilAsync(() => broadcaster.LogBroadcasts >= 2)).IsTrue();
        await Assert.That(publisher.History().Select(e => e.Message))
            .IsEquivalentTo(new[] { "первая", "вторая" });
    }
}
