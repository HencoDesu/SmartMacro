using System.Text.Json;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

// Stage 3: the client half of the protocol — correlation, the response/event split,
// failure propagation, and reconnection.
//
// The client under test sits on the SERVER halves of a DuplexStreamPair, which reads
// backwards for a second and then makes sense: the pair models "one peer plus a test
// harness", and here the peer being exercised is the client. So pair.SendAsync feeds the
// client a line, pair.ReadLineAsync picks up what the client wrote, and pair.CloseClient
// gives it the EOF a stopped daemon would.
public class IpcClientTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Drives an <see cref="IpcClient"/> over one or more staged in-memory connections.</summary>
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

        /// <summary>Adds a connection the client will get on its next (re)connect.</summary>
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
                // Nothing left to hand out: the maintain loop treats this as "the daemon
                // isn't there" and backs off, which is exactly the state we want at teardown.
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

    // ---- correlation ---------------------------------------------------------------------

    [Test]
    public async Task Responses_AreMatchedById_EvenWhenTheyArriveOutOfOrder()
    {
        await using var fixture = new ClientFixture();
        var pair = fixture.Stage();
        await Assert.That(await fixture.Client.StartAsync(TimeSpan.FromSeconds(2))).IsTrue();

        // Two calls in flight at once. The server is explicitly allowed to answer the second
        // one first (it doesn't await handlers), so the client may not rely on arrival order.
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

        // An event squeezed in between the request and its reply must not be mistaken for one.
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

    // ---- failure modes -------------------------------------------------------------------

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
        // Nothing staged, so the initial connect never succeeds.
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

        // The daemon's pipe closes — no reply will ever come. Failing the call beats letting
        // it burn its whole timeout.
        pair.CloseClient();

        await Assert.That(async () => await pending).Throws<IpcRequestException>();
        await Assert.That(await WaitUntilAsync(() => Volatile.Read(ref fixture.DisconnectedCount) >= 1)).IsTrue();
        await Assert.That(fixture.Client.IsConnected).IsFalse();
    }

    // ---- reconnection --------------------------------------------------------------------

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

        // Connected fires a SECOND time — the signal every view-model re-fetches on, because
        // whatever the daemon pushed while we were away is gone.
        await Assert.That(await WaitUntilAsync(() => Volatile.Read(ref fixture.ConnectedCount) >= 2)).IsTrue();
        await Assert.That(await WaitUntilAsync(() => fixture.Client.IsConnected)).IsTrue();

        var pending = fixture.Client.RequestAsync<MacroGraphNames>(IpcMessageTypes.GetMacros);
        var request = ParseRequest(await second.ReadLineAsync());
        await second.SendAsync(new IpcResponse(request.Id, Ok: true, IpcJson.Write(new MacroGraphNames(["pw-boot"]))));

        await Assert.That((await pending)!.Names).IsEquivalentTo(new[] { "pw-boot" });
    }

    /// <summary>Stand-in payload — this test cares that the new connection carries traffic, not what it carries.</summary>
    private sealed record MacroGraphNames(IReadOnlyList<string> Names);
}
