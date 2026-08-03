namespace SmartMacro.Macros.Execution;

/// <summary>Why a walk is parked. Drives which event the panel gets and how it renders it.</summary>
public enum DebugPauseReason
{
    /// <summary>The user pressed ⏸ while the walk was inside a node.</summary>
    Requested,

    /// <summary>One ⤼ Шаг worth of progress has been made.</summary>
    Step,

    /// <summary>▷| До курсора reached the node it was aimed at.</summary>
    Cursor,

    /// <summary>The node carries a breakpoint.</summary>
    Breakpoint,
}

/// <summary>
/// A walk parked at a node, handed back by <see cref="IMacroDebugger.Arm"/>.
///
/// Two calls rather than one <c>PauseIfNeededAsync</c> because the walker has to ANNOUNCE the
/// pause between deciding on it and waiting for it — otherwise the panel learns a walk is
/// parked only from the absence of further events, which is indistinguishable from a slow
/// node. Arming and waiting are still atomic with respect to a racing Resume: the gate exists
/// (and can therefore be released) from the moment <see cref="IMacroDebugger.Arm"/> returns.
/// </summary>
public sealed class MacroDebugGate
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal MacroDebugGate(Guid walkId, string nodeId, DebugPauseReason reason)
    {
        WalkId = walkId;
        NodeId = nodeId;
        Reason = reason;
    }

    /// <summary>Walk being held.</summary>
    public Guid WalkId { get; }

    /// <summary>Node the walk is parked BEFORE. It has not run yet.</summary>
    public string NodeId { get; }

    /// <summary>Why.</summary>
    public DebugPauseReason Reason { get; }

    /// <summary>
    /// Completes when the walk is released. Honours <paramref name="cancellationToken"/>, so
    /// ■ Стоп (and daemon shutdown, which cancels every run) unparks a paused walk instead of
    /// leaving it wedged — the resulting <see cref="OperationCanceledException"/> is the
    /// normal cancellation path and ends the walk as <c>Cancelled</c>.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _released.Task.WaitAsync(cancellationToken);

    internal void Release() => _released.TrySetResult();
}

/// <summary>
/// The walker's control channel, the sibling of <see cref="IMacroRunObserver"/>'s reporting
/// channel: that one says what happened, this one decides whether the walk may continue.
///
/// <b><see cref="IsActive"/> follows the same discipline as <c>IsEnabled</c>.</b> The walker
/// reads it once per node before touching anything else, and a daemon with no panel attached
/// therefore pays one volatile read. It must be honest: a constant <c>true</c> would put a
/// dictionary lookup and a lock on the path of something driving a live game.
///
/// <b>Called from engine threads, several at once</b> — a fan-out walks N graphs in parallel
/// and each has its own gate. Implementations must be thread-safe.
/// </summary>
public interface IMacroDebugger
{
    /// <summary>Whether any debugger is attached. Checked per node; must be cheap.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Decides whether the walk stops before <paramref name="nodeId"/>, and if so, arms the
    /// gate it must wait on. <c>null</c> = carry on.
    /// </summary>
    /// <param name="walkId">The walk, as reported to <see cref="IMacroRunObserver.WalkStarted"/>.</param>
    /// <param name="macroName">Graph being walked — breakpoints are keyed by (macro, node).</param>
    /// <param name="nodeId">Node about to run.</param>
    MacroDebugGate? Arm(Guid walkId, string macroName, string nodeId);

    /// <summary>
    /// Forgets a gate, whether it was released normally or abandoned by cancellation. The
    /// walker calls this in a <c>finally</c>; without it a cancelled walk would leave the
    /// session believing it is still parked.
    /// </summary>
    void Disarm(MacroDebugGate gate);

    /// <summary>
    /// Drops every trace of a finished walk. Called once per walk from the executor's exit
    /// path, so a session that has seen ten thousand walks holds state for none of them.
    /// </summary>
    void WalkFinished(Guid walkId);
}
