namespace SmartMacro.Contracts.Dto;

/// <summary>
/// What a <see cref="RunEventDto"/> reports. The set is deliberately open-ended: wave D5's
/// debugger (pause / step / run-to-node / breakpoints) added members here rather than new
/// message types, so the subscription, the batching and the client's demultiplexer all stayed
/// exactly as they were.
///
/// <b>An unknown kind must degrade, never break.</b> The panel's tracker ignores kinds it
/// does not recognise and its outcome renderer falls through to the raw symbol, so a panel
/// older than its daemon loses a feature instead of the log. Anything added here has to keep
/// that property.
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

    // ------------------------------------------------------------------- debugger (D5)

    /// <summary>
    /// The walk parked BEFORE <see cref="RunEventDto.NodeId"/> and is waiting to be released.
    /// <see cref="RunEventDto.Detail"/> says why in Russian («пауза», «шаг», «до курсора»).
    ///
    /// Parked between two nodes, never inside one: every game window an input or vision node
    /// wakes is re-frozen before that node returns, so a pause here cannot strand a woken
    /// client. See <c>MacroExecutor</c>'s gate.
    /// </summary>
    Paused,

    /// <summary>
    /// The same thing as <see cref="Paused"/>, but the reason was a breakpoint on
    /// <see cref="RunEventDto.NodeId"/>. A separate kind rather than a reason code because
    /// this is the one pause the panel renders differently — the red pill of mockup 1d.
    /// </summary>
    BreakpointHit,

    /// <summary>
    /// The walk was released and is about to run <see cref="RunEventDto.NodeId"/>.
    ///
    /// Not redundant with the next <see cref="NodeExited"/>: a released walk can sit inside a
    /// 60-second <c>WaitForElement</c>, and without this the toolbar would keep saying
    /// «на паузе» for a minute after the user pressed resume.
    /// </summary>
    Resumed,

    /// <summary>
    /// A run variable got a value. <see cref="RunEventDto.Variable"/> is its name and
    /// <see cref="RunEventDto.Detail"/> its display string; <see cref="RunEventDto.NodeId"/>
    /// is the node that wrote it, or <c>null</c> for the trigger's <c>cursor</c> seed, which
    /// is reported once at the head of every walk.
    ///
    /// <b>Why not read the value out of a <see cref="NodeExited"/> detail.</b> The detail of a
    /// <c>RecognizeTag</c> reads <c>"classes → Жрец"</c>: extracting the value would mean the
    /// panel parsing a free-form string whose shape is the daemon's business, and it would
    /// still never see <c>cursor</c>, which no node ever reports. The rate is not a concern —
    /// this is a handful of events per walk, not two per node.
    /// </summary>
    VariableSet,
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
/// <param name="Variable">
/// Set on <see cref="RunEventKind.VariableSet"/> and nowhere else: the variable's name, with
/// its value in <see cref="Detail"/>. Appended AFTER <paramref name="Walk"/> so every
/// positional construction that predates D5 still compiles and still means what it did.
/// </param>
public sealed record RunEventDto(
    Guid WalkId,
    RunEventKind Kind,
    int ElapsedMs,
    string? NodeId = null,
    string? Outcome = null,
    string? Detail = null,
    int DurationMs = 0,
    RunWalkDto? Walk = null,
    string? Variable = null);

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
