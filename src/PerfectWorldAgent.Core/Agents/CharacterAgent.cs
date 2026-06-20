using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Combat;
using PerfectWorldAgent.Config;
using PerfectWorldAgent.GameWindows;
using PerfectWorldAgent.Identification;
using PerfectWorldAgent.Input;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Presentation;
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
    private readonly TimeSpan _pollInterval;
    private readonly Native.VirtualKey _statsHotkey;
    private readonly TimeSpan _statsOpenDelay;
    private readonly ILogger<CharacterAgent> _logger;
    private readonly StateMachine<AgentState, AgentTrigger> _machine;
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _combatLoopCts;

    public CharacterAgent(
        IGameWindow window,
        ChannelWriter<AgentMessage> outbox,
        ICharacterProvider provider,
        ClassIconService classIcons,
        AgentInputDispatcher input,
        MacroLibrary macros,
        IOptions<AgentOptions> options,
        ILogger<CharacterAgent> logger)
    {
        _window = window;
        _outbox = outbox;
        _provider = provider;
        _classIcons = classIcons;
        _input = input;
        _macros = macros;
        _pollInterval = TimeSpan.FromSeconds(options.Value.AgentPollIntervalSeconds);
        _statsHotkey = options.Value.StatsHotkey;
        _statsOpenDelay = TimeSpan.FromMilliseconds(options.Value.StatsOpenDelayMs);
        _logger = logger;
        _inbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        // Born unidentified — placeholder name unique per-hwnd so it doesn't clash with
        // real characters; empty hotkeys ensure an unidentified agent that tries to act
        // sends nothing meaningful.
        Character = new Character
        {
            Name = $"Unknown (hwnd=0x{window.Handle.ToInt64():X})",
            IsMaster = false,
            Class = CharacterClass.Unknown,
            ImmunityKey = string.Empty,
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

    /// <summary>
    /// Re-label path. Called by the UI when the user wants to update an already-identified
    /// agent (e.g. fixing a misidentification, changing keys, re-snapping the template in
    /// a different lighting context). No state-machine transition; the agent stays in
    /// whatever state it was. Reuses <see cref="AgentIdentifiedMessage"/> so subscribers
    /// refresh.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the agent has never been identified — use <see cref="Identify"/> instead.</exception>
    public void UpdateCharacter(Character character)
    {
        string oldName;
        lock (_stateLock)
        {
            if (_machine.State == AgentState.AwaitingIdentification)
            {
                throw new InvalidOperationException($"Agent '{Name}' has never been identified — use Identify() instead.");
            }

            oldName = Name;
            Character = character;
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

    // On-demand identification, triggered by EnterIdentifyMessage. Opens the in-game
    // stats window (default hotkey C), waits for it to render, captures a screenshot,
    // runs the class matcher, then closes the stats window. On a successful match,
    // promotes via Identify(). On miss or any exception, stays in AwaitingIdentification.
    //
    // Activation/deactivation lifecycle mirrors AgentInputDispatcher.FireKeyAsync but
    // with a screenshot capture sandwiched between the open and close key-presses.
    private async Task IdentifyAsync()
    {
        if (IsIdentified)
        {
            return;
        }

        try
        {
            LogIdentifyAttemptStarted(Name);
            await _window.ActivateAsync().ConfigureAwait(false);
            byte[]? screenshot = null;
            try
            {
                // Open the stats window.
                await _window.PressKeyAsync(_statsHotkey).ConfigureAwait(false);
                // Give PW time to render the panel before capturing.
                await Task.Delay(_statsOpenDelay).ConfigureAwait(false);
                screenshot = _window.CaptureScreenshot();
                // Toggle the stats window closed. We always close, even on match failure,
                // so the user isn't left with stats panels open on every PW client.
                await _window.PressKeyAsync(_statsHotkey).ConfigureAwait(false);
            }
            finally
            {
                await _window.DeactivateAsync().ConfigureAwait(false);
            }

            if (screenshot is null)
            {
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
        catch (Exception ex)
        {
            LogIdentifyFailed(ex);
        }
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
        LogCombatLoopStarted(Name, Character.CombatMacroName);
        try
        {
            var macro = _macros.TryGet(Character.CombatMacroName);
            if (macro is null)
            {
                LogCombatMacroMissing(Name, Character.CombatMacroName);
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

    private async Task RunMacroAsync(Macro macro, CancellationToken cancellationToken)
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
                _ = IdentifyAsync();
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

            LogInboxReceived(message.GetType().Name, Name);
            switch (message)
            {
                case UseImmunityMessage:
                    // Per-character intent — look up our own ImmunityKey and press it.
                    // Empty key string skips silently; unknown VirtualKey name warn-logs.
                    TryFireCharacterAction(Character.ImmunityKey, "ImmunityKey");
                    break;

                case EnterCombatMessage:
                    // Fire SetCombat trigger — state machine OnEntry starts the macro
                    // runner. Re-fired SetCombat while already in InCombat is Ignore'd
                    // by the state machine (per-design "re-press does nothing").
                    TryFireStateTrigger(AgentTrigger.SetCombat);
                    break;

                case TakeAssistMessage:
                    // Master sets the target — they don't need to assist anyone. For
                    // everyone else: Shift+1 selects party member 1 (master), then the
                    // AssistKey-bound /assist macro retargets to master's current target.
                    if (Character.IsMaster)
                    {
                        LogAssistSkippedMaster(Name);
                    }
                    else if (TryParseAssistKey(out var assistKey))
                    {
                        _ = _input.FireAssistAsync(_window, assistKey, Name);
                    }
                    break;

                case ClickAtMessage click:
                    // Coords are in the foreground window's client space; we reuse them
                    // verbatim on our own window. Works when all PW clients are the
                    // same size — typical multi-client setup.
                    _ = _input.FireClickAsync(_window, click.X, click.Y, click.DoubleClick, Name);
                    break;
            }
        }
    }

    // Resolves a Character key-string ("F1", "F8", ...) to a VirtualKey and delegates
    // the actual send to AgentInputDispatcher. Empty string → no binding for this
    // character (typically placeholder / unidentified) → skip silently. Unrecognised
    // string → warn-log and skip.
    private void TryFireCharacterAction(string keyString, string actionName)
    {
        if (string.IsNullOrEmpty(keyString))
        {
            LogActionKeyEmpty(actionName, Name);
            return;
        }

        if (!Enum.TryParse<Native.VirtualKey>(keyString, ignoreCase: true, out var key))
        {
            LogUnknownActionKey(actionName, keyString);
            return;
        }

        _ = _input.FireKeyAsync(_window, key, actionName, Name);
    }

    // Parse Character.AssistKey for the assist sequence. Same skip/warn pattern as
    // TryFireCharacterAction but returns the parsed key for the caller to feed into
    // the dispatcher's FireAssistAsync.
    private bool TryParseAssistKey(out Native.VirtualKey key)
    {
        if (string.IsNullOrEmpty(Character.AssistKey))
        {
            LogAssistKeyMissing();
            key = default;
            return false;
        }

        if (!Enum.TryParse(Character.AssistKey, ignoreCase: true, out key))
        {
            LogUnknownActionKey("AssistKey", Character.AssistKey);
            return false;
        }

        return true;
    }

}
