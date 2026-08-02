using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Agents;
using PerfectWorldAgent.Hotkeys;
using PerfectWorldAgent.Input;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.ProcessMonitoring;

namespace PerfectWorldAgent.Orchestration;

// Pure dispatcher in v1 — no mode state machine, no automation. Routes:
//
// Inputs:
//   * ProcessMonitor.ProcessAppeared → spawn an agent (CharacterAgent owns the rest).
//   * HotkeyListener.HotkeyPressed → broadcast the corresponding AgentMessage to every
//     live agent's inbox.
//   * Agents → orchestrator messages (AgentIdentifiedMessage, AgentStoppingMessage)
//     drained from a shared upstream inbox.
//
// Outputs:
//   * Lifecycle events for UI — AgentStarted on creation, AgentIdentified on promotion,
//     AgentStopped when the agent's RunLoopAsync exits.
//
// Agents live in a single lock-protected HashSet; lookups (broadcast, shutdown,
// foreground-is-agent check) are O(n) over ≤9 entries.
public sealed partial class Orchestrator : IHostedService, IDisposable
{
    private readonly ILogger<Orchestrator> _logger;
    private readonly Channel<AgentMessage> _inbox;
    private readonly ProcessMonitor _processMonitor;
    private readonly HotkeyListener _hotkeyListener;
    private readonly ICharacterAgentFactory _agentFactory;
    private readonly CursorClickResolver _cursorResolver;

    private readonly Lock _agentsLock = new();
    private readonly HashSet<CharacterAgent> _agents = [];

    private CancellationTokenSource? _dispatchCts;
    private Task? _dispatchLoop;

    public event Action<CharacterAgent>? AgentStarted;
    public event Action<CharacterAgent>? AgentIdentified;
    public event Action<CharacterAgent>? AgentStopped;

    /// <summary>
    /// Snapshot of currently-tracked agents. Used by late subscribers (e.g. UI VM that's
    /// constructed after the orchestrator has already started spawning agents from the
    /// first <see cref="ProcessMonitoring.ProcessMonitor"/> poll) to catch up on missed
    /// <see cref="AgentStarted"/> events.
    /// </summary>
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
        CursorClickResolver cursorResolver,
        ILogger<Orchestrator> logger)
    {
        _logger = logger;
        _agentFactory = agentFactory;
        _cursorResolver = cursorResolver;
        _processMonitor = processMonitor;
        _processMonitor.ProcessAppeared += OnProcessAppeared;
        // No subscription to ProcessDisappeared — agents detect dead windows via
        // IGameWindow.IsAlive themselves and self-terminate via AgentStoppingMessage.

        _hotkeyListener = hotkeyListener;
        _hotkeyListener.HotkeyPressed += OnHotkeyPressed;
        _hotkeyListener.MacroHotkeyPressed += OnMacroHotkeyPressed;

        _inbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
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

            case OrchestratorTrigger.BroadcastAssist:
                LogBroadcastAssist();
                _ = BroadcastAsync(new TakeAssistMessage());
                return;

            case OrchestratorTrigger.BroadcastClick:
                BroadcastCursorClick(doubleClick: false);
                return;

            case OrchestratorTrigger.BroadcastDoubleClick:
                BroadcastCursorClick(doubleClick: true);
                return;

            case OrchestratorTrigger.BroadcastIdentify:
                LogBroadcastIdentify();
                _ = BroadcastAsync(new EnterIdentifyMessage());
                return;
        }
    }

    /// <summary>
    /// Programmatic broadcast — UI (Macros dialog) calls this from its Run button to
    /// fire a named macro on every live agent. Same broadcast pipeline as hotkey-fired
    /// messages; agents do their own MacroLibrary lookup and single-flight guarding.
    /// </summary>
    public void BroadcastMacro(string macroName)
    {
        LogBroadcastMacro(macroName);
        _ = BroadcastAsync(new RunMacroMessage(macroName));
    }

    // Delegates cursor / foreground / guard logic to CursorClickResolver. Just snapshots
    // the agent handle set and broadcasts on a non-null result.
    private void BroadcastCursorClick(bool doubleClick)
    {
        HashSet<IntPtr> handles;
        lock (_agentsLock)
        {
            handles = new HashSet<IntPtr>(_agents.Count);
            foreach (var agent in _agents)
            {
                handles.Add(agent.Handle);
            }
        }

        var resolved = _cursorResolver.TryResolve(handles, doubleClick);
        if (resolved is null)
        {
            return;
        }
        _ = BroadcastAsync(new ClickAtMessage(resolved.Value, doubleClick));
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
        _hotkeyListener.MacroHotkeyPressed -= OnMacroHotkeyPressed;
        _dispatchCts?.Cancel();
        _dispatchCts?.Dispose();
        _dispatchCts = null;
    }

    // Macro hotkey fired from the global listener — broadcast the macro by name to
    // every live agent. Identical broadcast path as the Macros-dialog Run button.
    private void OnMacroHotkeyPressed(string macroName)
    {
        BroadcastMacro(macroName);
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
            LogBroadcastEmpty(message.GetType().Name);
            return;
        }

        var recipients = string.Join(", ", snapshot.Select(a => a.Name));
        LogBroadcastStarting(message.GetType().Name, snapshot.Length, recipients);

        var delivered = 0;
        foreach (var agent in snapshot)
        {
            try
            {
                await agent.Inbox.WriteAsync(message, CancellationToken.None).ConfigureAwait(false);
                delivered++;
            }
            catch (ChannelClosedException)
            {
                // Agent stopped between snapshot and write — ignore.
                LogBroadcastChannelClosed(message.GetType().Name, agent.Name);
            }
        }
        LogBroadcastDelivered(message.GetType().Name, delivered, snapshot.Length);
    }

}
