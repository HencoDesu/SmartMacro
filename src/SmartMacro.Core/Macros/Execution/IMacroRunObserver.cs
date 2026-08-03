using System.Diagnostics;
using SmartMacro.Contracts.Dto;

namespace SmartMacro.Macros.Execution;

/// <summary>Identity of a walk, handed to <see cref="IMacroRunObserver.WalkStarted"/>.</summary>
/// <param name="WalkId">Fresh per <see cref="MacroExecutor.RunAsync"/> call — including every sub-macro fork.</param>
/// <param name="RunId">The tracked run this walk belongs to; <see cref="Guid.Empty"/> when the caller runs outside the registry (tests).</param>
/// <param name="MacroName">Graph being walked.</param>
/// <param name="ContextWindow">Window targetless nodes act on, or <c>null</c>.</param>
/// <param name="Depth">Sub-macro nesting level of this walk.</param>
public readonly record struct MacroWalkStart(
    Guid WalkId,
    Guid RunId,
    string MacroName,
    IntPtr? ContextWindow,
    int Depth);

/// <summary>
/// The executor's progress channel — the seam wave D3b hung the panel's live canvas off.
///
/// <b><see cref="IsEnabled"/> is not a nicety, it is the flooding fix.</b> The daemon is
/// resident and the panel is on-demand, so the overwhelming majority of runs happen with
/// nobody watching. The walker checks this flag before it times a node, formats a detail
/// string or allocates anything at all; when it is <c>false</c> the entire instrumentation
/// costs one volatile read per node. An implementation MUST make it cheap and MUST make it
/// honest — returning a constant <c>true</c> would put string formatting on the hot path of
/// something driving a live game.
///
/// <see cref="WalkStarted"/> and <see cref="WalkFinished"/> are the exception: they fire
/// regardless of <see cref="IsEnabled"/>, because the publisher's roster of live walks is
/// what lets a panel connecting mid-run learn that a run is in flight at all. They are two
/// calls per macro run, not two per node.
///
/// <b>Called from engine threads, possibly many at once</b> (a <c>RunMacroNode</c> fan-out
/// walks N graphs in parallel). Implementations must be thread-safe and must never block:
/// the caller is between two game inputs.
/// </summary>
public interface IMacroRunObserver
{
    /// <summary>Whether anything is listening. Checked per node; must be cheap.</summary>
    bool IsEnabled { get; }

    /// <summary>A walk began. Always called, even when <see cref="IsEnabled"/> is <c>false</c>.</summary>
    void WalkStarted(MacroWalkStart walk);

    /// <summary>The walker entered a node. Only called while <see cref="IsEnabled"/>.</summary>
    void NodeEntered(Guid walkId, int elapsedMs, string nodeId);

    /// <summary>The node finished. Only called while <see cref="IsEnabled"/>.</summary>
    /// <param name="outcome">One of <see cref="RunOutcomes"/>.</param>
    /// <param name="detail">Free-form specifics for the log strip, or <c>null</c>.</param>
    /// <param name="durationMs">Wall time inside the node, including awaited sub-macros.</param>
    void NodeExited(Guid walkId, int elapsedMs, string nodeId, string outcome, string? detail, int durationMs);

    /// <summary>The walk ended. Always called, even when <see cref="IsEnabled"/> is <c>false</c>.</summary>
    /// <param name="outcome"><see cref="RunOutcomes.Completed"/>, <see cref="RunOutcomes.Aborted"/> or <see cref="RunOutcomes.Cancelled"/>.</param>
    /// <param name="detail">Abort reason, or <c>null</c>.</param>
    void WalkFinished(Guid walkId, int elapsedMs, string outcome, string? detail);
}

/// <summary>
/// One walk's tracing state: its id, its start timestamp and the observer to report to.
///
/// A struct passed down the walker rather than fields on <see cref="MacroExecutor"/> —
/// the executor is a singleton walking many graphs at once, so per-walk state cannot live
/// on it. Every method is a no-op when there is no observer, which is what keeps the call
/// sites in the walker free of null checks.
/// </summary>
internal readonly struct MacroWalkTrace
{
    private readonly IMacroRunObserver? _observer;
    private readonly long _startTimestamp;

    private MacroWalkTrace(IMacroRunObserver? observer, Guid walkId, long startTimestamp)
    {
        _observer = observer;
        WalkId = walkId;
        _startTimestamp = startTimestamp;
    }

    /// <summary>Identity of this walk.</summary>
    public Guid WalkId { get; }

    /// <summary>
    /// Whether per-NODE events are wanted. False both when there is no observer and when
    /// nobody is subscribed — the walker branches on this before formatting anything.
    /// </summary>
    public bool IsTracing => _observer is { IsEnabled: true };

    /// <summary>Raw timestamp for measuring one node; feed it back to <see cref="NodeExited"/>.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Opens a walk and announces it. Always allocates an id — the roster needs one even untraced.</summary>
    public static MacroWalkTrace Begin(IMacroRunObserver? observer, Guid runId, string macroName, IntPtr? contextWindow, int depth)
    {
        var trace = new MacroWalkTrace(observer, Guid.NewGuid(), Stopwatch.GetTimestamp());
        observer?.WalkStarted(new MacroWalkStart(trace.WalkId, runId, macroName, contextWindow, depth));
        return trace;
    }

    /// <summary>Milliseconds since the walk began.</summary>
    public int ElapsedMs => ToMs(Stopwatch.GetTimestamp() - _startTimestamp);

    public void NodeEntered(string nodeId)
    {
        if (_observer is { IsEnabled: true } observer)
        {
            observer.NodeEntered(WalkId, ElapsedMs, nodeId);
        }
    }

    public void NodeExited(string nodeId, string outcome, string? detail, long nodeStartTimestamp)
    {
        if (_observer is { IsEnabled: true } observer)
        {
            observer.NodeExited(WalkId, ElapsedMs, nodeId, outcome, detail, ToMs(Stopwatch.GetTimestamp() - nodeStartTimestamp));
        }
    }

    public void Finished(string outcome, string? detail) =>
        _observer?.WalkFinished(WalkId, ElapsedMs, outcome, detail);

    private static int ToMs(long ticks) => (int)(ticks * 1000 / Stopwatch.Frequency);
}
