using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;

namespace SmartMacro.Tests.Ipc;

// D3b: the run-event channel as it behaves on a real (in-memory) connection.
//
// The thing under test is not "does an event arrive" — it is the flooding contract, because
// that is what decides whether the panel is still connected at the moment the user cares.
// The server hands each connection a bounded queue of 256 and DROPS a client that fills it,
// so an unmediated stream would disconnect the panel in the middle of the fan-out it was
// opened to watch. Three mechanisms answer that, and each has a test here:
//
//   1. nothing is produced unless a connection subscribed;
//   2. what is produced is coalesced into batches;
//   3. overflow is counted and reported, never silently swallowed and never blocking.
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

        /// <summary>Subscribes a client and returns the walks the daemon said were already running.</summary>
        public async Task<RunWalkDto[]> SubscribeAsync(DuplexStreamPair client, int id = 1, bool enabled = true)
        {
            await client.SendAsync(new IpcRequest(id, IpcMessageTypes.SubscribeRunEvents, IpcJson.Write(new SubscribeRunEventsRequest(enabled))));
            var reply = Parse(await client.ReadLineAsync());
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

    /// <summary>Drives the observer the way a walk of <paramref name="nodes"/> would.</summary>
    private static Guid Walk(RunEventPublisher publisher, string macroName, long hwnd, params string[] nodes)
    {
        var walkId = Guid.NewGuid();
        publisher.WalkStarted(new MacroWalkStart(walkId, Guid.NewGuid(), macroName, hwnd == 0 ? null : new IntPtr(hwnd), 0));
        foreach (var node in nodes)
        {
            publisher.NodeEntered(walkId, 0, node);
            publisher.NodeExited(walkId, 1, node, RunOutcomes.Ok, "деталь", 1);
        }
        return walkId;
    }

    // ---- opt-in ----------------------------------------------------------------------

    [Test]
    public async Task WithNoSubscriber_TheEngineIsNotEvenInstrumented()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        await Assert.That(fixture.Publisher.IsEnabled).IsFalse();
        Walk(fixture.Publisher, "pw-boot", 0x10, "a", "b");

        // Not "the events were filtered out" — they were never produced. The only line this
        // connection can get is the reply to a request sent afterwards.
        await client.SendAsync(new IpcRequest(7, IpcMessageTypes.GetWindows));
        var line = Parse(await client.ReadLineAsync());
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
        var evt = Parse(await client.ReadLineAsync());
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
        // A leaked reference here would leave the executor instrumented forever with nobody
        // reading, which is the exact cost the opt-in exists to avoid.
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

        await Assert.That(TypeOf(Parse(await watcher.ReadLineAsync()))).IsEqualTo(IpcMessageTypes.RunEvents);

        // The bystander gets ordinary events and nothing else. Handing it the burst would
        // cost it its connection for a stream it has no use for.
        fixture.Engine.Windows.Register(0x99, "elementclient");
        var seen = Parse(await bystander.ReadLineAsync());
        await Assert.That(TypeOf(seen)).IsEqualTo(IpcMessageTypes.WindowAppeared);

        watcher.CloseClient();
        bystander.CloseClient();
        await Task.WhenAll(watcherServe, bystanderServe);
    }

    // ---- mid-run subscription --------------------------------------------------------

    [Test]
    public async Task SubscribingMidRun_AnswersWithTheLiveWalks_FlaggedAsIncomplete()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();

        // Two walks already going before anybody was listening.
        var first = Walk(fixture.Publisher, "pw-boot", 0x140804);
        Walk(fixture.Publisher, "pw-assist", 0);

        var live = await fixture.SubscribeAsync(client);

        await Assert.That(live).Count().IsEqualTo(2);
        await Assert.That(live.Select(w => w.MacroName)).IsEquivalentTo(new[] { "pw-boot", "pw-assist" });
        // The whole point: their leading node rows happened while nothing was recording, and
        // the protocol says so rather than letting the panel render the tail as a full log.
        await Assert.That(live.All(w => !w.FromStart)).IsTrue();
        await Assert.That(live.Single(w => w.MacroName == "pw-boot").Hwnd).IsEqualTo(0x140804L);
        await Assert.That(live.Single(w => w.MacroName == "pw-assist").Hwnd).IsEqualTo(0L);

        // A walk that has since finished is not offered.
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

        var batch = IpcJson.Read<RunEventBatch>(Parse(await client.ReadLineAsync()).GetProperty("Payload"))!;
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

    // ---- the burst -------------------------------------------------------------------

    [Test]
    public async Task ATenWindowFanOutIsCoalesced_AndThePanelIsStillConnectedAfterwards()
    {
        const int Walks = 10;
        const int NodesPerWalk = 12;
        // 10 × (1 walk-start + 12 × 2 node events + 1 walk-end) = 260 events. Unbatched that
        // is already past the connection's 256-event queue — which is the failure this
        // wave had to design around, and it would land exactly when the user is watching.
        const int Expected = Walks * (2 + (NodesPerWalk * 2));

        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        await fixture.SubscribeAsync(client);

        var nodes = Enumerable.Range(0, NodesPerWalk).Select(i => $"n{i}").ToArray();
        // Produced from ten threads at once, the way a RunMacroNode fan-out actually does it.
        await Task.WhenAll(Enumerable.Range(0, Walks).Select(i => Task.Run(() =>
        {
            var walkId = Walk(fixture.Publisher, "pw-identify-one", 0x100 + i, nodes);
            fixture.Publisher.WalkFinished(walkId, 100, RunOutcomes.Completed, null);
        })));

        var events = new List<RunEventDto>();
        var envelopes = 0;
        var dropped = 0;
        var deadline = Environment.TickCount64 + 15000;
        while (events.Count + dropped < Expected && Environment.TickCount64 < deadline)
        {
            var line = Parse(await client.ReadLineAsync(timeoutMs: 5000));
            await Assert.That(TypeOf(line)).IsEqualTo(IpcMessageTypes.RunEvents);
            var batch = IpcJson.Read<RunEventBatch>(line.GetProperty("Payload"))!;
            events.AddRange(batch.Events);
            dropped += batch.Dropped;
            envelopes++;
        }

        await Assert.That(events.Count + dropped).IsEqualTo(Expected);
        // The queue is 4096 deep, so a burst this size loses nothing.
        await Assert.That(dropped).IsEqualTo(0);
        // The coalescing claim, stated as a number: hundreds of events, a handful of lines
        // on the wire — comfortably under the 256 that would cost the panel its connection.
        await Assert.That(envelopes).IsLessThan(64);
        await Assert.That(events.Count(e => e.Kind == RunEventKind.WalkStarted)).IsEqualTo(Walks);
        await Assert.That(events.Select(e => e.WalkId).Distinct()).Count().IsEqualTo(Walks);

        // And the whole point of the exercise — the connection survived it.
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(1);
        await client.SendAsync(new IpcRequest(99, IpcMessageTypes.GetWindows));
        var reply = Parse(await client.ReadLineAsync());
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

        // Deliberately past the publisher's own 4096-event queue. The engine must not block
        // — a macro run is between two Win32 messages — so the surplus is dropped, and the
        // count rides out with the next batch so the panel can admit the hole.
        var walkId = Guid.NewGuid();
        fixture.Publisher.WalkStarted(new MacroWalkStart(walkId, Guid.NewGuid(), "шторм", new IntPtr(0x1), 0));
        for (var i = 0; i < 20_000; i++)
        {
            fixture.Publisher.NodeEntered(walkId, i, "n");
        }

        var dropped = 0;
        var deadline = Environment.TickCount64 + 15000;
        while (dropped == 0 && Environment.TickCount64 < deadline)
        {
            var batch = IpcJson.Read<RunEventBatch>(Parse(await client.ReadLineAsync(timeoutMs: 5000)).GetProperty("Payload"))!;
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

        // Whatever was queued was addressed to a subscriber that is gone. Delivering it to
        // the next one would open its log with a burst from a run it never saw.
        await fixture.SubscribeAsync(client, id: 3);
        Walk(fixture.Publisher, "pw-assist", 0x2, "z");

        var batch = IpcJson.Read<RunEventBatch>(Parse(await client.ReadLineAsync(timeoutMs: 5000)).GetProperty("Payload"))!;
        await Assert.That(batch.Events.Select(e => e.NodeId).Where(id => id is not null)).IsEquivalentTo(new[] { "z", "z" });

        client.CloseClient();
        await serve;
    }
}
