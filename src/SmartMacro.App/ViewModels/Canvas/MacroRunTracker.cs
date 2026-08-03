using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// One walk as the run picker shows it: the chip in the log strip's header that the mockup
/// draws as <c>прогон 0x140804 ◂ ▸</c>.
///
/// The label is the CONTEXT WINDOW, not the run id, because that is the thing that
/// distinguishes the entries the picker exists for: ten windows booting through one graph
/// are ten walks with one run id and ten handles. A walk with no context window (the root
/// walk of a hotkey run, which routes purely by selector) is labelled by its macro instead.
/// </summary>
public sealed class MacroRunViewModel : ObservableObject
{
    private bool _isFinished;
    private string? _finalOutcome;
    private bool _isPaused;
    private bool _pauseRequested;
    private bool _pausedAtBreakpoint;
    private string? _pauseReason;
    private int _elapsedMs;

    internal MacroRunViewModel(RunWalkDto walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        Walk = walk;
        Label = walk.Hwnd != 0
            ? string.Create(CultureInfo.InvariantCulture, $"0x{walk.Hwnd:X}")
            : walk.MacroName;
    }

    /// <summary>The walk this row follows.</summary>
    public RunWalkDto Walk { get; }

    /// <summary>Identity — what every event correlates on.</summary>
    public Guid WalkId => Walk.WalkId;

    /// <summary>Graph being walked.</summary>
    public string MacroName => Walk.MacroName;

    /// <summary>Chip text: the window handle, or the macro name when the walk has no window.</summary>
    public string Label { get; }

    /// <summary>Rows of the log strip for this walk, oldest first.</summary>
    public ObservableCollection<RunLogRowViewModel> Log { get; } = [];

    /// <summary>
    /// <c>false</c> when the panel started listening after this walk began, so the head of
    /// <see cref="Log"/> is missing. Surfaced in the strip — never silently.
    /// </summary>
    public bool FromStart => Walk.FromStart;

    /// <summary>Node the walker is standing on, or <c>null</c> once the walk has ended.</summary>
    public string? CurrentNodeId { get; private set; }

    /// <summary>The walk has ended. It stays in the picker so its log can still be read.</summary>
    public bool IsFinished
    {
        get => _isFinished;
        private set => SetField(ref _isFinished, value);
    }

    /// <summary><c>true</c> while the walk is still going — drives the live dot on the chip.</summary>
    public bool IsLive => !_isFinished;

    /// <summary>How it ended, in Russian, or <c>null</c> while it is still running.</summary>
    public string? FinalOutcome
    {
        get => _finalOutcome;
        private set => SetField(ref _finalOutcome, value);
    }

    /// <summary>Rows kept per walk. A stuck loop must not grow the panel without bound.</summary>
    public const int MaxRows = 500;

    // ---- debugger state (D5) --------------------------------------------------------

    /// <summary>
    /// Nodes this walk has finished, with how long each took and which way it went. Drives
    /// the mockup's dimmed-with-a-tick boxes.
    ///
    /// A dictionary rather than a scan of <see cref="Log"/>, because the log is trimmed at
    /// <see cref="MaxRows"/> and a long walk would start un-ticking its own earliest boxes.
    /// A cycle re-records the node, so the stamp is always the LATEST pass — which is what
    /// someone watching a loop wants to read.
    /// </summary>
    public Dictionary<string, (string Time, string Outcome)> Passed { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Live value of each run variable, by name — the right-hand column of the variables
    /// panel. Per walk, because a fan-out gives every window its own <c>{tag}</c>.
    /// </summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>How many nodes this walk has ENTERED — the left half of the mockup's «3 / 11».</summary>
    public int NodesEntered { get; private set; }

    /// <summary>Parked right now, waiting for a debugger command.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set => SetField(ref _isPaused, value);
    }

    /// <summary>
    /// ⏸ was pressed but the walk is still inside a node. The honest in-between state: a
    /// pause is a REQUEST, and a <c>WaitForElement</c> can hold it off for a minute.
    /// </summary>
    public bool PauseRequested
    {
        get => _pauseRequested;
        internal set => SetField(ref _pauseRequested, value);
    }

    /// <summary>Parked by a breakpoint rather than by a step or a button — the mockup's red pill.</summary>
    public bool PausedAtBreakpoint
    {
        get => _pausedAtBreakpoint;
        private set => SetField(ref _pausedAtBreakpoint, value);
    }

    /// <summary>Why it is parked, in the daemon's Russian: «брейкпоинт», «шаг», «до курсора», «пауза».</summary>
    public string? PauseReason
    {
        get => _pauseReason;
        private set => SetField(ref _pauseReason, value);
    }

    /// <summary>
    /// Milliseconds since the walk began, as of the last event. The toolbar's «0:12.4»
    /// extrapolates from this while the walk is live — see <c>MacroEditorViewModel.TickElapsed</c>.
    /// </summary>
    public int ElapsedMs => _elapsedMs;

    /// <summary>When the last event arrived, so a live clock can add the time since.</summary>
    public DateTimeOffset ElapsedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    internal void Paused(RunEventDto evt)
    {
        CurrentNodeId = evt.NodeId;
        IsPaused = true;
        PauseRequested = false;
        PausedAtBreakpoint = evt.Kind == RunEventKind.BreakpointHit;
        PauseReason = evt.Detail;
        Touch(evt);
    }

    internal void Resumed(RunEventDto evt)
    {
        IsPaused = false;
        PausedAtBreakpoint = false;
        PauseReason = null;
        Touch(evt);
    }

    internal void VariableSet(RunEventDto evt)
    {
        if (evt.Variable is { Length: > 0 } name)
        {
            Variables[name] = evt.Detail ?? string.Empty;
        }
        Touch(evt);
    }

    internal void NodeEntered(RunEventDto evt)
    {
        // A row for a node that never reported its exit (the walk was cancelled inside it)
        // stays incomplete rather than being back-filled with a guess.
        CurrentRow()?.Settle();
        Log.Add(new RunLogRowViewModel(evt.ElapsedMs, evt.NodeId ?? string.Empty));
        CurrentNodeId = evt.NodeId;
        NodesEntered++;
        Touch(evt);
        Trim();
    }

    internal void NodeExited(RunEventDto evt)
    {
        if (evt.NodeId is { Length: > 0 } nodeId)
        {
            Passed[nodeId] = (
                RunLogRowViewModel.FormatDuration(evt.DurationMs),
                RunLogRowViewModel.DescribeOutcome(evt.Outcome));
        }
        Touch(evt);
        // Matched by node id from the tail: the walk is sequential, so the open row for this
        // node is the last one — unless the pair was split by a dropped batch, in which case
        // there is nothing to complete and the entered-row simply stays pending.
        for (var i = Log.Count - 1; i >= 0; i--)
        {
            var row = Log[i];
            if (row.IsCurrent && string.Equals(row.NodeId, evt.NodeId, StringComparison.Ordinal))
            {
                row.Complete(evt.Outcome, evt.Detail, evt.DurationMs);
                return;
            }
        }
    }

    internal void Finished(RunEventDto evt)
    {
        CurrentRow()?.Settle();
        CurrentNodeId = null;
        IsFinished = true;
        FinalOutcome = RunLogRowViewModel.DescribeOutcome(evt.Outcome);
        // A finished walk cannot be paused, and leaving the flag set would light a Resume
        // button for something that has already ended.
        IsPaused = false;
        PauseRequested = false;
        PausedAtBreakpoint = false;
        PauseReason = null;
        Touch(evt);
        OnPropertyChanged(nameof(IsLive));
    }

    private void Touch(RunEventDto evt)
    {
        _elapsedMs = Math.Max(_elapsedMs, evt.ElapsedMs);
        ElapsedAtUtc = DateTimeOffset.UtcNow;
    }

    private RunLogRowViewModel? CurrentRow() =>
        Log.Count > 0 && Log[^1].IsCurrent ? Log[^1] : null;

    private void Trim()
    {
        while (Log.Count > MaxRows)
        {
            Log.RemoveAt(0);
        }
    }
}

/// <summary>
/// Every walk the panel has heard about, and the rule for which one the canvas follows.
///
/// <b>Why one walk and not all of them.</b> A graph can be walked by ten windows at once;
/// lighting a node per walk lights nine boxes the user did not ask about. The mockup answers
/// this with a run chip in the debugger toolbar — one selected run — and this class is that
/// selection. <c>ExecutingNodeId</c> is whatever the SELECTED walk is standing on, and the
/// log strip shows that walk's rows and no one else's.
///
/// <b>The selection rules, all of them driven by "follow the thing that is alive":</b>
///
///   * nothing selected and a walk of the open macro starts ⇒ select it;
///   * the selected walk is FINISHED and a new one starts ⇒ switch to it, because the user
///     is watching a log that has stopped moving;
///   * the selected walk is LIVE and another starts ⇒ leave it alone. Stealing the selection
///     mid-run is the one thing that would make the picker useless during a fan-out;
///   * the user opens a different macro ⇒ re-derive the list for THAT macro. Walks of other
///     macros stay tracked (bounded), so switching back restores the log instead of
///     discarding it;
///   * the daemon connection drops ⇒ everything is discarded. Whatever happened during the
///     gap is unknowable, and a log with an invisible hole is worse than an empty one.
/// </summary>
public sealed class MacroRunTracker
{
    /// <summary>
    /// Walks retained across all macros. Ten windows plus their parent is eleven; three
    /// runs' worth of history is plenty to switch back to, and the cap stops a long session
    /// from accumulating them forever.
    /// </summary>
    internal const int MaxTrackedWalks = 40;

    private readonly Dictionary<Guid, MacroRunViewModel> _byId = [];
    private readonly List<MacroRunViewModel> _order = [];

    /// <summary>Every tracked walk, oldest first.</summary>
    public IReadOnlyList<MacroRunViewModel> All => _order;

    /// <summary>Forgets everything. The reconnect path — see the class comment.</summary>
    public void Clear()
    {
        _byId.Clear();
        _order.Clear();
    }

    /// <summary>Seeds a walk that was already in flight when we subscribed.</summary>
    public MacroRunViewModel Add(RunWalkDto walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        if (_byId.TryGetValue(walk.WalkId, out var existing))
        {
            return existing;
        }

        var run = new MacroRunViewModel(walk);
        _byId[walk.WalkId] = run;
        _order.Add(run);
        Evict();
        return run;
    }

    /// <summary>Applies one event. Returns the walk it touched, or <c>null</c> for an unknown one.</summary>
    /// <remarks>
    /// An event for a walk we never saw start is DROPPED rather than synthesised. It can only
    /// happen when the <c>WalkStarted</c> was in a batch the daemon had to discard, and a
    /// walk invented from a node event would have no macro name — so it could not be filed
    /// under a graph, which is the one thing the picker needs.
    /// </remarks>
    public MacroRunViewModel? Apply(RunEventDto evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt.Kind == RunEventKind.WalkStarted)
        {
            return evt.Walk is { } walk ? Add(walk) : null;
        }

        if (!_byId.TryGetValue(evt.WalkId, out var run))
        {
            return null;
        }

        switch (evt.Kind)
        {
            case RunEventKind.NodeEntered:
                run.NodeEntered(evt);
                break;
            case RunEventKind.NodeExited:
                run.NodeExited(evt);
                break;
            case RunEventKind.WalkFinished:
                run.Finished(evt);
                break;
            case RunEventKind.Paused:
            case RunEventKind.BreakpointHit:
                run.Paused(evt);
                break;
            case RunEventKind.Resumed:
                run.Resumed(evt);
                break;
            case RunEventKind.VariableSet:
                run.VariableSet(evt);
                break;
            default:
                // A kind this panel predates. Ignoring it keeps the log honest rather than
                // mislabelling it — the property D5 inherited and has to keep.
                break;
        }
        return run;
    }

    /// <summary>Walks of one macro, oldest first — what the picker offers while that graph is open.</summary>
    public IReadOnlyList<MacroRunViewModel> For(string? macroName) => macroName is null
        ? []
        : [.. _order.Where(run => string.Equals(run.MacroName, macroName, StringComparison.Ordinal))];

    // Finished walks go first and oldest-first, so a live fan-out is never evicted out from
    // under the user by its own siblings.
    private void Evict()
    {
        while (_order.Count > MaxTrackedWalks)
        {
            var victim = _order.FirstOrDefault(run => run.IsFinished) ?? _order[0];
            _order.Remove(victim);
            _byId.Remove(victim.WalkId);
        }
    }
}
