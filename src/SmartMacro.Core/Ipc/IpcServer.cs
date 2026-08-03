using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Windows;

namespace SmartMacro.Ipc;

/// <summary>
/// The daemon's control endpoint: a named pipe (<see cref="PipeName"/>) speaking JSON
/// Lines, serving several UI clients at once, dispatching their requests through
/// <see cref="IpcRequestDispatcher"/> and pushing engine events at all of them.
///
/// <b>Shape.</b> Three layers, split so that only the outermost one needs a real pipe:
/// <see cref="IpcConnection"/> does framing and write serialisation over any
/// <see cref="Stream"/>; <see cref="IpcRequestDispatcher"/> turns an envelope into an
/// envelope; this class owns the accept loop, the live-connection set and event fan-out.
/// <see cref="ServeConnectionAsync(Stream, CancellationToken)"/> is the seam: the accept
/// loop calls it with a pipe, the tests call it with in-memory halves, and everything
/// below the seam is identical in both cases.
///
/// <b>Per-connection concurrency.</b> Each connection runs two tasks: a reader loop and an
/// event pump. The reader loop does NOT await a handler before reading the next request —
/// <c>StopMacro</c> waits for a runner to acknowledge and <c>DumpCaptures</c> screenshots
/// every window, and neither may head-of-line-block the panel's other traffic. Responses
/// can therefore come back out of order, which is exactly what
/// <see cref="IpcRequest.Id"/> is for.
///
/// <b>Event delivery: one bounded queue per connection.</b> Engine events are raised on
/// engine threads (a macro node adding a tag, the store's watcher reloading), so the
/// handlers here do nothing but a non-blocking <c>TryWrite</c> into each client's queue and
/// return. A client that stops draining fills its queue, the <c>TryWrite</c> fails, and
/// that connection is DROPPED rather than allowed to slow the producer down — the UI
/// reconnects and re-fetches a fresh snapshot, which is both cheaper and more correct than
/// a UI catching up through a backlog. A single global queue was the alternative and is
/// worse: one wedged client would stall event delivery to every other client.
/// </summary>
public sealed partial class IpcServer : IHostedService, IAsyncDisposable, IIpcBroadcaster
{
    /// <summary>
    /// Pipe name — taken from <see cref="IpcPipe.Name"/> in Contracts, which is the only
    /// place either end may define it. The UI shares no assembly with this one.
    /// </summary>
    public const string PipeName = IpcPipe.Name;

    // The UI is normally a single client; the headroom is for a debug console attached
    // alongside it, and for the window between a UI crashing and Windows reclaiming its
    // handle. Beyond this the accept loop backs off and retries instead of failing.
    private const int MaxServerInstances = IpcPipe.MaxServerInstances;

    // Events per connection before we give up on it. A UI that has not drained 256 events
    // is not slow, it is gone (or deadlocked), and the reconnect path handles both.
    private const int EventQueueCapacity = 256;

    private const int PipeBufferBytes = 64 * 1024;
    private const int AcceptRetryDelayMs = 500;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IpcRequestDispatcher _dispatcher;
    private readonly WindowRegistry _windows;
    private readonly MacroGraphStore _macros;
    private readonly MacroRunRegistry _runs;
    private readonly RunEventPublisher _runEvents;
    private readonly ILogger<IpcServer> _logger;

    private readonly ConcurrentDictionary<ClientConnection, byte> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _subscriptionLock = new();

    private bool _subscribed;
    private Task? _acceptLoop;
    private int _disposed;

    public IpcServer(
        IpcRequestDispatcher dispatcher,
        WindowRegistry windows,
        MacroGraphStore macros,
        MacroRunRegistry runs,
        RunEventPublisher runEvents,
        ILogger<IpcServer> logger)
    {
        _dispatcher = dispatcher;
        _windows = windows;
        _macros = macros;
        _runs = runs;
        _runEvents = runEvents;
        _logger = logger;

        // Hand ourselves to the dispatcher so RequestActivate has something to broadcast
        // through. Done here rather than by DI because the dependency is genuinely circular
        // (server → dispatcher → server) and this is the end of it that already holds the
        // other object.
        dispatcher.AttachBroadcaster(this);
        // Same cycle, same resolution: the run-event pump pushes through us, and we hold it
        // so each connection can flip its own subscription on and off.
        runEvents.AttachBroadcaster(this);
    }

    /// <summary>Number of clients currently connected. Diagnostics and tests.</summary>
    public int ConnectionCount => _clients.Count;

    // ---------------------------------------------------------------- hosted lifecycle

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeToEngine();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token), CancellationToken.None);
        LogListening(PipeName, MaxServerInstances);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeFromEngine();
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_acceptLoop is { } loop)
        {
            // The loop is parked in WaitForConnectionAsync; cancelling unblocks it. The
            // timeout is belt-and-braces for a pipe that refuses to cancel.
            try
            {
                await loop.WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                LogAcceptLoopDidNotStop();
            }
            _acceptLoop = null;
        }

        // Every live connection is dropped, which closes its pipe — the signal the panel's
        // client turns into "служба остановлена" (and an exit) instead of a silent hang.
        foreach (var client in _clients.Keys)
        {
            client.Drop();
        }
        await WaitForClientsAsync(cancellationToken).ConfigureAwait(false);
        LogStopped();
    }

    /// <summary>
    /// Wires the engine's change events to the broadcast fan-out. Idempotent.
    ///
    /// Public and separate from <see cref="StartAsync"/> because the protocol tests need
    /// the event wiring WITHOUT a named pipe: they subscribe, drive a connection over
    /// in-memory streams, and mutate the registry directly.
    /// </summary>
    public void SubscribeToEngine()
    {
        lock (_subscriptionLock)
        {
            if (_subscribed)
            {
                return;
            }
            _subscribed = true;
            _windows.WindowAppeared += OnWindowAppeared;
            _windows.WindowTagsChanged += OnWindowTagsChanged;
            _windows.WindowClosed += OnWindowClosed;
            _macros.MacrosChanged += OnMacrosChanged;
            _runs.RunsChanged += OnRunsChanged;
        }
    }

    /// <summary>Detaches every engine subscription. Idempotent.</summary>
    public void UnsubscribeFromEngine()
    {
        lock (_subscriptionLock)
        {
            if (!_subscribed)
            {
                return;
            }
            _subscribed = false;
            _windows.WindowAppeared -= OnWindowAppeared;
            _windows.WindowTagsChanged -= OnWindowTagsChanged;
            _windows.WindowClosed -= OnWindowClosed;
            _macros.MacrosChanged -= OnMacrosChanged;
            _runs.RunsChanged -= OnRunsChanged;
        }
    }

    // ------------------------------------------------------------------- accept loop

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
                return;
            }
            catch (Exception ex)
            {
                // Usually "all pipe instances are busy" (MaxServerInstances reached) — back
                // off until a slot frees. Anything else (ACL trouble, a name collision with
                // a second daemon that beat the mutex) also lands here and is worth a line.
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
                LogAcceptFailed(ex, PipeName);
                try
                {
                    await Task.Delay(AcceptRetryDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            var accepted = pipe;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ServeConnectionAsync(accepted, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogConnectionFaulted(ex);
                    }
                    finally
                    {
                        await accepted.DisposeAsync().ConfigureAwait(false);
                    }
                },
                CancellationToken.None);
        }
    }

    private static NamedPipeServerStream CreatePipe() =>
        NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: PipeBufferBytes,
            outBufferSize: PipeBufferBytes,
            CreatePipeSecurity());

    /// <summary>
    /// One ACE: the user running the daemon gets full control. Nothing else — not
    /// Administrators, not SYSTEM — needs to talk to this pipe.
    ///
    /// This is deliberately the simple case, because today BOTH processes run elevated as
    /// the same interactive user (the ⚠ QUESTIONABLE elevation item in the split plan). If
    /// the UI is ever de-elevated, the DACL below still matches — the user SID is identical
    /// in the filtered and the full token — but the pipe would ALSO inherit the daemon's
    /// High mandatory integrity label, and Windows' mandatory policy blocks a
    /// Medium-integrity client from opening it for write. Fixing that means adding a SACL
    /// with a Medium (or Low) <c>SYSTEM_MANDATORY_LABEL</c> ACE, and — more importantly —
    /// deciding that a less-privileged peer is still allowed to drive elevated game
    /// windows. That is a security decision, not a plumbing one, so it is not pre-empted here.
    /// </summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        // identity.User is null only for exotic tokens (anonymous / no user SID); the
        // account name is the fallback the OS can still resolve.
        IdentityReference user = (IdentityReference?)identity.User ?? new NTAccount(identity.Name);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // --------------------------------------------------------------- one connection

    /// <summary>
    /// Serves one already-connected duplex stream until the peer disconnects, the stream
    /// breaks, or the server stops. Never throws for a peer-side failure.
    /// </summary>
    public Task ServeConnectionAsync(Stream duplex, CancellationToken cancellationToken = default) =>
        ServeConnectionAsync(duplex, duplex, cancellationToken);

    /// <summary>
    /// Split-stream overload — the shape an in-memory test pair has (and what an anonymous
    /// pipe pair would need too). <paramref name="input"/> and <paramref name="output"/>
    /// are left open; the caller owns them.
    /// </summary>
    public async Task ServeConnectionAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var client = new ClientConnection(new IpcConnection(input, output, leaveOpen: true), _runEvents);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token,
            client.Cts.Token);

        _clients.TryAdd(client, 0);
        LogClientConnected(_clients.Count);

        // Started before the reader loop so an event raised while the first request is
        // still being parsed is queued, not lost.
        var pump = PumpEventsAsync(client, linked.Token);
        try
        {
            await ReadLoopAsync(client, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(client, out _);
            // Before anything else: a client that died without unsubscribing must not leave
            // the executor instrumented for the rest of the daemon's life.
            client.SetRunEventSubscription(false);
            client.CompleteEvents();
            // The pump may be parked in a write to a pipe nobody is reading; cancelling is
            // what unblocks it, and the WhenAny guards the case where even that doesn't.
            client.Drop();
            await Task.WhenAny(pump, Task.Delay(DrainTimeout, CancellationToken.None)).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            LogClientDisconnected(_clients.Count);
        }
    }

    private async Task ReadLoopAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        // Completed handlers are pruned every iteration, so this stays small on a long-lived
        // connection while still letting the teardown path wait for genuine in-flight work.
        var inFlight = new List<Task>();

        while (!cancellationToken.IsCancellationRequested)
        {
            IpcRequest? request;
            try
            {
                request = await client.Connection.ReadAsync<IpcRequest>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // One unparseable line is not a broken connection: the reader is already
                // positioned at the next one. Skip it and keep serving.
                LogMalformedLine(ex);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                LogReadFailed(ex);
                break;
            }

            if (request is null)
            {
                break; // clean EOF — the peer closed its write half
            }

            inFlight.RemoveAll(static task => task.IsCompleted);
            inFlight.Add(HandleRequestAsync(client, request, cancellationToken));
        }

        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // HandleRequestAsync swallows everything; this only guards against a future edit
            // to it turning teardown into an unobserved-exception crash.
        }
    }

    private async Task HandleRequestAsync(ClientConnection client, IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _dispatcher.DispatchAsync(request, client, cancellationToken).ConfigureAwait(false);
            await client.Connection.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Connection or server is going away; nobody is waiting for this reply.
        }
        catch (Exception ex)
        {
            LogResponseWriteFailed(ex, request.Type, request.Id);
            client.Drop();
        }
    }

    private async Task PumpEventsAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in client.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await client.Connection.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogEventWriteFailed(ex);
            client.Drop();
        }
    }

    // ------------------------------------------------------------------- broadcasting

    /// <summary>
    /// Queues <paramref name="evt"/> on every live connection. Non-blocking: safe to call
    /// from an engine thread. A connection whose queue is full is dropped.
    /// </summary>
    public void Broadcast(IpcEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_clients.IsEmpty)
        {
            return;
        }

        foreach (var client in _clients.Keys)
        {
            Deliver(client, evt);
        }
    }

    /// <summary>
    /// Queues <paramref name="evt"/> on the connections that asked for the run-event stream
    /// and on no others.
    ///
    /// The filter is the point, not an optimisation: <c>RunEvents</c> is the only event in
    /// the protocol whose natural rate can outrun a connection's queue, and the penalty for
    /// a full queue is being dropped. A second panel — or a debug console — that never
    /// subscribed must not be exposed to that.
    /// </summary>
    public void BroadcastToRunSubscribers(IpcEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_clients.IsEmpty)
        {
            return;
        }

        foreach (var client in _clients.Keys)
        {
            if (client.WantsRunEvents)
            {
                Deliver(client, evt);
            }
        }
    }

    private void Deliver(ClientConnection client, IpcEvent evt)
    {
        if (client.TryEnqueue(evt))
        {
            return;
        }
        LogClientBacklogged(evt.Type, EventQueueCapacity);
        client.Drop();
    }

    // Payload construction is deferred so a daemon running with no panel attached doesn't
    // serialise a WindowDto on every tag a macro sets.
    private void Broadcast(string type, Func<JsonElement?>? payload = null)
    {
        if (_clients.IsEmpty)
        {
            return;
        }
        Broadcast(new IpcEvent(type, payload?.Invoke()));
    }

    private void OnWindowAppeared(ManagedWindowInfo window) =>
        Broadcast(IpcMessageTypes.WindowAppeared, () => IpcJson.Write(window.ToDto()));

    private void OnWindowTagsChanged(ManagedWindowInfo window) =>
        Broadcast(IpcMessageTypes.WindowTagsChanged, () => IpcJson.Write(window.ToDto()));

    // The window is already gone, so there is nothing left to describe but the handle.
    private void OnWindowClosed(ManagedWindowInfo window) =>
        Broadcast(IpcMessageTypes.WindowClosed, () => IpcJson.Write(new WindowClosedEvent(window.Hwnd.ToInt64())));

    // No payload by protocol: the library can be large and the client re-fetches with GetMacros.
    private void OnMacrosChanged(IReadOnlyList<MacroGraph> macros) =>
        Broadcast(IpcMessageTypes.MacrosChanged);

    // The run list is small and the UI needs it immediately, so this one does carry state.
    private void OnRunsChanged() =>
        Broadcast(IpcMessageTypes.RunningMacrosChanged, () => IpcJson.Write(_runs.Snapshot().ToDto()));

    // IpcMessageTypes.ActivateWindow has two producers, both outside this class and both
    // going through the public Broadcast(IpcEvent) overload via IIpcBroadcaster: the tray's
    // "Открыть панель" when the panel it launched is still alive, and the dispatcher's
    // RequestActivate handler (a second UI launch asking the first one to come forward).

    // ------------------------------------------------------------------------ teardown

    private async Task WaitForClientsAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)DrainTimeout.TotalMilliseconds;
        while (!_clients.IsEmpty && Environment.TickCount64 < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
        }
        if (!_clients.IsEmpty)
        {
            LogClientsDidNotDrain(_clients.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        UnsubscribeFromEngine();
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var client in _clients.Keys)
        {
            client.Drop();
        }
        _stopping.Dispose();
    }

    /// <summary>
    /// One connected client: the framed stream plus its own event queue, its own
    /// cancellation source and its own subscription state. Dropping a client cancels only
    /// that source, which is why one dead peer cannot take the accept loop or its siblings
    /// down with it.
    /// </summary>
    private sealed class ClientConnection : IAsyncDisposable, IIpcSession
    {
        private readonly Channel<IpcEvent> _events = Channel.CreateBounded<IpcEvent>(
            new BoundedChannelOptions(EventQueueCapacity)
            {
                // Wait mode with a TryWrite caller: a full queue makes TryWrite return
                // false instead of blocking the engine thread that raised the event, and
                // the caller turns that into "drop the connection".
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });

        private readonly RunEventPublisher _runEvents;
        private readonly Lock _subscriptionLock = new();
        private bool _wantsRunEvents;

        public ClientConnection(IpcConnection connection, RunEventPublisher runEvents)
        {
            Connection = connection;
            _runEvents = runEvents;
        }

        public IpcConnection Connection { get; }

        public CancellationTokenSource Cts { get; } = new();

        public ChannelReader<IpcEvent> Events => _events.Reader;

        /// <inheritdoc />
        public bool WantsRunEvents
        {
            get
            {
                lock (_subscriptionLock)
                {
                    return _wantsRunEvents;
                }
            }
        }

        /// <inheritdoc />
        public void SetRunEventSubscription(bool enabled)
        {
            // Locked and edge-triggered: the publisher gates the whole executor on a
            // subscriber COUNT, so a double subscribe (or a disconnect racing an explicit
            // unsubscribe) leaking a reference would leave the engine instrumented with
            // nobody watching.
            lock (_subscriptionLock)
            {
                if (_wantsRunEvents == enabled)
                {
                    return;
                }
                _wantsRunEvents = enabled;
            }

            if (enabled)
            {
                _runEvents.Acquire();
            }
            else
            {
                _runEvents.Release();
            }
        }

        public bool TryEnqueue(IpcEvent evt) => _events.Writer.TryWrite(evt);

        public void CompleteEvents() => _events.Writer.TryComplete();

        public void Drop()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down — dropping twice is normal (the pump and the reader
                // loop can both notice the same broken pipe).
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            Cts.Dispose();
        }
    }
}
