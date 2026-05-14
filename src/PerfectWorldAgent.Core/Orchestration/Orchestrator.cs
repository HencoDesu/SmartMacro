using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Agents;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Window;
using Stateless;

namespace PerfectWorldAgent.Orchestration;

// State graph (Hold is initial):
//
//           ┌────────── Stop ──────────┐
//           │                          │
//   ┌── GoFollow ───┐              ┌── GoCombat ──┐
//   │               ▼              ▼              │
//   │           ┌────────┐    ┌─────────┐         │
//   │           │ FOLLOW │    │ COMBAT  │         │
//   │           └───┬────┘    └────┬────┘         │
//   │       GoHold/Stop      GoHold/Stop/         │
//   │               ▼            CombatFinished   │
//  ┌┴─────┐         │                ▼          ┌─┴─┐
//  │ HOLD │◀────────┴────────────────┴──────────┤   │
//  └──────┘                                     └───┘
//
// Inputs:
//   * Infra signals from HotkeyListener / ProcessMonitor — Hotkey fires FireAsync,
//     ProcessMonitor.Appeared spawns agents. ProcessDisappeared is unused — agents
//     detect window death via IGameWindow.IsAlive in their poll loop and self-terminate
//     by writing AgentStoppingMessage.
//   * CharacterAgent → orchestrator messages — status, stuck, identified, stopping —
//     written to the shared inbox channel and drained by a background dispatch loop.
//     Single uniform upstream path; no C# events on agents.
//
// Outputs:
//   * Downstream broadcasts — state-machine entry actions push ModeChangedMessage to
//     every live agent by iterating _agents.
//   * Lifecycle events for UI — AgentStarted on creation, AgentIdentified on promotion,
//     AgentStopped when the agent's RunLoopAsync exits.
//
// The orchestrator no longer keys agents by pid — pid is an OS-layer routing concern.
// ProcessMonitor dedups pids itself (one Appeared per pid). Agents live in a single
// lock-protected HashSet; lookups (broadcast, shutdown, stopping cleanup) are O(n) over
// ≤9 entries.
public sealed partial class Orchestrator : IHostedService, IDisposable
{
    private readonly StateMachine<AgentMode, OrchestratorTrigger> _machine;
    private readonly ILogger<Orchestrator> _logger;
    private readonly Channel<AgentMessage> _inbox;
    private readonly SemaphoreSlim _machineLock = new(1, 1);
    private readonly ProcessMonitor _processMonitor;
    private readonly HotkeyListener _hotkeyListener;
    private readonly ICharacterAgentFactory _agentFactory;

    private readonly Lock _agentsLock = new();
    private readonly HashSet<CharacterAgent> _agents = [];

    private CancellationTokenSource? _dispatchCts;
    private Task? _dispatchLoop;

    public event Action<CharacterAgent>? AgentStarted;
    public event Action<CharacterAgent>? AgentIdentified;
    public event Action<CharacterAgent>? AgentStopped;

    // Snapshot of currently-tracked agents. Used by late subscribers (e.g. UI VM that's
    // constructed after the orchestrator has already started spawning agents from the
    // first ProcessMonitor poll) to catch up on missed AgentStarted events.
    public IReadOnlyCollection<CharacterAgent> SnapshotAgents()
    {
        lock (_agentsLock)
        {
            return _agents.ToArray();
        }
    }

    public Orchestrator(
        ProcessMonitor processMonitor,
        HotkeyListener hotkeyListener,
        ICharacterAgentFactory agentFactory,
        ILogger<Orchestrator> logger)
    {
        _logger = logger;
        _agentFactory = agentFactory;
        _processMonitor = processMonitor;
        _processMonitor.ProcessAppeared += OnProcessAppeared;
        // No subscription to ProcessDisappeared — agents detect dead windows via
        // IGameWindow.IsAlive themselves and self-terminate via AgentStoppingMessage.

        _hotkeyListener = hotkeyListener;
        _hotkeyListener.HotkeyPressed += OnHotkeyPressed;

        _inbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        _machine = new StateMachine<AgentMode, OrchestratorTrigger>(AgentMode.Hold);

        _machine.OnTransitionCompletedAsync(t =>
        {
            LogModeTransition(t.Source, t.Destination, t.Trigger);
            return Task.CompletedTask;
        });

        _machine.OnUnhandledTriggerAsync((state, trigger) =>
        {
            LogUnhandledTrigger(trigger, state);
            return Task.CompletedTask;
        });

        _machine.Configure(AgentMode.Hold)
            .OnEntryAsync(_ => BroadcastAsync(new ModeChangedMessage(AgentMode.Hold)))
            .Permit(OrchestratorTrigger.GoFollow, AgentMode.Follow)
            .Permit(OrchestratorTrigger.GoCombat, AgentMode.Combat)
            .Ignore(OrchestratorTrigger.GoHold)
            .PermitReentry(OrchestratorTrigger.Stop)
            .Ignore(OrchestratorTrigger.CombatFinished);

        _machine.Configure(AgentMode.Follow)
            .OnEntryAsync(_ => BroadcastAsync(new ModeChangedMessage(AgentMode.Follow)))
            .Permit(OrchestratorTrigger.GoHold, AgentMode.Hold)
            .Permit(OrchestratorTrigger.GoCombat, AgentMode.Combat)
            .Permit(OrchestratorTrigger.Stop, AgentMode.Hold)
            .Ignore(OrchestratorTrigger.GoFollow)
            .Ignore(OrchestratorTrigger.CombatFinished);

        _machine.Configure(AgentMode.Combat)
            .OnEntryAsync(_ => BroadcastAsync(new ModeChangedMessage(AgentMode.Combat)))
            .Permit(OrchestratorTrigger.GoHold, AgentMode.Hold)
            .Permit(OrchestratorTrigger.CombatFinished, AgentMode.Hold)
            .Permit(OrchestratorTrigger.Stop, AgentMode.Hold)
            .Ignore(OrchestratorTrigger.GoFollow)
            .Ignore(OrchestratorTrigger.GoCombat);
    }

    private async Task FireAsync(OrchestratorTrigger trigger, CancellationToken cancellationToken = default)
    {
        LogFiringTrigger(trigger, _machine.State);

        await _machineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _machine.FireAsync(trigger).ConfigureAwait(false);
        }
        finally
        {
            _machineLock.Release();
        }
    }

    private void OnProcessAppeared(ProcessInfo info)
    {
        LogProcessAppearedNotification(info.Pid, info.ProcessName, info.MainWindowHandle.ToInt64());
        _ = HandleProcessAppearedAsync(info);
    }

    private async Task HandleProcessAppearedAsync(ProcessInfo info)
    {
        try
        {
            var agent = await _agentFactory.CreateAsync(info, _inbox.Writer).ConfigureAwait(false);
            lock (_agentsLock)
            {
                _agents.Add(agent);
            }
            agent.Start();
            AgentStarted?.Invoke(agent);
        }
        catch (Exception ex)
        {
            LogAgentCreationFailed(ex, info.Pid);
        }
    }

    private void OnHotkeyPressed(OrchestratorTrigger trigger)
    {
        switch (trigger)
        {
            case OrchestratorTrigger.BroadcastImmunity:
                LogBroadcastImmunity();
                _ = BroadcastAsync(new UseImmunityMessage());
                return;

            case OrchestratorTrigger.BroadcastClick:
                BroadcastCursorClick(doubleClick: false);
                return;

            case OrchestratorTrigger.BroadcastDoubleClick:
                BroadcastCursorClick(doubleClick: true);
                return;
        }

        try
        {
            FireAsync(trigger).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogHotkeyFireFailed(ex, trigger);
        }
    }

    // Reads the current cursor position, translates it into the foreground window's
    // client coordinates, broadcasts ClickAtMessage to every agent. Each agent applies
    // those same coords on its own window (assumption: all PW clients are sized
    // identically — standard multi-client setup).
    //
    // Two guards prevent firing when the user isn't actually pointing at a game client:
    //   1. Foreground window must be one of our tracked agents — otherwise the user is
    //      in another app (browser, IDE, panel) and the hotkey was likely accidental
    //      (e.g. they remapped Tab on the Stream Deck and forgot).
    //   2. Cursor must be inside the foreground's client area — otherwise they're on
    //      the title bar / over a different monitor / over a partially-occluded window;
    //      the translated coords would be negative or out-of-bounds and the broadcast
    //      would click garbage positions in every agent's window.
    private void BroadcastCursorClick(bool doubleClick)
    {
        var kind = doubleClick ? "DoubleClick" : "Click";
        var (screenX, screenY) = Win32NativeWindowSystem.GetCursorPos();
        var foreground = Win32NativeWindowSystem.GetForeground();
        if (foreground.Handle == IntPtr.Zero)
        {
            LogBroadcastClickNoForeground(screenX, screenY, doubleClick);
            return;
        }

        if (!IsTrackedAgentHandle(foreground.Handle))
        {
            LogBroadcastClickForegroundNotAgent(kind, foreground.Handle.ToInt64(), screenX, screenY);
            return;
        }

        var (clientX, clientY) = foreground.ScreenToClient(screenX, screenY);
        var (width, height) = foreground.GetClientSize();
        if (clientX < 0 || clientY < 0 || clientX >= width || clientY >= height)
        {
            LogBroadcastClickCursorOutsideClient(kind, screenX, screenY, clientX, clientY, width, height);
            return;
        }

        LogBroadcastClick(kind, clientX, clientY);
        _ = BroadcastAsync(new ClickAtMessage(clientX, clientY, doubleClick));
    }

    private bool IsTrackedAgentHandle(IntPtr hwnd)
    {
        lock (_agentsLock)
        {
            foreach (var agent in _agents)
            {
                if (agent.Handle == hwnd)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_dispatchCts is not null)
        {
            throw new InvalidOperationException("Dispatch loop already running.");
        }

        _dispatchCts = new CancellationTokenSource();
        _dispatchLoop = Task.Run(() => DispatchLoopAsync(_dispatchCts.Token), _dispatchCts.Token);
        LogDispatchLoopStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopAllAgentsAsync(cancellationToken).ConfigureAwait(false);

        if (_dispatchCts is null)
        {
            return;
        }

        await _dispatchCts.CancelAsync();
        try
        {
            if (_dispatchLoop is not null)
            {
                await _dispatchLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _dispatchCts.Dispose();
            _dispatchCts = null;
            _dispatchLoop = null;
            LogDispatchLoopStopped();
        }
    }

    // Asks every agent to stop and waits for their RunningTask. Best-effort within the
    // host's shutdown deadline; survivors get torn down by process exit.
    private async Task StopAllAgentsAsync(CancellationToken cancellationToken)
    {
        CharacterAgent[] snapshot;
        lock (_agentsLock)
        {
            snapshot = _agents.ToArray();
        }
        if (snapshot.Length == 0)
        {
            return;
        }

        foreach (var agent in snapshot)
        {
            agent.Stop();
        }

        try
        {
            await Task.WhenAll(snapshot.Select(a => a.RunningTask ?? Task.CompletedTask))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _processMonitor.ProcessAppeared -= OnProcessAppeared;
        _hotkeyListener.HotkeyPressed -= OnHotkeyPressed;
        _dispatchCts?.Cancel();
        _dispatchCts?.Dispose();
        _dispatchCts = null;
        _machineLock.Dispose();
    }

    private async Task ProcessIncomingAsync()
    {
        while (_inbox.Reader.TryRead(out var message))
        {
            try
            {
                await HandleMessageAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle {MessageType} from agent", message.GetType().Name);
            }
        }
    }

    private Task HandleMessageAsync(AgentMessage message)
    {
        switch (message)
        {
            case StatusUpdateMessage status:
                LogStatusPosition(status.CharacterName, status.Position);
                // TODO: maintain last-known position per agent for cross-checks against master movement
                break;

            case StuckDetectedMessage stuck:
                LogStuckReported(stuck.CharacterName, stuck.LastPosition);
                // TODO: invoke recovery sequence (re-activate follow → jump → notify player)
                break;

            case AgentIdentifiedMessage id:
                LogAgentIdentified(id.OldName, id.Agent.Name);
                AgentIdentified?.Invoke(id.Agent);
                break;

            case AgentStoppingMessage stopping:
                lock (_agentsLock)
                {
                    _agents.Remove(stopping.Agent);
                }
                AgentStopped?.Invoke(stopping.Agent);
                break;

            default:
                LogUnhandledUpstreamMessageType(message.GetType().Name);
                break;
        }
        return Task.CompletedTask;
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessIncomingAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BroadcastAsync(AgentMessage message)
    {
        CharacterAgent[] snapshot;
        lock (_agentsLock)
        {
            snapshot = _agents.ToArray();
        }
        if (snapshot.Length == 0)
        {
            return;
        }

        foreach (var agent in snapshot)
        {
            try
            {
                await agent.Inbox.WriteAsync(message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Agent stopped between snapshot and write — ignore.
            }
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Mode transition: {Source} -> {Destination} (trigger {Trigger})")]
    partial void LogModeTransition(AgentMode source, AgentMode destination, OrchestratorTrigger trigger);

    [LoggerMessage(LogLevel.Warning, "Unhandled trigger {Trigger} in state {State}")]
    partial void LogUnhandledTrigger(OrchestratorTrigger trigger, AgentMode state);

    [LoggerMessage(LogLevel.Debug, "Firing trigger {Trigger} from state {State}")]
    partial void LogFiringTrigger(OrchestratorTrigger trigger, AgentMode state);

    [LoggerMessage(LogLevel.Information, "Orchestrator dispatch loop started")]
    partial void LogDispatchLoopStarted();

    [LoggerMessage(LogLevel.Debug, "Status from '{Name}': position {Position}")]
    partial void LogStatusPosition(string name, Coordinates position);

    [LoggerMessage(LogLevel.Warning, "Stuck reported by '{Name}' at {Position}")]
    partial void LogStuckReported(string name, Coordinates position);

    [LoggerMessage(LogLevel.Debug, "Unhandled upstream message {Type}")]
    partial void LogUnhandledUpstreamMessageType(string type);

    [LoggerMessage(LogLevel.Information, "Process appeared (from monitor): pid={Pid} name='{ProcessName}' hwnd=0x{Hwnd:X}")]
    partial void LogProcessAppearedNotification(int pid, string processName, long hwnd);

    [LoggerMessage(LogLevel.Error, "FireAsync({Trigger}) from hotkey event failed")]
    partial void LogHotkeyFireFailed(Exception ex, OrchestratorTrigger trigger);

    [LoggerMessage(LogLevel.Information, "Orchestrator dispatch loop stopped")]
    partial void LogDispatchLoopStopped();

    [LoggerMessage(LogLevel.Error, "Failed to create agent for pid={Pid}")]
    partial void LogAgentCreationFailed(Exception ex, int pid);

    [LoggerMessage(LogLevel.Information, "Agent promoted: '{OldName}' -> '{NewName}'")]
    partial void LogAgentIdentified(string oldName, string newName);

    [LoggerMessage(LogLevel.Information, "BroadcastImmunity hotkey pressed — broadcasting UseImmunityMessage to all agents")]
    partial void LogBroadcastImmunity();

    [LoggerMessage(LogLevel.Information, "Broadcast{Kind} hotkey pressed — client=({X},{Y}) on all agents")]
    partial void LogBroadcastClick(string kind, int x, int y);

    [LoggerMessage(LogLevel.Warning, "Broadcast click hotkey pressed but no foreground window — screen=({X},{Y}) doubleClick={DoubleClick}; skipping")]
    partial void LogBroadcastClickNoForeground(int x, int y, bool doubleClick);

    [LoggerMessage(LogLevel.Information, "Broadcast{Kind} suppressed — foreground hwnd=0x{Hwnd:X} is not a tracked agent (cursor screen=({X},{Y}))")]
    partial void LogBroadcastClickForegroundNotAgent(string kind, long hwnd, int x, int y);

    [LoggerMessage(LogLevel.Information, "Broadcast{Kind} suppressed — cursor screen=({Sx},{Sy}) maps to client=({Cx},{Cy}) which is outside [0,{W})x[0,{H})")]
    partial void LogBroadcastClickCursorOutsideClient(string kind, int sx, int sy, int cx, int cy, int w, int h);

    #endregion
}
