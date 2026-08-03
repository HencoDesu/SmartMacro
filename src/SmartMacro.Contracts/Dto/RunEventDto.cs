namespace SmartMacro.Contracts.Dto;

/// <summary>
/// What a <see cref="RunEventDto"/> reports. The set is deliberately open-ended: the
/// debugger of wave D5 (pause / step / run-to-node / breakpoints) adds members here rather
/// than new message types, so the subscription, the batching and the client's demultiplexer
/// all stay exactly as they are.
/// </summary>
public enum RunEventKind
{
    /// <summary>A walk began. <see cref="RunEventDto.Walk"/> describes it; this is the only kind that carries it.</summary>
    WalkStarted,

    /// <summary>The walker entered a node. No outcome yet — the node is running.</summary>
    NodeEntered,

    /// <summary>The node finished. Carries its outcome, its detail line and its duration.</summary>
    NodeExited,

    /// <summary>The walk ended. <see cref="RunEventDto.Outcome"/> says how; <see cref="RunEventDto.Detail"/> carries the error, if any.</summary>
    WalkFinished,
}

/// <summary>
/// The closed set of <see cref="RunEventDto.Outcome"/> values.
///
/// Symbolic, not Russian, unlike <see cref="RunEventDto.Detail"/>: an outcome drives the
/// panel's colour and its wording, so the daemon must not be the one deciding either. The
/// names mirror the graph model's own edge names (<c>Found</c> / <c>NotFound</c> /
/// <c>Matched</c> / <c>Timeout</c>) — the outcome IS which edge was taken.
/// </summary>
public static class RunOutcomes
{
    /// <summary>An action node did its work. The only outcome an action node can have.</summary>
    public const string Ok = "ok";

    /// <summary><c>FindElementNode</c> / <c>WaitForElementNode</c> matched.</summary>
    public const string Found = "found";

    /// <summary><c>FindElementNode</c> did not match (a single shot, so not a timeout).</summary>
    public const string NotFound = "notFound";

    /// <summary><c>WaitForElementNode</c> gave up.</summary>
    public const string Timeout = "timeout";

    /// <summary><c>RecognizeTagNode</c> recognised a tag.</summary>
    public const string Matched = "matched";

    /// <summary><c>RecognizeTagNode</c> recognised nothing.</summary>
    public const string NotMatched = "notMatched";

    /// <summary>The node threw: a bad variable, a missing context window, an unknown sub-macro.</summary>
    public const string Error = "error";

    /// <summary>Walk-level: the walker reached a null edge and stopped cleanly.</summary>
    public const string Completed = "completed";

    /// <summary>Walk-level: a node failed and took the walk down with it. <see cref="RunEventDto.Detail"/> has the reason.</summary>
    public const string Aborted = "aborted";

    /// <summary>Node- or walk-level: the run's token fired (Stop, or the daemon shutting down).</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One WALK — a single pass of <c>MacroExecutor</c> over one graph.
///
/// Not the same thing as a run. One <c>RunningMacroDto</c> can contain many walks: a
/// <c>RunMacroNode</c> with a tag selector forks one walk per matching window, all sharing
/// the parent's <see cref="RunId"/>. That is exactly the case the canvas has to disambiguate
/// — ten windows booting through <c>pw-identify-one</c> are ten walks of ONE graph, and
/// lighting a node for each of them would light nine boxes at once. The panel therefore
/// picks a walk and follows it; <see cref="Hwnd"/> is what the picker labels it with, which
/// is why the mockup's run chip reads <c>0x140804</c>.
/// </summary>
/// <param name="WalkId">Identity of this walk. Every event correlates on it.</param>
/// <param name="RunId">The tracked run this walk belongs to; matches <c>RunningMacroDto.RunId</c>. Shared by a parent and all its sub-walks.</param>
/// <param name="MacroName">Graph being walked. Sub-walks name the SUB-macro, not the parent.</param>
/// <param name="Hwnd">Context window, or <c>0</c> when there is none (a hotkey run's root walk routes by selector only).</param>
/// <param name="Depth">Sub-macro nesting: 0 for a trigger-initiated walk, +1 per <c>RunMacroNode</c> level.</param>
/// <param name="StartedUtc">When the walk began.</param>
/// <param name="FromStart">
/// <c>false</c> when the subscriber joined after the walk had already begun, so its node log
/// is missing an unknown number of leading rows. The panel must SAY so rather than render a
/// partial log as if it were complete. Always <c>false</c> for the walks returned by
/// <c>SubscribeRunEvents</c> and always <c>true</c> for a <see cref="RunEventKind.WalkStarted"/>.
/// </param>
public sealed record RunWalkDto(
    Guid WalkId,
    Guid RunId,
    string MacroName,
    long Hwnd,
    int Depth,
    DateTimeOffset StartedUtc,
    bool FromStart);

/// <summary>
/// One thing that happened inside a walk.
///
/// <b><see cref="Detail"/> is free-form and may be Russian; <see cref="Outcome"/> never
/// is.</b> A detail is data the daemon alone can produce (the point a click actually
/// resolved to, the template it matched, how many windows a selector hit) and the panel
/// renders it verbatim — duplicating that formatting on the UI side would mean shipping
/// node-type knowledge into a process that deliberately has none. An outcome, by contrast,
/// is a closed enumeration (<see cref="RunOutcomes"/>) precisely so the panel owns its
/// wording and its colour.
/// </summary>
/// <param name="WalkId">Walk this belongs to. See <see cref="RunWalkDto"/> for why this is not the run id.</param>
/// <param name="Kind">What happened.</param>
/// <param name="ElapsedMs">Milliseconds since the walk began — the log strip's left column.</param>
/// <param name="NodeId">Node involved; <c>null</c> for the walk-level kinds.</param>
/// <param name="Outcome">One of <see cref="RunOutcomes"/>; <c>null</c> for <see cref="RunEventKind.NodeEntered"/> and <see cref="RunEventKind.WalkStarted"/>.</param>
/// <param name="Detail">Human-readable specifics, or <c>null</c>.</param>
/// <param name="DurationMs">How long the node took. <c>0</c> for anything but <see cref="RunEventKind.NodeExited"/>.</param>
/// <param name="Walk">Set on <see cref="RunEventKind.WalkStarted"/> and nowhere else.</param>
public sealed record RunEventDto(
    Guid WalkId,
    RunEventKind Kind,
    int ElapsedMs,
    string? NodeId = null,
    string? Outcome = null,
    string? Detail = null,
    int DurationMs = 0,
    RunWalkDto? Walk = null);

/// <summary>
/// Payload of the <c>RunEvents</c> push: everything that happened since the last flush.
///
/// Batched rather than one event per envelope, because the burst is the whole problem. A
/// hotkey run fans out to ten windows, each walk crosses a dozen nodes, and every node is
/// two events — several hundred envelopes in a few hundred milliseconds, against a
/// per-connection queue of 256 that DROPS the client when it overflows. Coalescing turns
/// that into a handful of envelopes, which is the difference between a live log and a panel
/// that disconnects exactly when the user is watching it.
/// </summary>
/// <param name="Events">Events in the order the engine produced them.</param>
/// <param name="Dropped">
/// How many events were discarded before this batch because the daemon's own queue was
/// full. Non-zero means the log has a hole in it — the panel must say so. Reported rather
/// than hidden, and the engine is never blocked to prevent it: a macro drives a live game
/// and must not wait on a UI.
/// </param>
public sealed record RunEventBatch(IReadOnlyList<RunEventDto> Events, int Dropped);
