using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Ipc;

// Stage 2B: the server's own responsibilities — correlation, the response/event split,
// multi-client fan-out, and surviving a client that dies mid-broadcast.
//
// Every connection here is a pair of in-memory streams (see InMemoryDuplex.cs). No
// NamedPipeServerStream is created anywhere in the suite: it would need a global name
// (killing parallelism), a desktop session, and it makes "the peer is gone" hard to stage.
// ServeConnectionAsync is the seam that makes that possible — below it, the production
// path and the test path are the same code.
public class IpcServerTests
{
    private sealed class ServerFixture : IAsyncDisposable
    {
        public ServerFixture()
        {
            Engine = new IpcDispatcherHarness();
            Server = new IpcServer(
                Engine.Dispatcher,
                Engine.Windows,
                Engine.Macros,
                Engine.Runs,
                NullLogger<IpcServer>.Instance);
            Server.SubscribeToEngine();
        }

        public IpcDispatcherHarness Engine { get; }

        public IpcServer Server { get; }

        /// <summary>Opens a client, waits until the server has registered it, and returns both halves.</summary>
        public async Task<(DuplexStreamPair Client, Task Serve)> ConnectAsync()
        {
            var expected = Server.ConnectionCount + 1;
            var pair = new DuplexStreamPair();
            var serve = Server.ServeConnectionAsync(pair.ServerInput, pair.ServerOutput);
            await WaitUntilAsync(() => Server.ConnectionCount >= expected);
            return (pair, serve);
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

    // ------------------------------------------------------------------- correlation

    [Test]
    public async Task PipelinedRequests_GetRepliesCarryingTheirOwnIds()
    {
        await using var fixture = new ServerFixture();
        fixture.Engine.Windows.Register(0x10, "elementclient");
        var (client, serve) = await fixture.ConnectAsync();

        // Both go out before either reply comes back: the handler loop does not wait for
        // one request to finish before reading the next, so replies may arrive in either
        // order and the Id is the only thing tying them to a caller.
        await client.SendAsync(new IpcRequest(11, IpcMessageTypes.GetWindows));
        await client.SendAsync(new IpcRequest(22, IpcMessageTypes.GetRunningMacros));

        var replies = (await client.ReadLinesAsync(2)).Select(Parse).ToList();

        await Assert.That(replies.Select(r => r.GetProperty("Id").GetInt32()).OrderBy(id => id))
            .IsEquivalentTo(new[] { 11, 22 });
        await Assert.That(replies.All(r => r.GetProperty("Ok").GetBoolean())).IsTrue();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task Events_CarryNoId_SoTheyCanNeverBeMistakenForAReply()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        await client.SendAsync(new IpcRequest(5, IpcMessageTypes.GetWindows));
        await Assert.That(Parse(await client.ReadLineAsync()).GetProperty("Id").GetInt32()).IsEqualTo(5);

        fixture.Engine.Windows.Register(0x77, "elementclient");

        var evt = Parse(await client.ReadLineAsync());
        await Assert.That(evt.TryGetProperty("Id", out _)).IsFalse();
        await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.WindowAppeared);

        client.CloseClient();
        await serve;
    }

    // ------------------------------------------------------------------- engine events

    [Test]
    public async Task WindowLifecycle_IsPushedAsAppeared_TagsChanged_Closed()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        fixture.Engine.Windows.Register(0x123, "elementclient");
        fixture.Engine.Windows.AddTag(0x123, "Лучник");
        fixture.Engine.Windows.Unregister(0x123);

        var events = (await client.ReadLinesAsync(3)).Select(Parse).ToList();

        await Assert.That(events.Select(TypeOf)).IsEquivalentTo(new[]
        {
            IpcMessageTypes.WindowAppeared,
            IpcMessageTypes.WindowTagsChanged,
            IpcMessageTypes.WindowClosed,
        });

        var appeared = IpcJson.Read<WindowDto>(events[0].GetProperty("Payload"))!;
        await Assert.That(appeared.Hwnd).IsEqualTo(0x123L);
        await Assert.That(appeared.Tags).IsEmpty();

        // Tag events carry the full new state, not a delta.
        var tagged = IpcJson.Read<WindowDto>(events[1].GetProperty("Payload"))!;
        await Assert.That(tagged.Tags).IsEquivalentTo(new[] { "Лучник" });

        // Nothing survives the window but its handle.
        await Assert.That(IpcJson.Read<WindowClosedEvent>(events[2].GetProperty("Payload")))
            .IsEqualTo(new WindowClosedEvent(0x123));

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task LibraryChange_IsPushedAsPayloadlessMacrosChanged()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        await fixture.Engine.Macros.SaveAsync(new MacroGraph
        {
            Name = "новый",
            StartNodeId = "n0",
            Nodes = [new KeyPressNode { Id = "n0", Key = VirtualKey.F3, Target = new TargetSelector() }],
        });

        var evt = Parse(await client.ReadLineAsync());

        await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.MacrosChanged);
        // No payload by protocol — the library can be big, the client re-fetches.
        await Assert.That(evt.TryGetProperty("Payload", out _)).IsFalse();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task RunRegistryChange_IsPushedWithTheFreshSnapshot()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        var handle = fixture.Engine.Runs.TryBegin("бут")!;
        var started = Parse(await client.ReadLineAsync());
        var runs = IpcJson.Read<RunningMacroDto[]>(started.GetProperty("Payload"))!;
        await Assert.That(TypeOf(started)).IsEqualTo(IpcMessageTypes.RunningMacrosChanged);
        await Assert.That(runs).Count().IsEqualTo(1);
        await Assert.That(runs[0].RunId).IsEqualTo(handle.RunId);

        fixture.Engine.Runs.Complete(handle.RunId);
        var finished = Parse(await client.ReadLineAsync());
        await Assert.That(IpcJson.Read<RunningMacroDto[]>(finished.GetProperty("Payload"))!).IsEmpty();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task UnsubscribeFromEngine_SilencesPushes()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        fixture.Server.UnsubscribeFromEngine();
        fixture.Engine.Windows.Register(0x1, "elementclient");

        // Nothing should have been queued, so the only line we can get is the reply to a
        // request sent afterwards — if an event had slipped through it would be first.
        await client.SendAsync(new IpcRequest(3, IpcMessageTypes.GetWindows));
        var line = Parse(await client.ReadLineAsync());
        await Assert.That(line.GetProperty("Id").GetInt32()).IsEqualTo(3);

        client.CloseClient();
        await serve;
    }

    // ------------------------------------------------------------------ multi-client

    [Test]
    public async Task OneEvent_ReachesEveryConnectedClient()
    {
        await using var fixture = new ServerFixture();
        var (first, serveFirst) = await fixture.ConnectAsync();
        var (second, serveSecond) = await fixture.ConnectAsync();
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(2);

        fixture.Engine.Windows.Register(0x42, "elementclient");

        foreach (var client in new[] { first, second })
        {
            var evt = Parse(await client.ReadLineAsync());
            await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.WindowAppeared);
            await Assert.That(IpcJson.Read<WindowDto>(evt.GetProperty("Payload"))!.Hwnd).IsEqualTo(0x42L);
        }

        first.CloseClient();
        second.CloseClient();
        await Task.WhenAll(serveFirst, serveSecond);
    }

    [Test]
    public async Task ClientThatDiesMidBroadcast_IsDropped_AndTheRestKeepReceiving()
    {
        await using var fixture = new ServerFixture();
        var (dying, serveDying) = await fixture.ConnectAsync();
        var (survivor, serveSurvivor) = await fixture.ConnectAsync();

        // Only the daemon→client direction breaks: the server's reader is still parked, so
        // it learns about the death from the failed PUSH, which is the case that has to not
        // take the server (or the other client) with it.
        dying.BreakServerWrites();

        fixture.Engine.Windows.Register(0x50, "elementclient");

        await Assert.That(TypeOf(Parse(await survivor.ReadLineAsync()))).IsEqualTo(IpcMessageTypes.WindowAppeared);
        await Assert.That(await WaitUntilAsync(() => fixture.Server.ConnectionCount == 1)).IsTrue();

        // The server is still fully functional afterwards: more events, and requests too.
        fixture.Engine.Windows.AddTag(0x50, "Жрец");
        await Assert.That(TypeOf(Parse(await survivor.ReadLineAsync()))).IsEqualTo(IpcMessageTypes.WindowTagsChanged);

        await survivor.SendAsync(new IpcRequest(9, IpcMessageTypes.GetWindows));
        var reply = Parse(await survivor.ReadLineAsync());
        await Assert.That(reply.GetProperty("Id").GetInt32()).IsEqualTo(9);
        await Assert.That(reply.GetProperty("Ok").GetBoolean()).IsTrue();

        await serveDying;
        survivor.CloseClient();
        await serveSurvivor;
    }

    [Test]
    public async Task ClientDisconnect_RemovesItFromTheConnectionSet()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(1);

        client.CloseClient();
        await serve;

        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(0);
    }

    // -------------------------------------------------------------------- robustness

    [Test]
    public async Task MalformedLine_IsSkipped_AndTheConnectionKeepsServing()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        await client.SendLineAsync("{\"Id\": не число}");
        await client.SendAsync(new IpcRequest(4, IpcMessageTypes.GetWindows));

        var reply = Parse(await client.ReadLineAsync());
        await Assert.That(reply.GetProperty("Id").GetInt32()).IsEqualTo(4);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task ConcurrentEventBurstAndReplies_ProduceWellFormedNewlineDelimitedJson()
    {
        const int Requests = 25;
        const int Events = 25;

        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        // Two producers racing on ONE connection: the event pump and the request handlers.
        // Without IpcConnection's write lock this is where the output turns into spliced
        // half-messages (or a "stream in use" throw), and every Parse below fails.
        var pushes = Task.Run(() =>
        {
            for (var i = 0; i < Events; i++)
            {
                fixture.Server.Broadcast(new IpcEvent(
                    IpcMessageTypes.WindowClosed,
                    IpcJson.Write(new WindowClosedEvent(i))));
            }
        });
        var sends = Task.Run(async () =>
        {
            for (var i = 0; i < Requests; i++)
            {
                await client.SendAsync(new IpcRequest(i, IpcMessageTypes.GetWindows));
            }
        });
        await Task.WhenAll(pushes, sends);

        var lines = await client.ReadLinesAsync(Requests + Events, timeoutMs: 15000);

        var ids = new HashSet<int>();
        var hwnds = new HashSet<long>();
        foreach (var element in lines.Select(Parse))
        {
            if (element.TryGetProperty("Id", out var id))
            {
                await Assert.That(element.GetProperty("Ok").GetBoolean()).IsTrue();
                ids.Add(id.GetInt32());
            }
            else
            {
                hwnds.Add(IpcJson.Read<WindowClosedEvent>(element.GetProperty("Payload"))!.Hwnd);
            }
        }

        await Assert.That(ids).Count().IsEqualTo(Requests);
        await Assert.That(hwnds).Count().IsEqualTo(Events);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task Broadcast_WithNoClients_IsHarmless()
    {
        await using var fixture = new ServerFixture();

        // The engine raises events all day whether or not a panel is attached; the fan-out
        // has to be a no-op then, not a null-reference.
        fixture.Engine.Windows.Register(0x1, "elementclient");
        fixture.Server.Broadcast(new IpcEvent(IpcMessageTypes.ActivateWindow));

        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(0);
    }

    [Test]
    public async Task StopAsync_ClosesLiveConnections()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        // No StartAsync in this test — that would open a real pipe. StopAsync's other job,
        // unwinding whatever is connected, is exercised on the in-memory connection.
        await fixture.Server.StopAsync(CancellationToken.None);
        await serve;

        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(0);
        client.Dispose();
    }
}
