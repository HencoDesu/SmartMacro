using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;

namespace SmartMacro.ProcessMonitoring;

// Polls Process.GetProcessesByName at a configurable interval and raises ProcessAppeared /
// ProcessDisappeared events for every diff against the previous snapshot. Watches the
// UNION of all ProcessProfiles process names — one enumeration per profile name per tick.
// Designed to be cheap and tolerant of process crashes (a disappeared process is normal,
// not an error).
//
// The monitor is a passive observer — it doesn't know who listens to its events. The
// orchestrator subscribes during construction. Other consumers (UI, diagnostics) can
// subscribe too without the monitor caring.
public sealed partial class ProcessMonitor : IHostedService, IDisposable
{
    private readonly IReadOnlyList<string> _processNames;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<ProcessMonitor> _logger;

    // Pids for which we've already fired ProcessAppeared (i.e. their MainWindowHandle was
    // non-zero by the time we got to them).
    private readonly HashSet<int> _knownPids = [];

    // Pids we've seen with hwnd=0 and logged about. PW's launcher creates the process
    // several seconds before its main window is initialised; without this two-stage
    // tracking we'd fire ProcessAppeared with hwnd=0 (which crashes GameWindow's ctor)
    // and then never retry. We keep polling these pids each tick until hwnd != 0; the
    // "waiting" log fires once per pid so the operator sees the wait without log spam.
    private readonly HashSet<int> _pidsLoggedWaiting = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Raised when a new watched process is detected with a valid main-window handle.
    /// Late-bound: PW spawns its process before initialising the main window; the monitor
    /// keeps polling until <see cref="ProcessInfo.MainWindowHandle"/> is non-zero, then fires.
    /// </summary>
    public event Action<ProcessInfo>? ProcessAppeared;

    /// <summary>
    /// Raised when a previously-announced process is no longer present in the OS process
    /// list. Only fires for pids we previously emitted via <see cref="ProcessAppeared"/>.
    /// </summary>
    public event Action<int>? ProcessDisappeared;

    public ProcessMonitor(
        IOptions<AgentOptions> agentOptions,
        IOptions<ProcessProfileOptions> profileOptions,
        ILogger<ProcessMonitor> logger)
    {
        _processNames = profileOptions.Value.GetWatchedProcessNames();
        _pollInterval = TimeSpan.FromSeconds(agentOptions.Value.ProcessPollIntervalSeconds);
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("Monitor already started.");
        }

        if (_processNames.Count == 0)
        {
            // Legal but almost certainly a config mistake — no profiles means no windows
            // will ever be tracked. Keep the loop running anyway so hot-added consumers
            // see consistent (empty) behavior instead of a dead service.
            LogNoProfiles();
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        LogStarted(string.Join(", ", _processNames), _pollInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            if (_loop is not null)
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
            LogStopped(string.Join(", ", _processNames));
        }
    }

    private void Poll()
    {
        var current = SnapshotAll(_processNames);

        var currentPids = current
            .Select(x => x.Pid)
            .ToImmutableHashSet();

        // Disappearance — only fire for pids we previously announced as appeared. Pids
        // that died while still waiting for their window are silently dropped from the
        // "waiting" set; there's no need to fire Disappeared for something we never said
        // had appeared in the first place.
        var gone = _knownPids
            .Where(knownPid => !currentPids.Contains(knownPid))
            .ToList();

        if (gone.Count != 0)
        {
            foreach (var pid in gone)
            {
                _knownPids.Remove(pid);
                LogProcessDisappeared(pid);
                ProcessDisappeared?.Invoke(pid);
            }
        }
        _pidsLoggedWaiting.RemoveWhere(pid => !currentPids.Contains(pid));

        // New appearances — defer ProcessAppeared until MainWindowHandle is non-zero.
        // PW's elementclient_64 spawns the process several seconds before the main
        // window is created; firing too early crashes GameWindow.ctor and the pid is
        // never re-evaluated. Keeping the pid out of _knownPids until hwnd is ready
        // means the next poll tick re-checks it.
        foreach (var info in current)
        {
            if (_knownPids.Contains(info.Pid))
            {
                continue;
            }

            if (info.MainWindowHandle == IntPtr.Zero)
            {
                if (_pidsLoggedWaiting.Add(info.Pid))
                {
                    LogProcessWaitingForWindow(info.Pid, info.ProcessName);
                }
                continue;
            }

            _knownPids.Add(info.Pid);
            _pidsLoggedWaiting.Remove(info.Pid);
            LogProcessAppeared(info.Pid, info.ProcessName, info.MainWindowHandle.ToInt64());
            ProcessAppeared?.Invoke(info);
        }
    }

    private static List<ProcessInfo> SnapshotAll(IReadOnlyList<string> processNames)
    {
        var result = new List<ProcessInfo>();
        foreach (var name in processNames)
        {
            result.AddRange(SnapshotByName(name));
        }
        return result;
    }

    private static List<ProcessInfo> SnapshotByName(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            var result = new List<ProcessInfo>(procs.Length);
            foreach (var p in procs)
            {
                try
                {
                    result.Add(new ProcessInfo(p.Id, p.ProcessName, p.MainWindowHandle));
                }
                catch
                {
                    // Process may have died between enumeration and property access — skip it
                    // so a single dying process doesn't blow up the whole poll tick.
                }
            }
            return result;
        }
        finally
        {
            // Process holds an unmanaged handle; must be disposed deterministically rather
            // than waiting for GC.
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                LogPollFailed(ex);
            }

            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "ProcessMonitor started for [{ProcessNames}] (interval {Interval})")]
    partial void LogStarted(string processNames, TimeSpan interval);

    [LoggerMessage(LogLevel.Information, "ProcessMonitor stopped for [{ProcessNames}]")]
    partial void LogStopped(string processNames);

    [LoggerMessage(LogLevel.Warning, "No ProcessProfiles configured — ProcessMonitor has nothing to watch; add entries to the \"ProcessProfiles\" section in appsettings.json")]
    partial void LogNoProfiles();

    [LoggerMessage(LogLevel.Information, "Process appeared: pid={Pid} name='{ProcessName}' hwnd=0x{Hwnd:X}")]
    partial void LogProcessAppeared(int pid, string processName, long hwnd);

    [LoggerMessage(LogLevel.Information, "Process disappeared: pid={Pid}")]
    partial void LogProcessDisappeared(int pid);

    [LoggerMessage(LogLevel.Error, "Poll iteration failed; continuing loop")]
    partial void LogPollFailed(Exception ex);

    [LoggerMessage(LogLevel.Information, "Process seen but main window not ready yet: pid={Pid} name='{ProcessName}' — will keep polling until hwnd appears")]
    partial void LogProcessWaitingForWindow(int pid, string processName);

    #endregion
}
