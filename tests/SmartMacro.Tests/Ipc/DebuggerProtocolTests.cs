using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Macros;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Ipc;

// D5: the debugger as it behaves over a real (in-memory) connection, with a real executor
// walking a real graph on the other end.
//
// The three things only an end-to-end test can show:
//
//   1. a breakpoint set over the wire actually parks the walker;
//   2. DROPPING THE CONNECTION with a walk parked releases it — hazard 1, and the reason the
//      attach count rides on the run-event subscription rather than living on its own;
//   3. the debugger's own events reach the panel WITHOUT waiting out the 50 ms coalescing
//      window, because a step that lags feels like a broken button.
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
                Engine.Debug,
                NullLogger<IpcServer>.Instance);
            Server.SubscribeToEngine();
            Engine.RunEvents.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Primitives = new RecordingPrimitives();
            Resolver = new DictionaryResolver();
            Executor = new MacroExecutor(
                Primitives,
                new WindowRegistry(NullLogger<WindowRegistry>.Instance),
                Resolver,
                NullLogger<MacroExecutor>.Instance);
        }

        public IpcDispatcherHarness Engine { get; }

        public IpcServer Server { get; }

        public RecordingPrimitives Primitives { get; }

        public DictionaryResolver Resolver { get; }

        public MacroExecutor Executor { get; }

        public async Task<(DuplexStreamPair Client, Task Serve)> ConnectAsync()
        {
            var expected = Server.ConnectionCount + 1;
            var pair = new DuplexStreamPair();
            var serve = Server.ServeConnectionAsync(pair.ServerInput, pair.ServerOutput);
            await WaitUntilAsync(() => Server.ConnectionCount >= expected);
            return (pair, serve);
        }

        /// <summary>A walk of <paramref name="graph"/> wired to the real publisher and debug session.</summary>
        public Task<MacroRunResult> RunAsync(MacroGraph graph, CancellationToken ct = default) =>
            Executor.RunAsync(
                graph,
                new MacroRunContext
                {
                    ContextWindow = new IntPtr(0x140804),
                    Variables = MacroVariables.ForTrigger(new ScreenPoint(1804, 902)),
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
        StartNodeId = "a",
        Nodes =
        [
            new KeyPressNode { Id = "a", Key = VirtualKey.F1, Next = "b" },
            new KeyPressNode { Id = "b", Key = VirtualKey.F2, Next = "c" },
            new KeyPressNode { Id = "c", Key = VirtualKey.F3 },
        ],
    };

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// One line off the wire, sorted: a response goes to whoever asked for that id, an event
    /// batch is appended to <see cref="Events"/>.
    ///
    /// The demultiplexing is the point, and getting it wrong is how the first draft of these
    /// tests hung: correlating a response by "read lines until the id matches" DISCARDS the
    /// run events that arrive in between, and the pause a later assertion was waiting for had
    /// already gone past. The real <c>IpcClient</c> has exactly this shape for exactly this
    /// reason — responses and events share one stream and neither may eat the other.
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

        /// <summary>Reads until <paramref name="predicate"/> holds over everything seen so far.</summary>
        public async Task<List<RunEventDto>> UntilAsync(Func<List<RunEventDto>, bool> predicate)
        {
            while (!predicate(Events))
            {
                await NextAsync();
            }
            return Events;
        }

        public async Task<DebugAckDto> CommandAsync(int id, Guid walkId, DebugCommand command, string? nodeId = null)
        {
            var reply = await RequestAsync(id, IpcMessageTypes.DebugCommand, new DebugCommandRequest(walkId, command, nodeId));
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

    // ---- breakpoints over the wire ---------------------------------------------------

    [Test]
    public async Task BreakpointsCanBeSetWhileNothingIsRunning_AndAreReadBackWhole()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);

        // Arming before pressing Run is the normal way to use one, so this must not require a
        // live walk — or even a saved macro.
        await reader.RequestAsync(1, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["b", "c"]));
        var reply = await reader.RequestAsync(2, IpcMessageTypes.GetBreakpoints);

        var sets = IpcJson.Read<BreakpointSetDto[]>(reply.GetProperty("Payload"))!;
        await Assert.That(sets).Count().IsEqualTo(1);
        await Assert.That(sets[0].MacroName).IsEqualTo("pw-boot");
        await Assert.That(sets[0].NodeIds).IsEquivalentTo(new[] { "b", "c" });

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task AnEmptyBreakpointSetClearsTheMacroEntirely()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);

        await reader.RequestAsync(1, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["b"]));
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", []));
        var reply = await reader.RequestAsync(3, IpcMessageTypes.GetBreakpoints);

        await Assert.That(IpcJson.Read<BreakpointSetDto[]>(reply.GetProperty("Payload"))!).IsEmpty();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task BreakpointsSurviveAClientReconnecting()
    {
        // The ergonomic reason breakpoints live in the daemon rather than in the macro file:
        // the daemon outlives the panel, so "session-scoped" already means "survives a panel
        // restart", which is the thing anyone actually wanted from persistence.
        await using var fixture = new Fixture();
        var (first, firstServe) = await fixture.ConnectAsync();
        await new Reader(first).RequestAsync(1, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["b"]));
        first.CloseClient();
        await firstServe;

        var (second, secondServe) = await fixture.ConnectAsync();
        var reply = await new Reader(second).RequestAsync(1, IpcMessageTypes.GetBreakpoints);

        await Assert.That(IpcJson.Read<BreakpointSetDto[]>(reply.GetProperty("Payload"))!.Single().NodeIds)
            .IsEquivalentTo(new[] { "b" });

        second.CloseClient();
        await secondServe;
    }

    // ---- the round trip: park, step, resume ------------------------------------------

    [Test]
    public async Task ABreakpointParksTheWalk_AndTheStepCommandAdvancesItOneNode()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["a"]));

        var run = fixture.RunAsync(Chain());

        var events = await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));
        var hit = events.Single(e => e.Kind == RunEventKind.BreakpointHit);
        await Assert.That(hit.NodeId).IsEqualTo("a");
        await Assert.That(hit.Detail).IsEqualTo("брейкпоинт");
        await Assert.That(fixture.Primitives.Calls).IsEmpty();

        var ack = await reader.CommandAsync(3, hit.WalkId, DebugCommand.Step);
        await Assert.That(ack.Accepted).IsTrue();

        events = await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.Paused));
        var stepped = events.Single(e => e.Kind == RunEventKind.Paused);
        await Assert.That(stepped.NodeId).IsEqualTo("b");
        await Assert.That(stepped.Detail).IsEqualTo("шаг");
        await Assert.That(events.Any(e => e.Kind == RunEventKind.Resumed && e.NodeId == "a")).IsTrue();

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
            StartNodeId = "recognize-class",
            Nodes =
            [
                new RecognizeTagNode
                {
                    Id = "recognize-class",
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
        // cursor comes from the trigger and names no node; tag comes from the node that wrote it.
        var cursor = vars.Single(e => e.Variable == MacroVariableNames.Cursor);
        await Assert.That(cursor.NodeId).IsNull();
        await Assert.That(cursor.Detail).IsEqualTo(new ScreenPoint(1804, 902).ToString());

        var tag = vars.Single(e => e.Variable == "tag");
        await Assert.That(tag.NodeId).IsEqualTo("recognize-class");
        await Assert.That(tag.Detail).IsEqualTo("Жрец");

        client.CloseClient();
        await serve;
    }

    // ---- hazard 1: the panel goes away with a walk parked ------------------------------

    [Test]
    public async Task DroppingTheConnectionReleasesAPausedWalk()
    {
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["b"]));

        var run = fixture.RunAsync(Chain());
        await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));
        await Assert.That(fixture.Engine.Debug.AttachedCount).IsEqualTo(1);

        // The panel crashed / the user closed the window. Nothing is left that could press
        // resume, and a walk left in the gate would hold its macro's single-flight slot — so
        // that macro's hotkey would be dead until the daemon restarted.
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
        // The same hazard by the other route: the panel is still there but has left «Макросы»,
        // which is where the subscription is scoped. It can no longer show the pause, so it
        // must no longer be able to hold one.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["b"]));

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
        // The attach count is the safety property; a command from outside it could park a walk
        // that nothing is counted as able to release.
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
        // Hazard 3, engine half: ■ Стоп cancels the RUN's token, and a parked walk has to come
        // out of the gate rather than sit there holding it.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["b"]));

        using var cts = new CancellationTokenSource();
        var run = fixture.RunAsync(Chain(), cts.Token);
        await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));

        await cts.CancelAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Cancelled);

        client.CloseClient();
        await serve;
    }

    // ---- latency ------------------------------------------------------------------------

    [Test]
    public async Task ADebuggerEventDoesNotWaitOutTheCoalescingWindow()
    {
        // The batching window is 50 ms and is right for a log. For a step it is the difference
        // between an instant button and a sticky one, so pause/resume skip the dwell. Measured
        // generously — the assertion is "not a full dwell", not a benchmark.
        await using var fixture = new Fixture();
        var (client, serve) = await fixture.ConnectAsync();
        var reader = new Reader(client);
        await reader.SubscribeAsync(1);
        await reader.RequestAsync(2, IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest("pw-boot", ["a"]));

        var started = Environment.TickCount64;
        var run = fixture.RunAsync(Chain());
        var events = await reader.UntilAsync(all => all.Any(e => e.Kind == RunEventKind.BreakpointHit));
        var elapsed = Environment.TickCount64 - started;

        await Assert.That(elapsed).IsLessThan(45);

        await reader.CommandAsync(3, events.Single(e => e.Kind == RunEventKind.BreakpointHit).WalkId, DebugCommand.Resume);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        client.CloseClient();
        await serve;
    }
}
