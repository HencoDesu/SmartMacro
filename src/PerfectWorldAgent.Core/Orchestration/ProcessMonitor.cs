using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Config;

namespace PerfectWorldAgent.Orchestration;

// Polls Process.GetProcessesByName at a configurable interval and raises ProcessAppeared /
// ProcessDisappeared events for every diff against the previous snapshot. Designed to be
// cheap (one enumeration per tick) and tolerant of process crashes (a disappeared process
// is normal, not an error).
//
// The monitor is a passive observer — it doesn't know who listens to its events. The
// orchestrator subscribes during construction. Other consumers (UI, diagnostics) can
// subscribe too without the monitor caring.
public sealed partial class ProcessMonitor : IHostedService, IDisposable
{
    private readonly string _processName;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<ProcessMonitor> _logger;
    private readonly HashSet<int> _knownPids = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<ProcessInfo>? ProcessAppeared;
    public event Action<int>? ProcessDisappeared;

    // Test-only hooks — events can't be invoked from outside the declaring class even with
    // InternalsVisibleTo, so we expose internal raise helpers for unit tests.
    internal void RaiseProcessAppeared(ProcessInfo info) => ProcessAppeared?.Invoke(info);
    internal void RaiseProcessDisappeared(int pid) => ProcessDisappeared?.Invoke(pid);

    public ProcessMonitor(
        IOptions<AgentOptions> options,
        ILogger<ProcessMonitor> logger)
    {
        var values = options.Value;
        if (string.IsNullOrWhiteSpace(values.GameProcessName))
        {
            throw new ArgumentException("AgentOptions.GameProcessName is required.", nameof(options));
        }

        _processName = values.GameProcessName;
        _pollInterval = TimeSpan.FromSeconds(values.ProcessPollIntervalSeconds);
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("Monitor already started.");
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        LogStarted(_processName, _pollInterval);
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
            LogStopped(_processName);
        }
    }

    private void Poll()
    {
        var current = SnapshotByName(_processName);

        var currentPids = current
            .Select(x => x.Pid)
            .ToImmutableHashSet();

        var gone = _knownPids
            .Where(knownPid => !currentPids.Contains(knownPid))
            .ToList();

        if (gone.Count != 0)
        {
            foreach (var pid in gone)
            {
                _knownPids.Remove(pid);
                LogProcessDisappeared(pid, _processName);
                ProcessDisappeared?.Invoke(pid);
            }
        }

        var newProcesses = current.Where(info => _knownPids.Add(info.Pid));
        foreach (var info in newProcesses)
        {
            LogProcessAppeared(info.Pid, info.ProcessName, info.MainWindowHandle.ToInt64());
            ProcessAppeared?.Invoke(info);
        }
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
                LogPollFailed(ex, _processName);
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

    [LoggerMessage(LogLevel.Information, "ProcessMonitor started for '{ProcessName}' (interval {Interval})")]
    partial void LogStarted(string processName, TimeSpan interval);

    [LoggerMessage(LogLevel.Information, "ProcessMonitor stopped for '{ProcessName}'")]
    partial void LogStopped(string processName);

    [LoggerMessage(LogLevel.Information, "Process appeared: pid={Pid} name='{ProcessName}' hwnd=0x{Hwnd:X}")]
    partial void LogProcessAppeared(int pid, string processName, long hwnd);

    [LoggerMessage(LogLevel.Information, "Process disappeared: pid={Pid} name='{ProcessName}'")]
    partial void LogProcessDisappeared(int pid, string processName);

    [LoggerMessage(LogLevel.Error, "Poll iteration failed for '{ProcessName}'; continuing loop")]
    partial void LogPollFailed(Exception ex, string processName);

    #endregion
}
