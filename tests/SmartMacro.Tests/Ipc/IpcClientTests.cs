using System.Text.Json;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

// Стадия 3: клиентская половина протокола — сопоставление ответов запросам, разделение
// «ответ или событие», проброс отказов и переподключение.
//
// Проверяемый клиент сидит на СЕРВЕРНЫХ половинах DuplexStreamPair — с первого взгляда это
// читается наизнанку, а со второго становится понятно: пара изображает «один собеседник плюс
// тестовая оснастка», и здесь тот собеседник, которого гоняют, — как раз клиент. Поэтому
// pair.SendAsync подаёт клиенту строку, pair.ReadLineAsync подбирает то, что клиент написал, а
// pair.CloseClient выдаёт ему тот же EOF, что выдал бы остановленный демон.
public class IpcClientTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Гоняет <see cref="IpcClient"/> поверх одного или нескольких подложенных соединений в памяти.</summary>
    private sealed class ClientFixture : IAsyncDisposable
    {
        private readonly Queue<DuplexStreamPair> _staged = new();
        private readonly List<DuplexStreamPair> _all = [];

        public ClientFixture(TimeSpan? defaultTimeout = null)
        {
            Client = new IpcClient(ConnectAsync, defaultTimeout ?? TimeSpan.FromSeconds(5));
        }

        public IpcClient Client { get; }

        public int ConnectedCount;

        public int DisconnectedCount;

        public List<IpcEvent> Events { get; } = [];

        /// <summary>Добавляет соединение, которое клиент получит при следующем (пере)подключении.</summary>
        public DuplexStreamPair Stage()
        {
            var pair = new DuplexStreamPair();
            _staged.Enqueue(pair);
            _all.Add(pair);
            return pair;
        }

        public void Observe()
        {
            Client.Connected += () => Interlocked.Increment(ref ConnectedCount);
            Client.Disconnected += () => Interlocked.Increment(ref DisconnectedCount);
            Client.EventReceived += evt =>
            {
                lock (Events)
                {
                    Events.Add(evt);
                }
            };
        }

        private Task<IpcConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            if (_staged.Count == 0)
            {
                // Выдавать больше нечего: поддерживающий цикл понимает это как «демона нет» и
                // отступает — ровно то состояние, которое нам и нужно при разборке.
                throw new IOException("нет доступных соединений");
            }
            var pair = _staged.Dequeue();
            return Task.FromResult(new IpcConnection(pair.ServerInput, pair.ServerOutput, leaveOpen: true));
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            foreach (var pair in _all)
            {
                pair.Dispose();
            }
        }
    }

    private static IpcRequest ParseRequest(string line) =>
        JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options)!;

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

    // ---- сопоставление ответов запросам ----------------------------------------------------

    [Test]
    public async Task Responses_AreMatchedById_EvenWhenTheyArriveOutOfOrder()
    {
        await using var fixture = new ClientFixture();
        var pair = fixture.Stage();
        await Assert.That(await fixture.Client.StartAsync(TimeSpan.FromSeconds(2))).IsTrue();

        // Два вызова в полёте одновременно. Серверу прямо разрешено ответить сперва на второй
        // (обработчиков он не дожидается), так что клиент не вправе полагаться на порядок
        // прибытия.
        var first = fixture.Client.RequestAsync<WindowDto[]>(IpcMessageTypes.GetWindows);
        var second = fixture.Client.RequestAsync<string>(IpcMessageTypes.DumpCaptures);

        var requests = (await pair.ReadLinesAsync(2)).Select(ParseRequest).ToList();
        var windowsId = requests.Single(r => r.Type == IpcMessageTypes.GetWindows).Id;
        var dumpId = requests.Single(r => r.Type == IpcMessageTypes.DumpCaptures).Id;

        await pair.SendAsync(new IpcResponse(dumpId, Ok: true, IpcJson.Write("C:/debug")));
        await pair.SendAsync(new IpcResponse(
            windowsId,
            Ok: true,
            IpcJson.Write(new[] { new WindowDto(0x1111, "elementclient_64", ["Лучник"]) })));

        await Assert.That(await second).IsEqualTo("C:/debug");
        var windows = await first;
        await Assert.That(windows).IsNotNull();
        await Assert.That(windows!.Single().ProcessName).IsEqualTo("elementclient_64");
    }

    [Test]
    public async Task IdlessLines_AreRaisedAsEvents_AndNeverCompleteARequest()
    {
        await using var fixture = new ClientFixture();
        var pair = fixture.Stage();
        fixture.Observe();
        await fixture.Client.StartAsync(TimeSpan.FromSeconds(2));

        var pending = fixture.Client.RequestAsync<WindowDto[]>(IpcMessageTypes.GetWindows);
        var request = ParseRequest(await pair.ReadLineAsync());

        // Событие, втиснувшееся между запросом и ответом на него, не должно быть принято за ответ.
        await pair.SendAsync(new IpcEvent(
            IpcMessageTypes.WindowAppeared,
            IpcJson.Write(new WindowDto(0x2222, "proc", []))));
        await pair.SendAsync(new IpcEvent(IpcMessageTypes.MacrosChanged));
        await Assert.That(await WaitUntilAsync(() => fixture.Events.Count >= 2)).IsTrue();
        await Assert.That(pending.IsCompleted).IsFalse();

        await pair.SendAsync(new IpcResponse(request.Id, Ok: true, IpcJson.Write(Array.Empty<WindowDto>())));
        await Assert.That((await pending)!).IsEmpty();

        await Assert.That(fixture.Events.Select(e => e.Type))
            .IsEquivalentTo(new[] { IpcMessageTypes.WindowAppeared, IpcMessageTypes.MacrosChanged });
        await Assert.That(IpcJson.Read<WindowDto>(fixture.Events[0].Payload)!.Hwnd).IsEqualTo(0x2222);
        await Assert.That(fixture.Events[1].Payload).IsNull();
    }

    // ---- режимы отказа ---------------------------------------------------------------------

    [Test]
    public async Task NotOk_BecomesAnIpcRequestException_CarryingTheDaemonsMessage()
    {
        await using var fixture = new ClientFixture();
        var pair = fixture.Stage();
        await fixture.Client.StartAsync(TimeSpan.FromSeconds(2));

        var pending = fixture.Client.RequestAsync(IpcMessageTypes.RunMacro, new RunMacroRequest("нет-такого"));
        var request = ParseRequest(await pair.ReadLineAsync());
        await pair.SendAsync(new IpcResponse(request.Id, Ok: false, Payload: null, "Макрос 'нет-такого' не найден."));

        var thrown = await Assert.That(async () => await pending).Throws<IpcRequestException>();
        await Assert.That(thrown!.RequestType).IsEqualTo(IpcMessageTypes.RunMacro);
        await Assert.That(thrown.Message).Contains("не найден");
    }

    [Test]
    public async Task ARequestThatIsNeverAnswered_TimesOut()
    {
        await using var fixture = new ClientFixture();
        fixture.Stage();
        await fixture.Client.StartAsync(TimeSpan.FromSeconds(2));

        await Assert
            .That(async () => await fixture.Client.RequestAsync(IpcMessageTypes.GetMacros, timeout: ShortTimeout))
            .Throws<TimeoutException>();
    }

    [Test]
    public async Task RequestingWithNoConnection_FailsImmediately()
    {
        // Ничего не подложено, так что первое подключение так и не удаётся.
        await using var fixture = new ClientFixture();
        await Assert.That(await fixture.Client.StartAsync(TimeSpan.FromMilliseconds(300))).IsFalse();
        await Assert.That(fixture.Client.IsConnected).IsFalse();

        await Assert
            .That(async () => await fixture.Client.RequestAsync(IpcMessageTypes.GetWindows))
            .Throws<IpcRequestException>();
    }

    [Test]
    public async Task WhenTheDaemonHangsUp_PendingRequestsFail_AndDisconnectedIsRaised()
    {
        await using var fixture = new ClientFixture();
        var pair = fixture.Stage();
        fixture.Observe();
        await fixture.Client.StartAsync(TimeSpan.FromSeconds(2));

        var pending = fixture.Client.RequestAsync(IpcMessageTypes.GetMacros);
        await pair.ReadLineAsync();

        // Труба демона закрывается — ответа не будет уже никогда. Уронить вызов лучше, чем дать
        // ему выжечь весь свой таймаут.
        pair.CloseClient();

        await Assert.That(async () => await pending).Throws<IpcRequestException>();
        await Assert.That(await WaitUntilAsync(() => Volatile.Read(ref fixture.DisconnectedCount) >= 1)).IsTrue();
        await Assert.That(fixture.Client.IsConnected).IsFalse();
    }

    // ---- переподключение -------------------------------------------------------------------

    [Test]
    public async Task AfterAReconnect_ConnectedIsRaisedAgain_AndRequestsWorkOnTheNewConnection()
    {
        await using var fixture = new ClientFixture();
        var first = fixture.Stage();
        var second = fixture.Stage();
        fixture.Observe();

        await Assert.That(await fixture.Client.StartAsync(TimeSpan.FromSeconds(2))).IsTrue();
        await Assert.That(await WaitUntilAsync(() => Volatile.Read(ref fixture.ConnectedCount) >= 1)).IsTrue();

        first.CloseClient();

        // Connected срабатывает ВТОРОЙ раз — это тот сигнал, по которому каждая view-model
        // перезапрашивает данные: всё, что демон напушил, пока нас не было, потеряно.
        await Assert.That(await WaitUntilAsync(() => Volatile.Read(ref fixture.ConnectedCount) >= 2)).IsTrue();
        await Assert.That(await WaitUntilAsync(() => fixture.Client.IsConnected)).IsTrue();

        var pending = fixture.Client.RequestAsync<MacroGraphNames>(IpcMessageTypes.GetMacros);
        var request = ParseRequest(await second.ReadLineAsync());
        await second.SendAsync(new IpcResponse(request.Id, Ok: true, IpcJson.Write(new MacroGraphNames(["pw-boot"]))));

        await Assert.That((await pending)!.Names).IsEquivalentTo(new[] { "pw-boot" });
    }

    /// <summary>Нагрузка-заглушка: тесту важно, что по новому соединению идёт трафик, а не что именно.</summary>
    private sealed record MacroGraphNames(IReadOnlyList<string> Names);
}
