using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Config;
using PerfectWorldAgent.GameWindows;
using PerfectWorldAgent.Identification;
using PerfectWorldAgent.Input;
using PerfectWorldAgent.Macro;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Presentation;
using PerfectWorldAgent.Vision;
using Stateless;

namespace PerfectWorldAgent.Agents;

// One agent per game-client process. Always created — even before the character is known.
// The game's startup flow goes process-launch → server-select → character-select → in-world,
// and only the last screen exposes the nameplate we'd match against the roster. So an agent
// is born in AwaitingIdentification with a placeholder Character and waits to be promoted
// via Identify().
//
// State graph (minimal v1):
//
//   AwaitingIdentification ── Identified ──► Idle
//
// AwaitingIdentification is now passive — no polling loop. The agent waits in this
// state until it receives an EnterIdentifyMessage (fired by the BroadcastIdentify
// hotkey), at which point it runs IdentifyAsync (open stats → capture → match → close).
// On a class match, promote to Idle; on miss, stay in AwaitingIdentification.
//
// Lifecycle:
//   * Start() — kicks off the main run loop on the thread pool. Owned CTS + Task.
//   * Stop() — cancels the CTS; the loop exits, writes AgentStoppingMessage.
//   * RunningTask — exposed so the orchestrator can await orderly shutdown.
//
// Agent → Orchestrator notifications (identification, stopping) go through the orchestrator's
// inbox channel — uniform with the rest of the upstream traffic.
public sealed partial class CharacterAgent
{
    // Hard upper bound on how long an agent stays InCombat regardless of macro length.
    // Per-design: a boss either dies or goes immune within ~10s; the agent returns to
    // Idle automatically so the user doesn't need a separate Stop hotkey.
    private static readonly TimeSpan CombatWindow = TimeSpan.FromSeconds(10);

    private readonly IGameWindow _window;
    private readonly ChannelWriter<AgentMessage> _outbox;
    private readonly Channel<AgentMessage> _inbox;
    private readonly ICharacterProvider _provider;
    private readonly ClassIconService _classIcons;
    private readonly AgentInputDispatcher _input;
    private readonly MacroLibrary _macros;
    private readonly GameUiElementExample _uiTemplates;
    private readonly TimeSpan _pollInterval;
    private readonly Native.VirtualKey _statsHotkey;
    private readonly TimeSpan _statsOpenDelay;
    private readonly Native.ScreenPoint _serverSelectButton;
    private readonly Native.ScreenPoint _characterSelectButton;
    private readonly Native.ScreenRect _serverSelectRegion;
    private readonly Native.ScreenRect _characterSelectRegion;
    private readonly Native.ScreenRect _inWorldRegion;
    private readonly TimeSpan _bootPhaseTimeout;
    private readonly string _defaultImmunityKey;
    private readonly string _defaultAssistKey;
    private readonly string _defaultCombatMacroName;
    private readonly CharacterClass _masterClass;
    private readonly HashSet<CharacterClass> _ignoredClasses;
    private readonly ILogger<CharacterAgent> _logger;
    private readonly StateMachine<AgentState, AgentTrigger> _machine;
    private readonly Lock _stateLock = new();
    // Single-flight guard shared by EnterWorldAsync and IdentifyAsync. WaitAsync(0)
    // returns false on contention so a stacked-up trigger no-ops instead of letting
    // two concurrent activate/click/key cycles race the same window.
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _combatLoopCts;

    public CharacterAgent(
        IGameWindow window,
        ChannelWriter<AgentMessage> outbox,
        ICharacterProvider provider,
        ClassIconService classIcons,
        AgentInputDispatcher input,
        MacroLibrary macros,
        GameUiElementExample uiTemplates,
        IOptions<AgentOptions> options,
        ILogger<CharacterAgent> logger)
    {
        _window = window;
        _outbox = outbox;
        _provider = provider;
        _classIcons = classIcons;
        _input = input;
        _macros = macros;
        _uiTemplates = uiTemplates;
        _pollInterval = TimeSpan.FromSeconds(options.Value.AgentPollIntervalSeconds);
        _statsHotkey = options.Value.StatsHotkey;
        _statsOpenDelay = TimeSpan.FromMilliseconds(options.Value.StatsOpenDelayMs);
        _serverSelectButton = options.Value.ServerSelectButton;
        _characterSelectButton = options.Value.CharacterSelectButton;
        _serverSelectRegion = options.Value.ServerSelectRegion;
        _characterSelectRegion = options.Value.CharacterSelectRegion;
        _inWorldRegion = options.Value.InWorldRegion;
        _bootPhaseTimeout = TimeSpan.FromMilliseconds(options.Value.BootPhaseTimeoutMs);
        _defaultImmunityKey = options.Value.DefaultImmunityKey;
        _defaultAssistKey = options.Value.DefaultAssistKey;
        _defaultCombatMacroName = options.Value.DefaultCombatMacroName;
        _masterClass = options.Value.MasterClass;
        _ignoredClasses = new HashSet<CharacterClass>(options.Value.IgnoredClasses);
        _logger = logger;
        _inbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        // Born unidentified — placeholder name unique per-hwnd so it doesn't clash with
        // real characters. Class=Unknown until promoted by Identify().
        Character = new Character
        {
            Name = $"Unknown (hwnd=0x{window.Handle.ToInt64():X})",
            Class = CharacterClass.Unknown,
        };

        _machine = new StateMachine<AgentState, AgentTrigger>(AgentState.AwaitingIdentification);
        _machine.OnTransitioned(t => LogStateTransition(t.Source, t.Destination, t.Trigger));
        _machine.OnUnhandledTrigger((state, trigger) => LogUnhandledTrigger(trigger, state));

        _machine.Configure(AgentState.AwaitingIdentification)
            .Permit(AgentTrigger.Identified, AgentState.Idle);

        _machine.Configure(AgentState.Idle)
            .Ignore(AgentTrigger.Identified)
            .Ignore(AgentTrigger.SetIdle)
            .Permit(AgentTrigger.SetCombat, AgentState.InCombat);

        _machine.Configure(AgentState.InCombat)
            .OnEntry(StartCombatLoop)
            .OnExit(StopCombatLoop)
            .Ignore(AgentTrigger.SetCombat)   // per-design: re-press while running is a no-op
            .Permit(AgentTrigger.SetIdle, AgentState.Idle);
    }

    public Character Character { get; private set; }

    public string Name => Character.Name;
    public AgentState State => _machine.State;
    public bool IsIdentified => _machine.State != AgentState.AwaitingIdentification;

    // Master = the class designated in AgentOptions.MasterClass (typically Лучник).
    // The master is the one being /assist'd by everyone else, so it skips
    // TakeAssistMessage itself.
    public bool IsMaster => IsIdentified && Character.Class == _masterClass;

    // Ignored = class is in AgentOptions.IgnoredClasses. Used for utility characters
    // like a warehouse mule — they boot + identify normally but drop all broadcasts.
    public bool IsIgnored => IsIdentified && _ignoredClasses.Contains(Character.Class);
    public ChannelWriter<AgentMessage> Inbox => _inbox.Writer;
    public Task? RunningTask { get; private set; }

    // Underlying game-window handle. Exposed so the orchestrator can recognise when the
    // current foreground / window-under-cursor belongs to one of its tracked agents
    // (used by the BroadcastClick guard to suppress clicks when the user is outside the
    // game).
    public IntPtr Handle => _window.Handle;

    /// <summary>
    /// Active capture of the current game window. Exposed for diagnostics (dump-captures
    /// debug flow). Identification no longer needs UI-driven capture — class-based
    /// matching happens server-side via the BroadcastIdentify hotkey path.
    /// </summary>
    public byte[] CaptureScreenshot() => _window.CaptureScreenshot();

    /// <summary>
    /// Kicks off the main run loop on the thread pool and starts the identification loop.
    /// Idempotency: throws if the agent is already running.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when called on an already-running agent.</exception>
    public void Start()
    {
        if (RunningTask is not null)
        {
            throw new InvalidOperationException($"Agent '{Name}' is already started.");
        }

        _runCts = new CancellationTokenSource();
        RunningTask = Task.Run(() => RunLoopAsync(_runCts.Token));

        // Auto-kick the boot pipeline (vision-driven phase polls → click → identify).
        // Fire-and-forget; on success it calls Identify() which fires the state-machine
        // transition. The captured _runCts.Token cancels mid-flow if Stop() runs.
        _ = EnterWorldAsync(_runCts.Token);
    }

    /// <summary>
    /// Signals the run loop to stop. The loop exits, writes <see cref="AgentStoppingMessage"/>,
    /// and the task completes via <see cref="RunningTask"/>.
    /// </summary>
    public void Stop()
    {
        _runCts?.Cancel();
    }

    /// <summary>
    /// One-shot promotion from unidentified to identified. Called by the identification
    /// loop after a successful auto-match or by the UI after the user labels an unknown
    /// agent. Updates <see cref="Character"/>, fires the <c>Identified</c> trigger to
    /// leave <see cref="AgentState.AwaitingIdentification"/> (cancelling the identification
    /// loop via OnExit), applies the class icon, and notifies the orchestrator.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the agent is already identified.</exception>
    public void Identify(Character character)
    {
        string oldName;
        lock (_stateLock)
        {
            if (_machine.State != AgentState.AwaitingIdentification)
            {
                throw new InvalidOperationException($"Agent '{Name}' is already identified.");
            }

            oldName = Name;
            Character = character;
            _machine.Fire(AgentTrigger.Identified);
        }

        LogPromoted(oldName, character.Name);
        _classIcons.TryApply(_window, character.Class);
        _outbox.TryWrite(new AgentIdentifiedMessage(this, oldName));
    }

    // Main run loop — responsible only for window-death detection and inbox draining.
    // State-specific work happens in per-state loops triggered by the state machine's
    // OnEntry/OnExit hooks. The main loop wakes on inbox messages or window-aliveness
    // ticks; per-state loops run on their own cadence.
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        LogStarted(Name, _machine.State, _pollInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_window.IsAlive)
                {
                    LogWindowGone();
                    break;
                }

                DrainInbox();

                await WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogRunFailed(ex);
        }
        finally
        {
            // Make sure any state-specific loops also stop. They observe their own CTS
            // and exit; we don't need to await.
            StopCombatLoop();
            LogStopped(Name);
            _outbox.TryWrite(new AgentStoppingMessage(this));
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    // Sleeps until *either* a new inbox message arrives *or* a periodic window-aliveness
    // tick fires. The wake-on-message branch is the synchronization fix: when the
    // orchestrator broadcasts UseImmunityMessage etc., all agents become runnable within
    // microseconds rather than up to PollInterval seconds apart.
    private async Task WaitForNextTickAsync(CancellationToken runCancellation)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(runCancellation);
        timeoutCts.CancelAfter(_pollInterval);
        try
        {
            await _inbox.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the poll timeout fired or runCancellation cancelled — outer loop
            // re-checks cancellation; nothing to rethrow.
        }
    }

    // Boot flow — vision-driven poll-loops:
    //   1. Capture once. If in-world template matches → skip everything and jump to identify.
    //   2. Else poll until ServerSelect template matches → click confirm.
    //   3. Poll until CharacterSelect template matches → click confirm.
    //   4. Poll until InWorld template matches.
    //   5. Identify the character.
    //
    // Each phase has its own timeout (BootDetectorOptions.PerPhaseTimeoutMs). If a
    // template is missing or the screen never transitions, the phase logs a timeout
    // and the whole flow aborts — operator can re-trigger via BroadcastIdentify.
    //
    // Single-flight via _operationLock — shared with IdentifyAsync to prevent
    // concurrent activate/click/key races on the same window.
    private async Task EnterWorldAsync(CancellationToken cancellationToken)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            LogOperationAlreadyRunning(Name, "EnterWorld");
            return;
        }

        try
        {
            LogBootFlowStarting(Name);

            // Short-circuit: client may already be in-world (app restart, reconnect,
            // master being actively played). A quick poll with a tiny budget resolves
            // it without firing any boot clicks.
            if (await WaitForPhaseAsync(BootPhase.InWorld, TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false))
            {
                LogBootSkippedAlreadyInWorld(Name);
                await IdentifyAsync_NoLock(cancellationToken).ConfigureAwait(false);
                return;
            }

            // Phase 1: server-select. Poll until the confirm button is rendered, then click.
            if (!await WaitForPhaseAsync(BootPhase.ServerSelect, _bootPhaseTimeout, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await ClickBootButtonAsync(_serverSelectButton, "ServerSelect").ConfigureAwait(false);

            // Phase 2: character-select.
            if (!await WaitForPhaseAsync(BootPhase.CharacterSelect, _bootPhaseTimeout, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await ClickBootButtonAsync(_characterSelectButton, "CharacterSelect").ConfigureAwait(false);

            // Phase 3: in-world.
            if (!await WaitForPhaseAsync(BootPhase.InWorld, _bootPhaseTimeout, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            LogBootFlowFinished(Name);

            // Identify the freshly-arrived character.
            await IdentifyAsync_NoLock(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop() mid-flow; nothing to log.
        }
        catch (Exception ex)
        {
            LogBootFlowFailed(ex);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // Delegates to IGameWindow.WaitForElementAt — vision polling + template match lives
    // there now. We just supply the template (loaded once at startup by
    // GameUiElementLoader) and the per-phase region from options. Empty region =
    // fullscreen search.
    private async Task<bool> WaitForPhaseAsync(BootPhase phase, TimeSpan budget, CancellationToken cancellationToken)
    {
        LogBootWaitingForPhase(Name, phase);

        var (template, region) = phase switch
        {
            BootPhase.ServerSelect => (_uiTemplates.ServerSelectButton, _serverSelectRegion),
            BootPhase.CharacterSelect => (_uiTemplates.CharacterSelectButton, _characterSelectRegion),
            BootPhase.InWorld => (_uiTemplates.ChatSettingsButton, _inWorldRegion),
            _ => ([], default),
        };

        var ready = await _window.WaitForElementAt(template, region, budget, cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            LogBootPhaseReady(Name, phase);
        }
        else
        {
            LogBootPhaseTimeout(Name, phase, (int)budget.TotalSeconds);
        }
        return ready;
    }

    // Single identify pass — opens stats, captures, matches class, closes stats. Does
    // NOT advance through boot screens; assumes the agent is already in-world. On a
    // match, promotes via Identify() (state-machine transition); on miss, stays
    // AwaitingIdentification. Triggered by EnterIdentifyMessage (BroadcastIdentify hotkey).
    //
    // Public entry path that acquires the shared single-flight lock. EnterWorldAsync
    // calls IdentifyAsync_NoLock directly because it already holds the lock.
    private async Task IdentifyAsync(CancellationToken cancellationToken)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            LogOperationAlreadyRunning(Name, "Identify");
            return;
        }
        try
        {
            await IdentifyAsync_NoLock(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // Inner identify logic — caller MUST hold _operationLock. Used both by IdentifyAsync
    // (which acquires the lock around it) and by EnterWorldAsync (which already holds
    // the lock for the entire boot+identify pipeline).
    private async Task IdentifyAsync_NoLock(CancellationToken cancellationToken)
    {
        if (IsIdentified)
        {
            return;
        }

        LogIdentifyAttemptStarted(Name);
        byte[]? screenshot = null;
        try
        {
            await _window.ActivateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _window.PressKeyAsync(_statsHotkey, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_statsOpenDelay, cancellationToken).ConfigureAwait(false);
                // Passive capture — the outer ActivateAsync already woke the window.
                // Active CaptureScreenshot would re-send WM_ACTIVATEAPP and then
                // DEACTIVATE if the window isn't foreground, which leaves the second
                // stats-toggle press flying into a deactivated window that ignores it —
                // result: stats stays open after identification.
                screenshot = _window.CaptureScreenshotPassive();
                await _window.PressKeyAsync(_statsHotkey, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _window.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogIdentifyFailed(ex);
            return;
        }

        Character? identified;
        try
        {
            identified = _provider.Identify(screenshot);
        }
        catch (Exception ex)
        {
            LogIdentifyFailed(ex);
            return;
        }

        if (identified is null)
        {
            LogIdentifyNoMatch(Name);
            return;
        }

        Identify(identified);
    }

    // Wraps activation around a single click at the given client point. Failures are
    // logged (via AgentInputDispatcher) and swallowed — a click that errors out doesn't
    // abort the boot flow, since the boot sequence has best-effort semantics anyway.
    private Task ClickBootButtonAsync(Native.ScreenPoint point, string label)
    {
        LogBootClick(Name, label, point);
        return _input.FireClickAsync(_window, point, doubleClick: false, Name);
    }

    // State-specific loop for InCombat: runs the character's macro within a 10-second
    // hard window. After the window elapses (regardless of where in the macro we are),
    // fires SetIdle to transition back. Re-pressing the Combat hotkey while we're still
    // in this state is ignored at the state-machine level.
    private void StartCombatLoop()
    {
        if (_combatLoopCts is not null)
        {
            return;
        }
        _combatLoopCts = new CancellationTokenSource();
        _combatLoopCts.CancelAfter(CombatWindow);
        _ = Task.Run(() => CombatLoopAsync(_combatLoopCts.Token));
    }

    private void StopCombatLoop()
    {
        _combatLoopCts?.Cancel();
        _combatLoopCts?.Dispose();
        _combatLoopCts = null;
    }

    private async Task CombatLoopAsync(CancellationToken cancellationToken)
    {
        LogCombatLoopStarted(Name, _defaultCombatMacroName);
        try
        {
            var macro = _macros.TryGet(_defaultCombatMacroName);
            if (macro is null)
            {
                LogCombatMacroMissing(Name, _defaultCombatMacroName);
            }
            else
            {
                await RunMacroAsync(macro, cancellationToken).ConfigureAwait(false);
            }

            // Wait out the remainder of the 10s window. We arrive here either because
            // the macro completed quickly or it didn't exist; either way we hold the
            // agent in InCombat until the timeout cancels us.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — either 10s elapsed or external stop. Fall through to fire SetIdle.
        }
        catch (Exception ex)
        {
            LogCombatLoopFailed(ex);
        }
        finally
        {
            LogCombatLoopStopped(Name);
            TryFireStateTrigger(AgentTrigger.SetIdle);
        }
    }

    private async Task RunMacroAsync(Macro.Macro macro, CancellationToken cancellationToken)
    {
        foreach (var step in macro.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _input.FireKeyAsync(_window, step.Key, $"Macro({macro.Name})", Name).ConfigureAwait(false);
            if (step.DelayMs > 0)
            {
                await Task.Delay(step.DelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Fires a trigger under the state lock. Silent if the current state doesn't permit
    // the trigger (the state machine's OnUnhandledTrigger handler will log it). Used by
    // both inbox handlers (SetCombat) and the combat loop's finally block (SetIdle).
    private void TryFireStateTrigger(AgentTrigger trigger)
    {
        lock (_stateLock)
        {
            if (_machine.CanFire(trigger))
            {
                _machine.Fire(trigger);
            }
        }
    }

    private void DrainInbox()
    {
        while (_inbox.Reader.TryRead(out var message))
        {
            // EnterIdentifyMessage is the one inbox message handled BEFORE the
            // unidentified-guard: it's specifically meant for unidentified agents to
            // promote themselves. All other broadcasts are no-ops until identified.
            if (message is EnterIdentifyMessage)
            {
                LogInboxReceived(message.GetType().Name, Name);
                // Routes through identification-only (no boot clicks). Assumes the agent
                // is already in-world (boot ran on Start). The single-flight semaphore
                // means a hotkey press during an in-progress boot is a no-op — operator
                // can re-press once boot finishes.
                var ct = _runCts?.Token ?? CancellationToken.None;
                _ = IdentifyAsync(ct);
                continue;
            }

            // Until the agent is labeled, swallow all other broadcast actions silently.
            // We genuinely don't know whose window this is yet — sending immunity / clicks
            // could land in the wrong place (e.g. character-select screen, chat input,
            // the launcher).
            if (!IsIdentified)
            {
                continue;
            }

            // Ignored class (warehouse / utility character) — drop the broadcast.
            // Boot + identify still happen so the agent makes it in-world, but it
            // doesn't act on party-wide commands.
            if (IsIgnored)
            {
                LogBroadcastIgnored(message.GetType().Name, Name, Character.Class);
                continue;
            }

            LogInboxReceived(message.GetType().Name, Name);
            switch (message)
            {
                case UseImmunityMessage:
                    // Same default ImmunityKey on every agent — all clients are
                    // configured identically in-game.
                    TryFireKey(_defaultImmunityKey, "ImmunityKey");
                    break;

                case EnterCombatMessage:
                    // Fire SetCombat trigger — state machine OnEntry starts the macro
                    // runner. Re-fired SetCombat while already in InCombat is Ignore'd
                    // by the state machine (per-design "re-press does nothing").
                    TryFireStateTrigger(AgentTrigger.SetCombat);
                    break;

                case TakeAssistMessage:
                    // Master is the one being assisted on — they don't /assist anyone.
                    // For everyone else: click party-slot-1 (master portrait) + fire
                    // AssistKey → /assist macro retargets to master's current target.
                    if (IsMaster)
                    {
                        LogAssistSkippedMaster(Name);
                    }
                    else if (TryParseKey(_defaultAssistKey, "AssistKey", out var assistKey))
                    {
                        _ = _input.FireAssistAsync(_window, assistKey, Name);
                    }
                    break;

                case ClickAtMessage click:
                    // Point is in the foreground window's client space; we reuse it
                    // verbatim on our own window. Works when all PW clients are the
                    // same size — typical multi-client setup.
                    _ = _input.FireClickAsync(_window, click.Point, click.DoubleClick, Name);
                    break;
            }
        }
    }

    // Resolves a key-string ("F1", "F8", ...) to a VirtualKey and delegates the actual
    // send to AgentInputDispatcher. Empty string → no binding configured → skip silently.
    // Unrecognised string → warn-log and skip.
    private void TryFireKey(string keyString, string actionName)
    {
        if (!TryParseKey(keyString, actionName, out var key))
        {
            return;
        }
        _ = _input.FireKeyAsync(_window, key, actionName, Name);
    }

    private bool TryParseKey(string keyString, string actionName, out Native.VirtualKey key)
    {
        if (string.IsNullOrEmpty(keyString))
        {
            LogActionKeyEmpty(actionName, Name);
            key = default;
            return false;
        }

        if (!Enum.TryParse(keyString, ignoreCase: true, out key))
        {
            LogUnknownActionKey(actionName, keyString);
            return false;
        }
        return true;
    }

}
