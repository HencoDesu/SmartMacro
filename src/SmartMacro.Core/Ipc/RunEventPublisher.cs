using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Execution;

namespace SmartMacro.Ipc;

/// <summary>
/// Turns <see cref="MacroExecutor"/>'s per-node progress into the <c>RunEvents</c> push, and
/// is the entire answer to "what stops the panel from being flooded off the pipe".
///
/// <b>The hazard.</b> One hotkey can start a walk that forks per window; each walk crosses a
/// dozen nodes; each node is two events. Several hundred events in a few hundred
/// milliseconds, against <see cref="IpcServer"/>'s per-connection queue of 256 which DROPS a
/// client that cannot keep up. Left unmediated, the busiest moment of a run is exactly when
/// the panel would vanish.
///
/// <b>Three deliberate mitigations, in order of how much they buy:</b>
///
///   1. <b>Opt-in.</b> Nothing is produced unless a connection asked
///      (<c>SubscribeRunEvents</c>). <see cref="IsEnabled"/> is a single volatile read the
///      walker does per node; when it is false there is no timing, no detail string, no DTO
///      and no JSON. The daemon is resident and the panel is not, so this is the common case
///      and it has to cost nothing.
///   2. <b>Coalescing.</b> Events go into one bounded queue and leave as batches, at most one
///      envelope per <see cref="FlushIntervalMs"/>. A 400-event burst becomes a handful of
///      envelopes — three orders of magnitude below what the connection queue would notice.
///   3. <b>Bounded, lossy, and honest about it.</b> If the queue fills anyway the engine
///      thread's <c>TryWrite</c> fails, the event is counted and dropped, and the count rides
///      out on the next batch so the panel can say the log has a hole. What must never happen
///      is the engine waiting: a macro run is driving a live game between two Win32 messages.
///
/// <b>Live-walk roster.</b> <see cref="WalkStarted"/>/<see cref="WalkFinished"/> maintain
/// <see cref="LiveWalks"/> whether or not anybody is subscribed — two dictionary operations
/// per macro run, not per node. Without it a panel that connects mid-run could not even
/// learn that a run exists, and <c>SubscribeRunEvents</c> would have nothing honest to
/// answer with.
/// </summary>
public sealed partial class RunEventPublisher : IMacroRunObserver, IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Dwell before draining the queue. Sets both the coalescing window and the worst-case
    /// lag of the canvas highlight — 50 ms is invisible to a human watching a node light up
    /// and turns the worst realistic burst into single-digit envelopes.
    /// </summary>
    private const int FlushIntervalMs = 50;

    /// <summary>
    /// Events buffered before we start dropping. Deep enough for the whole of a ten-window
    /// fan-out (~500 events) plus slack, shallow enough that a wedged pump cannot grow the
    /// daemon's heap without bound.
    /// </summary>
    private const int QueueCapacity = 4096;

    /// <summary>Events per envelope. A cap on JSON size per line, not on throughput — the pump loops.</summary>
    private const int MaxBatchSize = 400;

    private readonly Channel<RunEventDto> _queue = Channel.CreateBounded<RunEventDto>(
        new BoundedChannelOptions(QueueCapacity)
        {
            // Wait + TryWrite: a full queue makes TryWrite return false rather than block the
            // engine thread, and we turn that into a counted drop. DropOldest would be
            // silent, which is the one behaviour this class must not have.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly ConcurrentDictionary<Guid, RunWalkDto> _live = new();
    private readonly ILogger<RunEventPublisher> _logger;

    private IIpcBroadcaster? _broadcaster;
    private CancellationTokenSource? _stopping;
    private Task? _pump;
    private int _subscribers;
    private int _dropped;
    private int _disposed;

    /// <summary>
    /// Completed by an urgent enqueue to cut a coalescing dwell short.
    ///
    /// A signal rather than a flag, because the flag version only worked when the urgent
    /// event was the one that WOKE the pump. In practice it never is: a breakpoint hit is
    /// preceded by <c>WalkStarted</c> and <c>NodeEntered</c> in the same millisecond, so a
    /// flag checked once at the top of the loop had already been read as "not urgent" and the
    /// pause still paid the full 50 ms. Measured 63 ms before, single digits after.
    /// </summary>
    private volatile TaskCompletionSource _urgent = NewUrgentSignal();

    public RunEventPublisher(ILogger<RunEventPublisher> logger) => _logger = logger;

    /// <inheritdoc />
    public bool IsEnabled => Volatile.Read(ref _subscribers) > 0;

    /// <summary>How many connections are currently subscribed. Diagnostics and tests.</summary>
    public int SubscriberCount => Volatile.Read(ref _subscribers);

    /// <summary>
    /// Supplies the fan-out. Called once by <see cref="IpcServer"/>'s constructor for the
    /// same reason <c>AttachBroadcaster</c> exists on the dispatcher: server → publisher →
    /// server is a genuine cycle and this is the end that already holds the other object.
    /// </summary>
    public void AttachBroadcaster(IIpcBroadcaster broadcaster) => _broadcaster = broadcaster;

    // ------------------------------------------------------------------ subscriptions

    /// <summary>
    /// One more connection wants the stream. Paired with <see cref="Release"/> by
    /// <see cref="IpcServer"/>, including on the disconnect path — a client that dies
    /// without unsubscribing must not leave the engine instrumented forever.
    /// </summary>
    public void Acquire()
    {
        var count = Interlocked.Increment(ref _subscribers);
        if (count == 1)
        {
            LogTracingOn();
        }
    }

    /// <summary>One fewer subscriber. The last one out drains the queue.</summary>
    public void Release()
    {
        var count = Interlocked.Decrement(ref _subscribers);
        if (count > 0)
        {
            return;
        }

        // Whatever is still queued is addressed to nobody, and keeping it would mean the
        // NEXT subscriber's first batch is a burst of history from a run it never saw.
        while (_queue.Reader.TryRead(out _))
        {
        }
        Interlocked.Exchange(ref _dropped, 0);
        LogTracingOff();
    }

    /// <summary>
    /// Walks in flight right now, in start order — the answer to <c>SubscribeRunEvents</c>.
    /// Every entry has <c>FromStart = false</c> by construction: it began before the caller
    /// was listening, so its node log will be missing an unknowable number of leading rows.
    /// </summary>
    public IReadOnlyList<RunWalkDto> LiveWalks() =>
        [.. _live.Values.OrderBy(walk => walk.StartedUtc).Select(walk => walk with { FromStart = false })];

    // -------------------------------------------------------------- IMacroRunObserver

    public void WalkStarted(MacroWalkStart walk)
    {
        var dto = new RunWalkDto(
            walk.WalkId,
            walk.RunId,
            walk.MacroName,
            walk.ContextWindow?.ToInt64() ?? 0,
            walk.Depth,
            DateTimeOffset.UtcNow,
            FromStart: true);

        // Recorded unconditionally — see the class comment. Cheap and bounded by the number
        // of concurrent walks, which is the number of game windows.
        _live[walk.WalkId] = dto;

        if (IsEnabled)
        {
            Enqueue(new RunEventDto(walk.WalkId, RunEventKind.WalkStarted, ElapsedMs: 0, Walk: dto));
        }
    }

    public void NodeEntered(Guid walkId, int elapsedMs, string nodeId) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.NodeEntered, elapsedMs, nodeId));

    public void NodeExited(Guid walkId, int elapsedMs, string nodeId, string outcome, string? detail, int durationMs) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.NodeExited, elapsedMs, nodeId, outcome, detail, durationMs));

    public void WalkFinished(Guid walkId, int elapsedMs, string outcome, string? detail)
    {
        _live.TryRemove(walkId, out _);
        if (IsEnabled)
        {
            Enqueue(new RunEventDto(walkId, RunEventKind.WalkFinished, elapsedMs, Outcome: outcome, Detail: detail));
        }
    }

    public void VariableSet(Guid walkId, int elapsedMs, string name, string value, string? nodeId) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.VariableSet, elapsedMs, nodeId, Detail: value, Variable: name));

    /// <summary>
    /// A walk parked. <b>Flushed immediately</b>, bypassing the coalescing window: this is
    /// the acknowledgement of a button press, and 50 ms of dwell on top of a pipe round trip
    /// is the difference between a step that feels instant and one that feels stuck. Safe to
    /// exempt because it is a handful of events per session — the burst this class exists to
    /// tame is node traffic, which is untouched.
    /// </summary>
    public void WalkPaused(Guid walkId, int elapsedMs, string nodeId, DebugPauseReason reason) =>
        Enqueue(
            new RunEventDto(
                walkId,
                reason == DebugPauseReason.Breakpoint ? RunEventKind.BreakpointHit : RunEventKind.Paused,
                elapsedMs,
                nodeId,
                Detail: Describe(reason)),
            urgent: true);

    /// <inheritdoc cref="WalkPaused" />
    public void WalkResumed(Guid walkId, int elapsedMs, string nodeId) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.Resumed, elapsedMs, nodeId), urgent: true);

    // Russian, like every other Detail: the panel renders it verbatim in the log strip and
    // the toolbar, and the daemon is the only side that knows which of the four it was.
    private static string Describe(DebugPauseReason reason) => reason switch
    {
        DebugPauseReason.Breakpoint => "брейкпоинт",
        DebugPauseReason.Step => "шаг",
        DebugPauseReason.Cursor => "до курсора",
        _ => "пауза",
    };

    // -------------------------------------------------------------------- the pump

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null)
        {
            return;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_pump is { } pump)
        {
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            _pump = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping?.Dispose();
        _stopping = null;
    }

    /// <summary>
    /// Drains the queue into batches. The <see cref="FlushIntervalMs"/> delay after the
    /// FIRST event is the coalescing window: it is what lets a burst arriving over the next
    /// few milliseconds ride out on one envelope instead of four hundred.
    /// </summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var batch = new List<RunEventDto>(MaxBatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await DwellAsync(cancellationToken).ConfigureAwait(false);

                // Re-armed BEFORE the drain, so an urgent write that raced us either lands in
                // THIS batch (it was queued before the drain) or fires the new signal and gets
                // its own immediate flush. Never both, never neither.
                _urgent = NewUrgentSignal();

                batch.Clear();
                while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var evt))
                {
                    batch.Add(evt);
                }

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (batch.Count == 0 && dropped == 0)
                {
                    continue;
                }

                Publish(batch, dropped);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A serialisation bug here must not take the host down: the engine keeps running
            // and the panel simply stops seeing a log.
            LogPumpFailed(ex);
        }
    }

    /// <summary>
    /// The coalescing window — cut short the moment a debugger event is queued.
    /// </summary>
    private async Task DwellAsync(CancellationToken cancellationToken)
    {
        var urgent = _urgent.Task;
        if (urgent.IsCompleted)
        {
            return;
        }
        // The delay gets its own token so the loser of the race is cancelled rather than left
        // pending on a timer for every flush cycle of the daemon's life.
        using var dwell = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(urgent, Task.Delay(FlushIntervalMs, dwell.Token)).ConfigureAwait(false);
        await dwell.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource NewUrgentSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Publish(List<RunEventDto> batch, int dropped)
    {
        if (_broadcaster is not { } broadcaster)
        {
            return;
        }
        if (dropped > 0)
        {
            LogDropped(dropped);
        }
        broadcaster.BroadcastToRunSubscribers(new IpcEvent(
            IpcMessageTypes.RunEvents,
            IpcJson.Write(new RunEventBatch([.. batch], dropped))));
    }

    private void Enqueue(RunEventDto evt, bool urgent = false)
    {
        if (_queue.Writer.TryWrite(evt))
        {
            if (urgent)
            {
                // AFTER the write, so a pump woken by the signal is guaranteed to find the
                // item. (Before the write would be the safe order for a flag read once at the
                // top of the loop; for a signal that interrupts the dwell it is the wrong way
                // round.)
                _urgent.TrySetResult();
            }
            return;
        }
        // Never block, never grow: the caller is an engine thread between two game inputs.
        Interlocked.Increment(ref _dropped);
    }

    [LoggerMessage(LogLevel.Debug, "Run-event tracing on — a client subscribed")]
    partial void LogTracingOn();

    [LoggerMessage(LogLevel.Debug, "Run-event tracing off — no subscribers left")]
    partial void LogTracingOff();

    [LoggerMessage(LogLevel.Warning, "Run-event queue overflowed: {Dropped} events dropped")]
    partial void LogDropped(int dropped);

    [LoggerMessage(LogLevel.Error, "Run-event pump failed; the stream is dead until the daemon restarts")]
    partial void LogPumpFailed(Exception exception);
}
