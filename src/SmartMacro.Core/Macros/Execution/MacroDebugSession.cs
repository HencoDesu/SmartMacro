using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// The debugger's state: which nodes are breakpoints, which walks are parked, and how many
/// panels are attached. One instance per daemon.
///
/// ────────────────────────────────────────────────────────────────────────────────────────
/// <b>Where breakpoints live, and why it is here rather than in the macro file.</b>
///
/// The plan asked "in the graph, or in the session?" and left it open. They live HERE, in the
/// daemon's memory, for the whole life of the daemon process and no longer.
///
///   · A breakpoint is a fact about a debugging session, not about a macro. Persisting one
///     into <c>macros/*.json</c> would put it in the artefact the user edits, diffs and — the
///     examples especially — ships to someone else. «Почему у меня макрос встаёт на третьей
///     ноде» is not a question anyone should have to answer.
///   · Saving one would also mean a breakpoint DIRTIES the editor, so toggling a red dot on a
///     clean graph would demand a save, and forgetting to save would silently drop it. Both
///     are worse than losing it on daemon restart.
///   · The ergonomic win people actually want from persistence — "my breakpoints are still
///     there when I reopen the panel" — costs nothing here, because the daemon outlives the
///     panel by design. That is the whole point of the split.
///
/// What is therefore given up: breakpoints do not survive «Выход» from the tray. That is the
/// right trade — a daemon restart is also when every macro run, every tag and every hotkey
/// registration is rebuilt, so nothing about the session survives it anyway.
/// ────────────────────────────────────────────────────────────────────────────────────────
///
/// <b>A parked walk must never outlive its audience.</b> This is the failure mode the whole
/// attach count exists for. A walk waiting inside <see cref="MacroDebugGate.WaitAsync"/> holds
/// its run's single-flight slot in <see cref="MacroRunRegistry"/>, so that macro's hotkey is
/// dead until it moves. The daemon is resident and drives a live game — "until the user
/// restarts it" is not an acceptable answer. So:
///
///   · Every attached debugger is a run-event subscriber (<c>SubscribeRunEvents</c>), which
///     the server already releases on disconnect, crash included.
///   · When the LAST one detaches, <see cref="Release"/> unparks every walk and forgets every
///     pause request. Breakpoints stay stored but stop biting, because a breakpoint that
///     halted a walk nobody could resume would recreate the same wedge.
///   · Resuming, rather than aborting, is the conservative choice: the run was started
///     legitimately (usually by a hotkey) and abandoning a macro halfway through can leave
///     the game in a worse state than letting it finish.
///
/// <b>No inactivity timeout, deliberately.</b> The remaining case a timeout would cover is
/// "the panel is up and the user walked away", and there the pause is doing exactly its job —
/// resuming a live game automation under a user who is reading the screen is worse than the
/// thing it would be protecting against. Cancellation (■ Стоп, shutdown) already unparks.
/// </summary>
public sealed partial class MacroDebugSession : IMacroDebugger
{
    private readonly Lock _lock = new();

    // macro name → node ids. Ordinal throughout: node ids and macro names are both
    // case-sensitive everywhere else in the system.
    private readonly Dictionary<string, HashSet<string>> _breakpoints = new(StringComparer.Ordinal);

    private readonly Dictionary<Guid, WalkState> _walks = [];

    /// <summary>
    /// Walks the session has seen at least one node of, so a Pause aimed at a walk that has
    /// already finished can be refused rather than creating state nothing will ever consume.
    /// </summary>
    private readonly HashSet<Guid> _live = [];

    private readonly ILogger<MacroDebugSession> _logger;

    private int _attached;

    public MacroDebugSession(ILogger<MacroDebugSession> logger) => _logger = logger;

    /// <inheritdoc />
    /// <remarks>
    /// True as soon as one panel is attached, whether or not any breakpoint exists — a Pause
    /// can be requested at any moment, so the gate has to be reachable. The cost when it is
    /// true and nothing is armed is one lock and two dictionary lookups per node, against a
    /// walker whose cheapest node is a Win32 round trip.
    /// </remarks>
    public bool IsActive => Volatile.Read(ref _attached) > 0;

    /// <summary>How many panels are attached. Diagnostics and tests.</summary>
    public int AttachedCount => Volatile.Read(ref _attached);

    // ------------------------------------------------------------------- attach/detach

    /// <summary>
    /// One more attached debugger. Paired with <see cref="Release"/> by <c>IpcServer</c> on
    /// the same edge as the run-event subscription, disconnect path included.
    /// </summary>
    public void Acquire()
    {
        if (Interlocked.Increment(ref _attached) == 1)
        {
            LogAttached();
        }
    }

    /// <summary>
    /// One fewer. The last one out unparks everything — see the class comment; this is the
    /// answer to "the panel died with a walk paused".
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _attached) > 0)
        {
            return;
        }

        List<MacroDebugGate> stranded;
        lock (_lock)
        {
            stranded = [.. _walks.Values.Select(state => state.Gate).OfType<MacroDebugGate>()];
            _walks.Clear();
            // The roster too: whatever is still walking will re-register itself the moment a
            // debugger attaches again, and keeping dead ids would leak a Guid per run.
            _live.Clear();
        }

        foreach (var gate in stranded)
        {
            gate.Release();
        }
        if (stranded.Count > 0)
        {
            LogAutoResumed(stranded.Count);
        }
        LogDetached();
    }

    // ---------------------------------------------------------------------- breakpoints

    /// <summary>
    /// Replaces one macro's breakpoints. An empty list removes the macro from the map
    /// entirely, so <see cref="Breakpoints"/> never reports an empty set.
    /// </summary>
    public void SetBreakpoints(string macroName, IReadOnlyList<string> nodeIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        ArgumentNullException.ThrowIfNull(nodeIds);

        var wanted = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        lock (_lock)
        {
            if (wanted.Count == 0)
            {
                _breakpoints.Remove(macroName);
            }
            else
            {
                _breakpoints[macroName] = wanted;
            }
        }
        LogBreakpointsSet(macroName, wanted.Count);
    }

    /// <summary>Every macro that has breakpoints, ordered by name. The answer to <c>GetBreakpoints</c>.</summary>
    public IReadOnlyList<BreakpointSetDto> Breakpoints()
    {
        lock (_lock)
        {
            return
            [
                .. _breakpoints
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new BreakpointSetDto(
                        pair.Key,
                        [.. pair.Value.OrderBy(id => id, StringComparer.Ordinal)]))
            ];
        }
    }

    // ------------------------------------------------------------------------ commands

    /// <summary>
    /// Applies one panel command to one walk.
    /// </summary>
    /// <returns>
    /// The acknowledgement the protocol returns. <c>Accepted = false</c> means the walk is
    /// unknown — it finished, or it belongs to a daemon run that predates this session.
    /// </returns>
    public DebugAckDto Command(Guid walkId, DebugCommand command, string? nodeId)
    {
        MacroDebugGate? release = null;
        DebugAckDto ack;

        lock (_lock)
        {
            // Pause is the one command that may address a walk we have never gated: the walk
            // is running normally and we are asking it to stop at its next node. Every other
            // command acts on state that must already exist.
            if (!_walks.TryGetValue(walkId, out var state))
            {
                if (command != DebugCommand.Pause || !_live.Contains(walkId))
                {
                    return new DebugAckDto(Accepted: false, Paused: false, PauseRequested: false);
                }
                state = new WalkState();
                _walks[walkId] = state;
            }

            switch (command)
            {
                case DebugCommand.Pause:
                    // Already parked ⇒ nothing to ask for. Otherwise it is a REQUEST honoured
                    // at the next node boundary, which can be a 60-second WaitForElement away
                    // — the panel says «пауза…» until the Paused event confirms it.
                    if (state.Gate is null)
                    {
                        state.Pending = DebugPauseReason.Requested;
                        state.RunToNodeId = null;
                    }
                    break;

                case DebugCommand.Resume:
                    state.Pending = null;
                    state.RunToNodeId = null;
                    release = Take(state);
                    break;

                case DebugCommand.Step:
                    state.Pending = DebugPauseReason.Step;
                    state.RunToNodeId = null;
                    release = Take(state);
                    break;

                case DebugCommand.RunToNode:
                    // No node id would mean "run to nowhere", i.e. a plain Resume with a
                    // misleading name. Refuse instead.
                    if (string.IsNullOrWhiteSpace(nodeId))
                    {
                        return new DebugAckDto(Accepted: false, state.Gate is not null, state.Pending is not null);
                    }
                    state.Pending = null;
                    state.RunToNodeId = nodeId;
                    release = Take(state);
                    break;

                default:
                    return new DebugAckDto(Accepted: false, state.Gate is not null, state.Pending is not null);
            }

            ack = new DebugAckDto(Accepted: true, state.Gate is not null, state.Pending is not null || state.RunToNodeId is not null);
        }

        // Outside the lock: releasing runs the walker's continuation, which will call back
        // into Arm on the next node.
        release?.Release();
        LogCommand(command.ToString(), walkId);
        return ack;
    }

    // ------------------------------------------------------------------- IMacroDebugger

    /// <inheritdoc />
    public MacroDebugGate? Arm(Guid walkId, string macroName, string nodeId)
    {
        // Re-checked inside the lock is unnecessary: a detach that races this releases the
        // gate the moment it sees it, because Release() drains _walks.
        if (!IsActive)
        {
            return null;
        }

        MacroDebugGate gate;
        lock (_lock)
        {
            _live.Add(walkId);
            var state = _walks.TryGetValue(walkId, out var existing) ? existing : null;

            // Breakpoint first: an explicit red dot outranks a step that happened to land here,
            // and the panel renders it differently.
            DebugPauseReason? reason =
                HasBreakpoint(macroName, nodeId) ? DebugPauseReason.Breakpoint
                : state?.Pending is { } pending ? pending
                : state?.RunToNodeId is { } target && string.Equals(target, nodeId, StringComparison.Ordinal)
                    ? DebugPauseReason.Cursor
                    : null;

            if (reason is not { } pauseReason)
            {
                return null;
            }

            state ??= new WalkState();
            _walks[walkId] = state;
            // Consumed: a step is one node, a run-to-cursor is one arrival.
            state.Pending = null;
            state.RunToNodeId = null;
            gate = new MacroDebugGate(walkId, nodeId, pauseReason);
            state.Gate = gate;
        }

        LogPaused(walkId, nodeId, gate.Reason.ToString());
        return gate;
    }

    /// <inheritdoc />
    public void Disarm(MacroDebugGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        lock (_lock)
        {
            if (_walks.TryGetValue(gate.WalkId, out var state) && ReferenceEquals(state.Gate, gate))
            {
                state.Gate = null;
            }
        }
    }

    /// <inheritdoc />
    public void WalkFinished(Guid walkId)
    {
        lock (_lock)
        {
            _live.Remove(walkId);
            _walks.Remove(walkId);
        }
    }

    private bool HasBreakpoint(string macroName, string nodeId) =>
        _breakpoints.TryGetValue(macroName, out var nodes) && nodes.Contains(nodeId);

    private static MacroDebugGate? Take(WalkState state)
    {
        var gate = state.Gate;
        state.Gate = null;
        return gate;
    }

    /// <summary>Per-walk debugger state. Guarded by the session lock; never escapes it except as a gate.</summary>
    private sealed class WalkState
    {
        /// <summary>Reason to park at the NEXT node, whatever it is. Consumed on use.</summary>
        public DebugPauseReason? Pending { get; set; }

        /// <summary>Park when this node is reached. Consumed on arrival; never reaching it is legal.</summary>
        public string? RunToNodeId { get; set; }

        /// <summary>The gate the walk is currently held by, or <c>null</c> when it is running.</summary>
        public MacroDebugGate? Gate { get; set; }
    }

    [LoggerMessage(LogLevel.Debug, "Debugger attached")]
    partial void LogAttached();

    [LoggerMessage(LogLevel.Debug, "Debugger detached")]
    partial void LogDetached();

    [LoggerMessage(LogLevel.Warning, "Last debugger detached — auto-resumed {Count} paused walk(s) so they cannot hold their single-flight slots forever")]
    partial void LogAutoResumed(int count);

    [LoggerMessage(LogLevel.Debug, "Breakpoints for '{MacroName}': {Count}")]
    partial void LogBreakpointsSet(string macroName, int count);

    [LoggerMessage(LogLevel.Debug, "Debug command {Command} on walk {WalkId}")]
    partial void LogCommand(string command, Guid walkId);

    [LoggerMessage(LogLevel.Information, "Walk {WalkId} paused before node '{NodeId}' ({Reason})")]
    partial void LogPaused(Guid walkId, string nodeId, string reason);
}
