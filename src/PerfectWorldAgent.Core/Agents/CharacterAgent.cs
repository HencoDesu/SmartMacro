using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Config;
using PerfectWorldAgent.Core;
using PerfectWorldAgent.Models;
using Stateless;

namespace PerfectWorldAgent.Agents;

// One agent per game-client process. Always created — even before the character is known.
// The game's startup flow goes process-launch → server-select → character-select → in-world,
// and only the last screen exposes the nameplate we'd match against the roster. So an agent
// is born in AwaitingIdentification with a placeholder Character and waits to be promoted
// via Identify().
//
// State graph (see AgentState / AgentTrigger):
//
//   AwaitingIdentification ─ Identified ──► Idle ◄────────┐
//                                            │  ▲          │
//                                       Set* triggers      │
//                                            ▼  │          │
//                                        Following ──── SetCombat ──► InCombat
//                                            │                              │
//                                      StuckDetected                  Set{Hold,Follow}
//                                            ▼                              │
//                                          Stuck ─── Set{Hold,Follow,Combat} ┘
//
// The agent's state machine is independent from the Orchestrator's (which tracks the
// global "what the user wants"). They can diverge: orchestrator broadcasts FOLLOW but a
// particular agent may be Stuck. ModeChangedMessage from the orchestrator maps to
// Set{Hold,Follow,Combat} triggers; Stuck transitions are agent-internal.
//
// Lifecycle:
//   * Start() — kicks off the run loop on the thread pool. Owned CTS + Task lifecycle.
//   * Stop() — cancels the CTS; the loop exits, writes AgentStoppingMessage.
//   * RunningTask — exposed so the orchestrator can await orderly shutdown.
//
// Agent → Orchestrator notifications (identification, stopping, status, stuck) are sent as
// messages into the orchestrator's inbox channel — uniform with the rest of the upstream
// traffic. No C# events on the agent.
public sealed partial class CharacterAgent
{
    private readonly IGameWindow _window;
    private readonly ChannelWriter<AgentMessage> _outbox;
    private readonly Channel<AgentMessage> _inbox;
    private readonly ICharacterProvider _provider;
    private readonly TimeSpan _pollInterval;
    private readonly int _interStepDelayMs;
    private readonly ILogger<CharacterAgent> _logger;
    private readonly StateMachine<AgentState, AgentTrigger> _machine;
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _runCts;

    public CharacterAgent(
        IGameWindow window,
        ChannelWriter<AgentMessage> outbox,
        ICharacterProvider provider,
        IOptions<AgentOptions> options,
        IOptions<Native.ActivatingInputOptions> inputOptions,
        ILogger<CharacterAgent> logger)
    {
        _window = window;
        _outbox = outbox;
        _provider = provider;
        _pollInterval = TimeSpan.FromSeconds(options.Value.AgentPollIntervalSeconds);
        _interStepDelayMs = inputOptions.Value.InterStepDelayMs;
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
            BurstBuffKey = string.Empty,
            DamageKey = string.Empty,
            ImmunityKey = string.Empty,
        };

        _machine = new StateMachine<AgentState, AgentTrigger>(AgentState.AwaitingIdentification);
        _machine.OnTransitioned(t => LogStateTransition(t.Source, t.Destination, t.Trigger));
        _machine.OnUnhandledTrigger((state, trigger) => LogUnhandledTrigger(trigger, state));

        _machine.Configure(AgentState.AwaitingIdentification)
            .Permit(AgentTrigger.Identified, AgentState.Idle)
            .Ignore(AgentTrigger.SetHold)
            .Ignore(AgentTrigger.SetFollow)
            .Ignore(AgentTrigger.SetCombat)
            .Ignore(AgentTrigger.StuckDetected);

        _machine.Configure(AgentState.Idle)
            .PermitReentry(AgentTrigger.SetHold)
            .Permit(AgentTrigger.SetFollow, AgentState.Following)
            .Permit(AgentTrigger.SetCombat, AgentState.InCombat)
            .Ignore(AgentTrigger.Identified)
            .Ignore(AgentTrigger.StuckDetected);

        _machine.Configure(AgentState.Following)
            .Permit(AgentTrigger.SetHold, AgentState.Idle)
            .Permit(AgentTrigger.SetCombat, AgentState.InCombat)
            .Permit(AgentTrigger.StuckDetected, AgentState.Stuck)
            .Ignore(AgentTrigger.SetFollow)
            .Ignore(AgentTrigger.Identified);

        _machine.Configure(AgentState.InCombat)
            .Permit(AgentTrigger.SetHold, AgentState.Idle)
            .Permit(AgentTrigger.SetFollow, AgentState.Following)
            .Ignore(AgentTrigger.SetCombat)
            .Ignore(AgentTrigger.StuckDetected)
            .Ignore(AgentTrigger.Identified);

        // Stuck honours all user mode changes — re-issuing follow effectively a "try
        // again" override; switching to combat lets the player fight where they stand.
        _machine.Configure(AgentState.Stuck)
            .Permit(AgentTrigger.SetHold, AgentState.Idle)
            .Permit(AgentTrigger.SetFollow, AgentState.Following)
            .Permit(AgentTrigger.SetCombat, AgentState.InCombat)
            .Ignore(AgentTrigger.StuckDetected)
            .Ignore(AgentTrigger.Identified);
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

    // Captures the current game window. Exposed for the UI labeling flow: the user
    // clicks "label" on an unidentified row, the dialog calls this to grab the live
    // nameplate, hands it to ICharacterProvider.RegisterAsync, and then calls Identify.
    public byte[] CaptureScreenshot() => _window.CaptureScreenshot();

    public void Start()
    {
        if (RunningTask is not null)
        {
            throw new InvalidOperationException($"Agent '{Name}' is already started.");
        }

        _runCts = new CancellationTokenSource();
        RunningTask = Task.Run(() => RunLoopAsync(_runCts.Token));
    }

    public void Stop()
    {
        _runCts?.Cancel();
    }

    // One-shot promotion. Called by RunLoopAsync after a successful auto-identification or
    // by the UI after the user labels an unknown agent. Updates Character (so Name etc.
    // reflect the new identity) and fires the Identified trigger to leave
    // AwaitingIdentification. Sends AgentIdentifiedMessage to the orchestrator.
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
        TryApplyClassIcon();
        _outbox.TryWrite(new AgentIdentifiedMessage(this, oldName));
    }

    // Re-label path — called by the UI when the user wants to update an already-identified
    // agent (e.g. fixing a misidentification, changing keys, re-snapping the template in
    // a different lighting context). No state-machine transition; the agent stays in
    // whatever state it was. Reuses AgentIdentifiedMessage so subscribers refresh.
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
        TryApplyClassIcon();
        _outbox.TryWrite(new AgentIdentifiedMessage(this, oldName));
    }

    // CharacterClass enum uses Russian display names; user's icon files use English
    // class names. Map between them here so the user can drop their existing icons in
    // Assets/ClassIcons/ without renaming. Adjust if a mapping is wrong (e.g. Оборотень
    // might be "tank" or might be something else depending on which PW build the user
    // pulled the icons from).
    private static readonly IReadOnlyDictionary<CharacterClass, string> ClassIconNames =
        new Dictionary<CharacterClass, string>
        {
            [CharacterClass.Маг] = "mage",
            [CharacterClass.Воин] = "warrior",
            [CharacterClass.Стрелок] = "gunner",
            [CharacterClass.Друид] = "druid",
            [CharacterClass.Оборотень] = "tank",       // Barbarian / shape-shifter
            [CharacterClass.Странник] = "rover",
            [CharacterClass.Жрец] = "priest",
            [CharacterClass.Лучник] = "archer",
            [CharacterClass.Паладин] = "paladin",
            [CharacterClass.Шаман] = "shaman",
            [CharacterClass.Убийца] = "assassin",
            [CharacterClass.Бард] = "bard",
            [CharacterClass.Мистик] = "mystic",
            [CharacterClass.Страж] = "guardian",
            [CharacterClass.ДухКрови] = "bloodspirit",
            [CharacterClass.Жнец] = "reaper",
            [CharacterClass.Призрак] = "ghost",
            // Канлонг — no icon in the user's current set; will silently skip.
        };

    // Best-effort: look for Assets/ClassIcons/<file>.png next to the .exe and apply it
    // as the window's title-bar/taskbar icon. Lets the user distinguish 9 PW windows at
    // a glance in the taskbar instead of seeing 9 identical PW launcher icons. Silent
    // skip for Class=Unknown, unmapped class (Канлонг), or missing file — the icon set
    // is user-managed (drop PNGs into Assets/ClassIcons/, csproj copies them on build).
    private void TryApplyClassIcon()
    {
        if (Character.Class == CharacterClass.Unknown)
        {
            return;
        }

        if (!ClassIconNames.TryGetValue(Character.Class, out var fileStem))
        {
            LogClassIconUnmapped(Character.Class);
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "ClassIcons", $"{fileStem}.png");
        if (!File.Exists(path))
        {
            LogClassIconMissing(Character.Class, path);
            return;
        }

        try
        {
            if (_window.SetIconFromFile(path))
            {
                LogClassIconApplied(Character.Class);
            }
            else
            {
                LogClassIconLoadFailed(Character.Class, path);
            }
        }
        catch (Exception ex)
        {
            LogClassIconException(ex, Character.Class);
        }
    }

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
                Tick();

                await WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogRunFailed(ex);
        }
        finally
        {
            LogStopped(Name);
            _outbox.TryWrite(new AgentStoppingMessage(this));
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    // Sleeps until *either* a new inbox message arrives *or* the poll interval expires.
    // The wake-on-message branch is the synchronization fix: when the orchestrator
    // broadcasts SendKeyMessage / ModeChangedMessage / etc., all agents become runnable
    // within microseconds rather than up to PollInterval seconds apart (which was the
    // observed inter-agent skew under fixed Task.Delay).
    private async Task WaitForNextTickAsync(CancellationToken runCancellation)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(runCancellation);
        timeoutCts.CancelAfter(_pollInterval);
        try
        {
            // Returns true if items available, false if channel completed. We don't care
            // about the bool — the next loop iteration will DrainInbox unconditionally.
            await _inbox.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the poll timeout fired (normal tick) or runCancellation cancelled
            // (we're shutting down). Either way the outer loop's cancellation check
            // decides what to do next; we don't need to rethrow.
        }
    }

    // State-aware per-tick work. The state machine routes — each state's body is the
    // per-state behaviour (still mostly TODO; auto-identification is the only real one).
    private void Tick()
    {
        switch (_machine.State)
        {
            case AgentState.AwaitingIdentification:
                TryIdentify();
                break;

            case AgentState.Idle:
                // No work — agent is identified and waiting for orders.
                break;

            case AgentState.Following:
                // TODO: re-issue /follow if master moved out of range, capture coords for
                // StuckDetector, fire StuckDetected trigger if appropriate.
                break;

            case AgentState.InCombat:
                // TODO: combat rotation — burst buff key, damage key, cooldown bookkeeping.
                break;

            case AgentState.Stuck:
                // TODO: recovery attempt — re-activate follow, jump, or notify player.
                break;
        }
    }

    private void TryIdentify()
    {
        byte[] screenshot;
        try
        {
            // Passive capture — no WM_ACTIVATEAPP traffic for the periodic poll. The
            // user's manual focus on a game window stays uninterrupted; price is that a
            // truly frozen background frame may not identify on this tick (retried next
            // tick).
            screenshot = _window.CaptureScreenshotPassive();
        }
        catch (Exception ex)
        {
            LogCaptureFailed(ex);
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
            return;
        }

        Identify(identified);
    }

    private void DrainInbox()
    {
        while (_inbox.Reader.TryRead(out var message))
        {
            // Until the agent is labeled, swallow all broadcast actions silently. We
            // genuinely don't know whose window this is yet — sending immunity / clicks /
            // mode changes could land in the wrong place (e.g. character-select screen,
            // chat input, the launcher). Identification work itself runs from Tick()
            // and uses screenshots; it doesn't go through the inbox.
            if (!IsIdentified)
            {
                continue;
            }

            LogInboxReceived(message.GetType().Name, Name);
            switch (message)
            {
                case ModeChangedMessage mode:
                    FireModeTrigger(mode.Mode);
                    break;

                case UseImmunityMessage:
                    // Per-character intent — look up our own ImmunityKey and press it.
                    // Empty key string skips silently; unknown VirtualKey name warn-logs.
                    TryFireCharacterAction(Character.ImmunityKey, "ImmunityKey");
                    break;

                case TakeAssistMessage:
                    // Master sets the target — they don't need to assist anyone. For
                    // everyone else: Shift+1 selects party member 1 (master), then the
                    // AssistKey-bound /assist macro retargets to master's current target.
                    if (!Character.IsMaster)
                    {
                        _ = SendAssistSequenceAsync();
                    }
                    else
                    {
                        LogAssistSkippedMaster(Name);
                    }
                    break;

                case ClickAtMessage click:
                    // Coords are in the foreground window's client space; we reuse them
                    // verbatim on our own window. Works when all PW clients are the
                    // same size — typical multi-client setup.
                    _ = SendClickSafelyAsync(click.X, click.Y, click.DoubleClick);
                    break;

                // ExecuteActionMessage, ShutdownMessage — TODO: act on these once per-mode
                // work is real. For now drop silently so the channel doesn't backlog.
            }
        }
    }

    // Resolves a Character key-string ("F1", "F8", ...) to a VirtualKey and fires it
    // asynchronously. Empty string → no binding for this character (typically placeholder
    // / unidentified) → skip silently. Unrecognised string → warn-log and skip.
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

        _ = SendKeySafelyAsync(key, actionName);
    }

    private async Task SendKeySafelyAsync(Native.VirtualKey key, string actionName = "Key")
    {
        try
        {
            LogActionFiring(actionName, key, Name);
            await _window.ActivateAsync().ConfigureAwait(false);
            try
            {
                await _window.PressKeyAsync(key).ConfigureAwait(false);
            }
            finally
            {
                await _window.DeactivateAsync().ConfigureAwait(false);
            }
            LogActionFired(actionName, key, Name);
        }
        catch (Exception ex)
        {
            LogSendKeyFailed(ex, key, Name);
        }
    }

    // Two-step assist: Shift+1 selects party-slot 1 (master), AssistKey fires the
    // in-game /assist macro against the now-selected master. Both happen inside a SINGLE
    // ActivateAsync/DeactivateAsync cycle — window stays active across both phases so PW
    // doesn't drop the chord's effect on intermediate deactivation. The Task.Delay
    // between chord and follow-up uses the configured InterStepDelayMs — gives PW time
    // to actually apply the party-member selection (update target frame, sync internal
    // target state) before /assist runs. Tune via appsettings.json
    // Input:Activating:InterStepDelayMs if some agents still target master after assist.
    private async Task SendAssistSequenceAsync()
    {
        if (string.IsNullOrEmpty(Character.AssistKey))
        {
            LogAssistKeyMissing();
            return;
        }

        if (!Enum.TryParse<Native.VirtualKey>(Character.AssistKey, ignoreCase: true, out var assistKey))
        {
            LogUnknownActionKey("AssistKey", Character.AssistKey);
            return;
        }

        try
        {
            await _window.ActivateAsync().ConfigureAwait(false);
            try
            {
                await _window.PressChordAsync(Native.VirtualKey.Shift, Native.VirtualKey.D1).ConfigureAwait(false);
                await Task.Delay(_interStepDelayMs).ConfigureAwait(false);
                await _window.PressKeyAsync(assistKey).ConfigureAwait(false);
            }
            finally
            {
                await _window.DeactivateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogAssistChordFailed(ex);
        }
    }

    private async Task SendClickSafelyAsync(int x, int y, bool doubleClick)
    {
        try
        {
            await _window.ActivateAsync().ConfigureAwait(false);
            try
            {
                if (doubleClick)
                {
                    await _window.DoubleClickAsync(x, y).ConfigureAwait(false);
                }
                else
                {
                    await _window.ClickAsync(x, y).ConfigureAwait(false);
                }
            }
            finally
            {
                await _window.DeactivateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogSendClickFailed(ex, x, y, doubleClick);
        }
    }

    private void FireModeTrigger(AgentMode mode)
    {
        var trigger = mode switch
        {
            AgentMode.Hold => AgentTrigger.SetHold,
            AgentMode.Follow => AgentTrigger.SetFollow,
            AgentMode.Combat => AgentTrigger.SetCombat,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown AgentMode"),
        };

        lock (_stateLock)
        {
            _machine.Fire(trigger);
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Agent '{Name}' run loop started (state={State}, poll={Poll})")]
    partial void LogStarted(string name, AgentState state, TimeSpan poll);

    [LoggerMessage(LogLevel.Information, "Agent '{Name}' run loop stopped")]
    partial void LogStopped(string name);

    [LoggerMessage(LogLevel.Information, "Agent promoted: '{OldName}' -> '{NewName}'")]
    partial void LogPromoted(string oldName, string newName);

    [LoggerMessage(LogLevel.Information, "Agent window no longer alive; exiting run loop")]
    partial void LogWindowGone();

    [LoggerMessage(LogLevel.Debug, "State transition: {Source} -> {Destination} (trigger {Trigger})")]
    partial void LogStateTransition(AgentState source, AgentState destination, AgentTrigger trigger);

    [LoggerMessage(LogLevel.Warning, "Unhandled trigger {Trigger} in state {State}")]
    partial void LogUnhandledTrigger(AgentTrigger trigger, AgentState state);

    // Debug-level — transient failures during identification polling (window minimised,
    // alt-tabbed, launcher process without a real client area, GPU stalled). Genuine
    // window destruction is detected separately via IsAlive at the top of the run loop.
    [LoggerMessage(LogLevel.Debug, "Screenshot capture failed during identification poll")]
    partial void LogCaptureFailed(Exception ex);

    [LoggerMessage(LogLevel.Error, "Identification failed during poll")]
    partial void LogIdentifyFailed(Exception ex);

    [LoggerMessage(LogLevel.Error, "Agent run loop failed unexpectedly")]
    partial void LogRunFailed(Exception ex);

    [LoggerMessage(LogLevel.Error, "PressKeyAsync({Key}) failed on '{Name}'")]
    partial void LogSendKeyFailed(Exception ex, Native.VirtualKey key, string name);

    [LoggerMessage(LogLevel.Debug, "Inbox: '{Name}' received {MessageType}")]
    partial void LogInboxReceived(string messageType, string name);

    [LoggerMessage(LogLevel.Information, "Firing {Action}={Key} on '{Name}'")]
    partial void LogActionFiring(string action, Native.VirtualKey key, string name);

    [LoggerMessage(LogLevel.Information, "Fired {Action}={Key} on '{Name}' OK")]
    partial void LogActionFired(string action, Native.VirtualKey key, string name);

    [LoggerMessage(LogLevel.Information, "{Action} not configured for '{Name}' — skipping silently")]
    partial void LogActionKeyEmpty(string action, string name);

    [LoggerMessage(LogLevel.Debug, "Assist skipped for '{Name}' — IsMaster=true")]
    partial void LogAssistSkippedMaster(string name);

    [LoggerMessage(LogLevel.Warning, "Character {Action} = '{KeyString}' does not parse as a known VirtualKey; skipping")]
    partial void LogUnknownActionKey(string action, string keyString);

    [LoggerMessage(LogLevel.Error, "Click ({X},{Y}) doubleClick={DoubleClick} failed")]
    partial void LogSendClickFailed(Exception ex, int x, int y, bool doubleClick);

    [LoggerMessage(LogLevel.Debug, "Class icon file not found for {Cls} at {Path}; taskbar icon stays default")]
    partial void LogClassIconMissing(CharacterClass cls, string path);

    [LoggerMessage(LogLevel.Information, "Class icon applied for {Cls}")]
    partial void LogClassIconApplied(CharacterClass cls);

    [LoggerMessage(LogLevel.Warning, "Class icon LoadImage failed for {Cls} at {Path} (file may be corrupt or not a valid .ico)")]
    partial void LogClassIconLoadFailed(CharacterClass cls, string path);

    [LoggerMessage(LogLevel.Error, "Class icon application threw for {Cls}")]
    partial void LogClassIconException(Exception ex, CharacterClass cls);

    [LoggerMessage(LogLevel.Debug, "No icon filename mapping for {Cls}; taskbar icon stays default")]
    partial void LogClassIconUnmapped(CharacterClass cls);

    [LoggerMessage(LogLevel.Error, "PressChord(Shift+D1) failed during assist sequence")]
    partial void LogAssistChordFailed(Exception ex);

    [LoggerMessage(LogLevel.Debug, "AssistKey not set on character; party-member-1 selected but no /assist macro fired")]
    partial void LogAssistKeyMissing();

    #endregion
}
