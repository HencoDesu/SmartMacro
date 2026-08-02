using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Agents;
using SmartMacro.Hotkeys;
using SmartMacro.Input;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.ProcessMonitoring;

namespace SmartMacro.Orchestration;

// The one place a trigger turns into a macro run. Every path — hotkey, process-appeared,
// UI Run — funnels through RunAsync:
//
//   trigger → MacroGraphStore.TryGet(name)
//           → MacroRunRegistry.TryBegin(name, singleFlightKey)   [null = already running]
//           → MacroExecutor.RunAsync(graph, context, handle.Token)
//           → MacroRunRegistry.Complete(runId)                   [always, in finally]
//
// The two trigger sources differ only in what they put in the context:
//   * hotkey           — no context window; the macro routes via tag selectors, and
//                        single-flight is per macro NAME (hammering a hotkey is a no-op).
//   * process-appeared — the new window IS the context, and single-flight is per
//                        (macro, window) so nine clients launching at once each boot.
// Both seed the `cursor` variable, because a macro can't know how it was started.
//
// Beyond that the orchestrator only owns agent lifecycle: spawn one per appeared process,
// hold them so shutdown can stop them all. Nothing outside observes that set — since W0.3
// the UI watches WindowRegistry and MacroRunRegistry instead. Agents no longer receive
// commands either — there is no inbox broadcast any more, just macro runs against handles.
public sealed partial class Orchestrator : IHostedService, IMacroRunner, IDisposable
{
    private readonly ILogger<Orchestrator> _logger;
    private readonly Channel<AgentMessage> _inbox;
    private readonly ProcessMonitor _processMonitor;
    private readonly HotkeyListener _hotkeyListener;
    private readonly ICharacterAgentFactory _agentFactory;
    private readonly MacroGraphStore _macros;
    private readonly MacroExecutor _executor;
    private readonly MacroRunRegistry _runs;
    private readonly CursorPositionProvider _cursor;

    private readonly Lock _agentsLock = new();
    private readonly HashSet<CharacterAgent> _agents = [];

    private CancellationTokenSource? _dispatchCts;
    private Task? _dispatchLoop;

    public Orchestrator(
        ProcessMonitor processMonitor,
        HotkeyListener hotkeyListener,
        ICharacterAgentFactory agentFactory,
        MacroGraphStore macros,
        MacroExecutor executor,
        MacroRunRegistry runs,
        CursorPositionProvider cursor,
        ILogger<Orchestrator> logger)
    {
        _logger = logger;
        _agentFactory = agentFactory;
        _macros = macros;
        _executor = executor;
        _runs = runs;
        _cursor = cursor;

        _processMonitor = processMonitor;
        _processMonitor.ProcessAppeared += OnProcessAppeared;
        // No subscription to ProcessDisappeared — agents detect dead windows via
        // IGameWindow.IsAlive themselves and self-terminate via AgentStoppingMessage.

        _hotkeyListener = hotkeyListener;
        _hotkeyListener.MacroTriggered += OnMacroTriggered;

        _inbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
    }

    /// <summary>
    /// Starts a macro by name with no context window — the manual equivalent of pressing
    /// its hotkey. Used by the UI's Run button. Fire-and-forget; failures are logged.
    /// </summary>
    public void RunMacro(string macroName)
    {
        _ = RunAsync(macroName, contextWindow: null, singleFlightKey: null);
    }

    /// <summary>
    /// Runs one macro to completion under the run registry.
    /// </summary>
    /// <param name="macroName">Graph to run; unknown names are a logged no-op.</param>
    /// <param name="contextWindow">Window targetless nodes act on, or <c>null</c> for selector-only macros.</param>
    /// <param name="singleFlightKey">Dedupe key; <c>null</c> = the macro name (see <see cref="MacroRunRegistry.TryBegin"/>).</param>
    public async Task RunAsync(string macroName, IntPtr? contextWindow, string? singleFlightKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);

        var graph = _macros.TryGet(macroName);
        if (graph is null)
        {
            LogMacroNotFound(macroName);
            return;
        }

        var handle = _runs.TryBegin(macroName, singleFlightKey);
        if (handle is null)
        {
            // Already running under this key — the registry logged it.
            return;
        }

        try
        {
            var context = new MacroRunContext
            {
                ContextWindow = contextWindow,
                Variables = MacroVariables.ForTrigger(_cursor.Current()),
                OnNodeEntered = nodeId => handle.CurrentNodeId = nodeId,
            };
            var result = await _executor.RunAsync(graph, context, handle.Token).ConfigureAwait(false);
            if (result.Status == MacroRunStatus.Aborted)
            {
                LogMacroAborted(macroName, result.Error ?? "(no details)");
            }
        }
        catch (Exception ex)
        {
            // MacroExecutor converts run-level failures into results; anything reaching
            // here is a bug, and must still not take the host down.
            LogMacroFailed(ex, macroName);
        }
        finally
        {
            _runs.Complete(handle.RunId);
        }
    }

    private void OnMacroTriggered(string macroName)
    {
        LogHotkeyTriggered(macroName);
        _ = RunAsync(macroName, contextWindow: null, singleFlightKey: null);
    }

    private void OnProcessAppeared(ProcessInfo info)
    {
        LogProcessAppearedNotification(info.Pid, info.ProcessName, info.MainWindowHandle.ToInt64());
        _ = HandleProcessAppearedAsync(info);
    }

    private async Task HandleProcessAppearedAsync(ProcessInfo info)
    {
        CharacterAgent agent;
        try
        {
            agent = await _agentFactory.CreateAsync(info, _inbox.Writer).ConfigureAwait(false);
            lock (_agentsLock)
            {
                _agents.Add(agent);
            }
            // Start registers the window (and its drivable facade) in WindowRegistry — it
            // must happen before any macro targets the handle.
            agent.Start();
        }
        catch (Exception ex)
        {
            LogAgentCreationFailed(ex, info.Pid);
            return;
        }

        StartProcessAppearedMacros(info.ProcessName, agent.Handle);
    }

    // Every graph with a matching ProcessAppearedTrigger runs against the new window.
    // Several may match — a boot macro plus, say, a window-positioning one — so they all
    // start in parallel, each with its own run entry.
    private void StartProcessAppearedMacros(string processName, IntPtr hwnd)
    {
        foreach (var graph in _macros.All)
        {
            if (!graph.Triggers.OfType<ProcessAppearedTrigger>()
                    .Any(trigger => string.Equals(trigger.ProcessName, processName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            LogProcessMacroStarting(graph.Name, processName, hwnd.ToInt64());
            var key = string.Create(CultureInfo.InvariantCulture, $"{graph.Name}@0x{hwnd.ToInt64():X}");
            _ = RunAsync(graph.Name, hwnd, key);
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
        // Cancel in-flight macro runs first: they drive windows the agents are about to
        // tear down, and a run left mid-activation would keep a client woken up.
        try
        {
            await _runs.StopAllAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

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
        _hotkeyListener.MacroTriggered -= OnMacroTriggered;
        _dispatchCts?.Cancel();
        _dispatchCts?.Dispose();
        _dispatchCts = null;
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
            case AgentStoppingMessage stopping:
                lock (_agentsLock)
                {
                    _agents.Remove(stopping.Agent);
                }
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
}
