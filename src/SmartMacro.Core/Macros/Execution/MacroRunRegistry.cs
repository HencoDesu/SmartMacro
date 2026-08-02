using Microsoft.Extensions.Logging;

namespace SmartMacro.Macros.Execution;

/// <summary>Immutable view of one tracked run, as returned by <see cref="MacroRunRegistry.Snapshot"/>.</summary>
/// <param name="RunId">Unique id of this run.</param>
/// <param name="MacroName">Name of the macro being run.</param>
/// <param name="StartedUtc">When the run began.</param>
/// <param name="CurrentNodeId">Id of the node the walker last entered; <c>null</c> before the first node.</param>
public sealed record MacroRunSnapshot(Guid RunId, string MacroName, DateTime StartedUtc, string? CurrentNodeId);

/// <summary>
/// Live handle for one run, returned by <see cref="MacroRunRegistry.TryBegin"/>. The
/// runner executes with <see cref="Token"/> and reports progress through
/// <see cref="CurrentNodeId"/> (wire it to <see cref="MacroRunContext.OnNodeEntered"/>);
/// when the run ends — however it ends — call <see cref="MacroRunRegistry.Complete"/>.
/// </summary>
public sealed class MacroRunHandle
{
    private string? _currentNodeId;

    internal MacroRunHandle(Guid runId, string macroName, DateTime startedUtc, CancellationToken token)
    {
        RunId = runId;
        MacroName = macroName;
        StartedUtc = startedUtc;
        Token = token;
    }

    /// <summary>Unique id of this run.</summary>
    public Guid RunId { get; }

    /// <summary>Name of the macro being run.</summary>
    public string MacroName { get; }

    /// <summary>When the run began.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>Cancelled by <see cref="MacroRunRegistry.StopAsync"/> / <see cref="MacroRunRegistry.StopAllAsync"/>.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Id of the node the walker last entered. Volatile — updated live by the runner,
    /// read by UI snapshots (future visual debugging).
    /// </summary>
    public string? CurrentNodeId
    {
        get => Volatile.Read(ref _currentNodeId);
        set => Volatile.Write(ref _currentNodeId, value);
    }
}

/// <summary>
/// Tracks running macros: single-flight per macro name (re-triggering a running macro is
/// a logged no-op), Stop from the UI, cancel-all on shutdown, and a snapshot for display.
/// The executor knows nothing about this class — callers bracket
/// <see cref="MacroExecutor.RunAsync"/> with <see cref="TryBegin"/> / <see cref="Complete"/>.
/// </summary>
public sealed partial class MacroRunRegistry : IDisposable
{
    private sealed class ActiveRun
    {
        public required MacroRunHandle Handle { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, ActiveRun> _runs = [];
    private readonly ILogger<MacroRunRegistry> _logger;
    private bool _disposed;

    public MacroRunRegistry(ILogger<MacroRunRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised (outside the lock) after a run is added or removed.</summary>
    public event Action? RunsChanged;

    /// <summary>
    /// Registers a new run of <paramref name="macroName"/>. Single-flight per name:
    /// returns <c>null</c> (with a log entry) when a run of that macro is already tracked.
    /// </summary>
    public MacroRunHandle? TryBegin(string macroName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);

        MacroRunHandle handle;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.Values.Any(run => string.Equals(run.Handle.MacroName, macroName, StringComparison.Ordinal)))
            {
                LogAlreadyRunning(macroName);
                return null;
            }

            var cts = new CancellationTokenSource();
            handle = new MacroRunHandle(Guid.NewGuid(), macroName, DateTime.UtcNow, cts.Token);
            _runs.Add(handle.RunId, new ActiveRun { Handle = handle, Cts = cts });
        }

        LogRunStarted(macroName, handle.RunId);
        RunsChanged?.Invoke();
        return handle;
    }

    /// <summary>
    /// Removes a finished run and unblocks anyone awaiting <see cref="StopAsync"/> on it.
    /// The runner must call this exactly once per <see cref="TryBegin"/>, in a
    /// <c>finally</c>. Unknown ids return <c>false</c> (no event).
    /// </summary>
    public bool Complete(Guid runId)
    {
        ActiveRun? run;
        lock (_lock)
        {
            if (!_runs.Remove(runId, out run))
            {
                return false;
            }
        }

        run.Completed.TrySetResult();
        run.Cts.Dispose();
        LogRunCompleted(run.Handle.MacroName, runId);
        RunsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Cancels the run and returns a task that completes when the runner acknowledges via
    /// <see cref="Complete"/>. Unknown (already finished) ids complete immediately.
    /// </summary>
    public Task StopAsync(Guid runId)
    {
        ActiveRun? run;
        lock (_lock)
        {
            if (!_runs.TryGetValue(runId, out run))
            {
                return Task.CompletedTask;
            }
        }

        LogStopRequested(run.Handle.MacroName, runId);
        Cancel(run);
        return run.Completed.Task;
    }

    /// <summary>Cancels every tracked run and waits for all of them to <see cref="Complete"/> (shutdown path).</summary>
    public Task StopAllAsync()
    {
        List<ActiveRun> runs;
        lock (_lock)
        {
            runs = [.. _runs.Values];
        }

        foreach (var run in runs)
        {
            Cancel(run);
        }
        return Task.WhenAll(runs.Select(run => run.Completed.Task));
    }

    /// <summary>Atomic snapshot of all tracked runs, isolated from later changes.</summary>
    public IReadOnlyList<MacroRunSnapshot> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<MacroRunSnapshot>(_runs.Count);
            foreach (var run in _runs.Values)
            {
                result.Add(new MacroRunSnapshot(
                    run.Handle.RunId,
                    run.Handle.MacroName,
                    run.Handle.StartedUtc,
                    run.Handle.CurrentNodeId));
            }
            return result;
        }
    }

    /// <summary>Cancels every tracked run without waiting. Prefer <see cref="StopAllAsync"/> on orderly shutdown.</summary>
    public void Dispose()
    {
        List<ActiveRun> runs;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            runs = [.. _runs.Values];
        }

        foreach (var run in runs)
        {
            Cancel(run);
        }
    }

    private static void Cancel(ActiveRun run)
    {
        try
        {
            run.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Complete() raced us and already disposed the CTS — the run is finished, done.
        }
    }

    [LoggerMessage(LogLevel.Information, "Macro '{MacroName}' is already running — trigger ignored (single-flight)")]
    partial void LogAlreadyRunning(string macroName);

    [LoggerMessage(LogLevel.Information, "Macro run started: '{MacroName}' ({RunId})")]
    partial void LogRunStarted(string macroName, Guid runId);

    [LoggerMessage(LogLevel.Information, "Macro run finished: '{MacroName}' ({RunId})")]
    partial void LogRunCompleted(string macroName, Guid runId);

    [LoggerMessage(LogLevel.Information, "Stop requested for macro run '{MacroName}' ({RunId})")]
    partial void LogStopRequested(string macroName, Guid runId);
}
