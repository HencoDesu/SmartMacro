using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.Identification;
using SmartMacro.Input;
using SmartMacro.Macro;
using SmartMacro.Presentation;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Agents;

// One agent per game-client process. Always created — even before the window is tagged.
// The game's startup flow goes process-launch → server-select → character-select → in-world,
// and only the last screen exposes the stats window we'd match against the tag templates.
//
// Identity now lives in WindowRegistry: the agent registers its window on Start, applies
// tags via Identify(), and unregisters when its run loop exits. "Identified" simply means
// "the window carries at least one tag" — there is no state machine anymore; the State
// string exposed for the UI is derived from IsIdentified.
//
// Lifecycle:
//   * Start() — registers the window and kicks off the main run loop on the thread pool.
//   * Stop() — cancels the CTS; the loop exits, unregisters the window, writes
//     AgentStoppingMessage.
//   * RunningTask — exposed so the orchestrator can await orderly shutdown.
//
// Agent → Orchestrator notifications (identification, stopping) go through the orchestrator's
// inbox channel — uniform with the rest of the upstream traffic.
public sealed partial class CharacterAgent
{
    private readonly IGameWindow _window;
    private readonly string _processName;
    private readonly WindowRegistry _registry;
    private readonly ChannelWriter<AgentMessage> _outbox;
    private readonly Channel<AgentMessage> _inbox;
    private readonly ICharacterProvider _provider;
    private readonly ClassIconService _classIcons;
    private readonly AgentInputDispatcher _input;
    private readonly MacroLibrary _macros;
    private readonly MacroRunner _macroRunner;
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
    private readonly string _masterTag;
    private readonly HashSet<string> _ignoredTags;
    private readonly string _placeholderName;
    private readonly ILogger<CharacterAgent> _logger;
    // Guards the promote-once contract of Identify(): without it, a manual UI assign
    // racing the auto-identify path could double-tag and double-notify.
    private readonly Lock _identifyLock = new();
    // Single-flight guard shared by EnterWorldAsync and IdentifyAsync. WaitAsync(0)
    // returns false on contention so a stacked-up trigger no-ops instead of letting
    // two concurrent activate/click/key cycles race the same window.
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private CancellationTokenSource? _runCts;

    public CharacterAgent(
        IGameWindow window,
        string processName,
        WindowRegistry registry,
        ChannelWriter<AgentMessage> outbox,
        ICharacterProvider provider,
        ClassIconService classIcons,
        AgentInputDispatcher input,
        MacroLibrary macros,
        MacroRunner macroRunner,
        GameUiElementExample uiTemplates,
        IOptions<AgentOptions> options,
        ILogger<CharacterAgent> logger)
    {
        _window = window;
        _processName = processName;
        _registry = registry;
        _outbox = outbox;
        _provider = provider;
        _classIcons = classIcons;
        _input = input;
        _macros = macros;
        _macroRunner = macroRunner;
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
        _masterTag = options.Value.MasterTag;
        _ignoredTags = new HashSet<string>(options.Value.IgnoredTags, StringComparer.Ordinal);
        _logger = logger;
        _inbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        // Born tagless — placeholder name unique per-hwnd so it doesn't clash with
        // identified windows until the first tag lands.
        _placeholderName = $"Unknown (hwnd=0x{window.Handle.ToInt64():X})";
    }

    /// <summary>Display name — the window's first tag, or a per-hwnd placeholder until tagged.</summary>
    public string Name => FirstTagOrNull() ?? _placeholderName;

    /// <summary>
    /// Coarse state string for UI binding, derived from <see cref="IsIdentified"/>.
    /// Transitional — W0.3 rebuilds the UI around windows + tag chips.
    /// </summary>
    public string State => IsIdentified ? "Idle" : "AwaitingIdentification";

    /// <summary>Identified = the registry holds at least one tag for this window.</summary>
    public bool IsIdentified => _registry.GetTags(Handle).Count > 0;

    // Master = the window carrying AgentOptions.MasterTag (typically Лучник).
    // The master is the one being /assist'd by everyone else, so it skips
    // TakeAssistMessage itself.
    public bool IsMaster => _registry.HasTag(Handle, _masterTag);

    // Ignored = the window carries any tag from AgentOptions.IgnoredTags. Used for
    // utility characters like a warehouse mule — they boot + identify normally but
    // drop all broadcasts.
    public bool IsIgnored => _registry.GetTags(Handle).Overlaps(_ignoredTags);
    public ChannelWriter<AgentMessage> Inbox => _inbox.Writer;
    public Task? RunningTask { get; private set; }

    // Underlying game-window handle. Exposed so the orchestrator can recognise when the
    // current foreground / window-under-cursor belongs to one of its tracked agents
    // (used by the BroadcastClick guard to suppress clicks when the user is outside the
    // game).
    public IntPtr Handle => _window.Handle;

    /// <summary>
    /// Active capture of the current game window. Exposed for diagnostics (dump-captures
    /// debug flow).
    /// </summary>
    public byte[] CaptureScreenshot() => _window.CaptureScreenshot();

    /// <summary>
    /// Registers the window in <see cref="WindowRegistry"/>, kicks off the main run loop
    /// on the thread pool, and fires the boot pipeline.
    /// Idempotency: throws if the agent is already running.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when called on an already-running agent.</exception>
    public void Start()
    {
        if (RunningTask is not null)
        {
            throw new InvalidOperationException($"Agent '{Name}' is already started.");
        }

        _registry.Register(Handle, _processName);

        _runCts = new CancellationTokenSource();
        RunningTask = Task.Run(() => RunLoopAsync(_runCts.Token));

        // Auto-kick the boot pipeline (vision-driven phase polls → click → identify).
        // Fire-and-forget; on success it calls Identify() which applies the tag via the
        // registry. The captured _runCts.Token cancels mid-flow if Stop() runs.
        _ = EnterWorldAsync(_runCts.Token);
    }

    /// <summary>
    /// Signals the run loop to stop. The loop exits, unregisters the window from
    /// <see cref="WindowRegistry"/>, writes <see cref="AgentStoppingMessage"/>, and the
    /// task completes via <see cref="RunningTask"/>.
    /// </summary>
    public void Stop()
    {
        _runCts?.Cancel();
    }

    /// <summary>
    /// One-shot promotion from tagless to identified. Called by the identification flow
    /// after a successful auto-match or by the UI after the user assigns a tag manually.
    /// Applies the tag via <see cref="WindowRegistry.AddTag"/>, applies the taskbar icon,
    /// and notifies the orchestrator.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the window already carries a tag.</exception>
    public void Identify(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        string oldName;
        lock (_identifyLock)
        {
            if (IsIdentified)
            {
                throw new InvalidOperationException($"Agent '{Name}' is already identified.");
            }

            oldName = Name;
            _registry.AddTag(Handle, tag);
        }

        LogPromoted(oldName, tag);
        _classIcons.TryApply(_window, tag);
        _outbox.TryWrite(new AgentIdentifiedMessage(this, oldName));
    }

    // Main run loop — responsible only for window-death detection and inbox draining.
    // The main loop wakes on inbox messages or window-aliveness ticks.
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        LogStarted(Name, State, _pollInterval);
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
            LogStopped(Name);
            // Window is gone (or we're shutting down) — the registry entry and its tags
            // die with it. Raises WindowClosed for registry subscribers.
            _registry.Unregister(Handle);
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

    // Single identify pass — opens stats, captures, matches the tag, closes stats. Does
    // NOT advance through boot screens; assumes the agent is already in-world. On a
    // match, promotes via Identify() (applies the tag through the registry); on miss,
    // stays tagless. Triggered by EnterIdentifyMessage (BroadcastIdentify hotkey).
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

        string? tag;
        try
        {
            tag = _provider.Identify(screenshot);
        }
        catch (Exception ex)
        {
            LogIdentifyFailed(ex);
            return;
        }

        if (tag is null)
        {
            LogIdentifyNoMatch(Name);
            return;
        }

        Identify(tag);
    }

    // Wraps activation around a single click at the given client point. Failures are
    // logged (via AgentInputDispatcher) and swallowed — a click that errors out doesn't
    // abort the boot flow, since the boot sequence has best-effort semantics anyway.
    private Task ClickBootButtonAsync(Native.ScreenPoint point, string label)
    {
        LogBootClick(Name, label, point);
        return _input.FireClickAsync(_window, point, doubleClick: false, Name);
    }

    // Fire-and-forget macro run on RunMacroMessage. Lookup by name in MacroLibrary,
    // then pick the action list whose tag key is present in this window's tag set.
    // Single-flight via _operationLock — if an identify or a previous macro is still
    // running, this one no-ops.
    private async Task RunMacroAsync(string macroName, CancellationToken cancellationToken)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            LogOperationAlreadyRunning(Name, $"RunMacro({macroName})");
            return;
        }

        try
        {
            var macro = _macros.TryGet(macroName);
            if (macro is null)
            {
                LogMacroNotFound(Name, macroName);
                return;
            }

            // Per-tag lookup: each tag can have its own action sequence (different cast
            // times / rotations). First dictionary key contained in the window's tag set
            // wins; multiple matches are logged. No matching key → silent no-op.
            var tags = _registry.GetTags(Handle);
            List<MacroAction>? actions = null;
            string? matchedTag = null;
            foreach (var (tag, tagActions) in macro.ActionsByTag)
            {
                if (!tags.Contains(tag))
                {
                    continue;
                }
                if (matchedTag is null)
                {
                    matchedTag = tag;
                    actions = tagActions;
                }
                else
                {
                    LogMacroMultipleTagMatches(Name, macroName, matchedTag, tag);
                }
            }

            if (actions is null || actions.Count == 0)
            {
                LogMacroNoActionsForTags(Name, macroName, string.Join(", ", tags));
                return;
            }

            LogMacroStarted(Name, macroName);
            await _macroRunner.RunAsync(macroName, actions, _window, Name, cancellationToken).ConfigureAwait(false);
            LogMacroFinished(Name, macroName);
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop() mid-run; nothing to log.
        }
        catch (Exception ex)
        {
            LogMacroFailed(ex, macroName);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void DrainInbox()
    {
        while (_inbox.Reader.TryRead(out var message))
        {
            // EnterIdentifyMessage is the one inbox message handled BEFORE the
            // unidentified-guard: it's specifically meant for tagless agents to
            // promote themselves. Already-identified agents drop it silently — no stat-
            // window flash, no log noise. Broadcast semantics stay simple (fan-out to
            // all agents); the filter lives here.
            if (message is EnterIdentifyMessage)
            {
                if (IsIdentified)
                {
                    continue;
                }
                LogInboxReceived(message.GetType().Name, Name);
                // Routes through identification-only (no boot clicks). Assumes the agent
                // is already in-world (boot ran on Start). The single-flight semaphore
                // means a hotkey press during an in-progress boot is a no-op — operator
                // can re-press once boot finishes.
                var ct = _runCts?.Token ?? CancellationToken.None;
                _ = IdentifyAsync(ct);
                continue;
            }

            // Until the window is tagged, swallow all other broadcast actions silently.
            // We genuinely don't know whose window this is yet — sending immunity / clicks
            // could land in the wrong place (e.g. character-select screen, chat input,
            // the launcher).
            if (!IsIdentified)
            {
                continue;
            }

            // Ignored tag (warehouse / utility character) — drop the broadcast.
            // Boot + identify still happen so the agent makes it in-world, but it
            // doesn't act on party-wide commands.
            if (IsIgnored)
            {
                LogBroadcastIgnored(message.GetType().Name, Name, string.Join(", ", _registry.GetTags(Handle)));
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

                case RunMacroMessage runMacro:
                    // Fire-and-forget — RunMacroAsync handles single-flight via
                    // _operationLock so spamming the broadcast just no-ops.
                    var macroCt = _runCts?.Token ?? CancellationToken.None;
                    _ = RunMacroAsync(runMacro.MacroName, macroCt);
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

    private string? FirstTagOrNull()
    {
        foreach (var tag in _registry.GetTags(Handle))
        {
            return tag;
        }
        return null;
    }
}
