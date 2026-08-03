namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// The complete protocol catalog: every legal value of <see cref="IpcRequest.Type"/> and
/// <see cref="IpcEvent.Type"/>. Each constant documents its request payload and its
/// response payload; "—" means the payload is <c>null</c>/absent.
///
/// The names are the strings themselves so an unparsed line is still readable in a log.
///
/// Every constant here is live on both ends: each request type has a <c>case</c> in
/// <c>IpcRequestDispatcher</c> (daemon) and a caller in <c>IpcClient</c> (panel); each event
/// type has an <c>IpcServer</c> publisher and a panel subscriber. Adding a constant without
/// wiring both ends is the thing to avoid — this catalog is the protocol, not a wish list.
/// </summary>
public static class IpcMessageTypes
{
    // ---------------------------------------------------------------- requests: windows

    /// <summary>Request: — → Response: <c>WindowDto[]</c>. Snapshot of every tracked window with its tags.</summary>
    public const string GetWindows = "GetWindows";

    /// <summary>Request: <see cref="AddTagRequest"/> → Response: —. Manual tagging from the UI.</summary>
    public const string AddTag = "AddTag";

    /// <summary>Request: <see cref="RemoveTagRequest"/> → Response: —. Manual untagging from the UI.</summary>
    public const string RemoveTag = "RemoveTag";

    // ----------------------------------------------------------------- requests: macros

    /// <summary>
    /// Request: <see cref="RunMacroRequest"/> → Response: —. Manual Run from the editor.
    /// The daemon resolves the cursor context itself; the client sends no coordinates.
    /// </summary>
    public const string RunMacro = "RunMacro";

    /// <summary>
    /// Request: <see cref="StopMacroRequest"/> → Response: —. Cancels one tracked RUN — which
    /// means every walk in it, including the nine siblings of a ten-window fan-out.
    ///
    /// <b>This is also the debugger's ■ Стоп</b>, and it is the one debugger control that is
    /// not per-walk. The asymmetry is deliberate: pause and step exist to look at ONE walk,
    /// but nobody pressing stop while ten clients are being driven means "stop one of them".
    /// The panel is required to LABEL it — the button says «■ Стоп ×3» when the selected
    /// walk's run has three of them.
    /// </summary>
    public const string StopMacro = "StopMacro";

    /// <summary>Request: — → Response: <c>RunningMacroDto[]</c>. Snapshot of the run registry.</summary>
    public const string GetRunningMacros = "GetRunningMacros";

    /// <summary>Request: — → Response: <c>MacroGraph[]</c>. The whole macro library.</summary>
    public const string GetMacros = "GetMacros";

    /// <summary>
    /// Request: <see cref="SaveMacroRequest"/> → Response: <c>ValidationIssueDto[]</c>.
    /// An EMPTY array means the graph was written; a non-empty one means it was rejected
    /// and carries the reasons. Warnings alone do not block a save, so a rejection always
    /// contains at least one <c>Error</c>.
    /// </summary>
    public const string SaveMacro = "SaveMacro";

    /// <summary>Request: <see cref="DeleteMacroRequest"/> → Response: —. Deletes the macro file.</summary>
    public const string DeleteMacro = "DeleteMacro";

    /// <summary>
    /// Request: <see cref="SubscribeRunEventsRequest"/> → Response: <c>RunWalkDto[]</c>.
    /// Turns the <see cref="RunEvents"/> stream on or off FOR THIS CONNECTION.
    ///
    /// <b>Opt-in on purpose.</b> With nobody subscribed the executor emits nothing at all —
    /// no timing, no detail strings, no serialisation — so the resident daemon costs the
    /// same whether or not a panel exists. This is the load-bearing half of the flooding
    /// answer; the other half is that the events are batched (see <c>RunEventBatch</c>).
    ///
    /// The response is the set of walks ALREADY in flight, each with
    /// <c>FromStart = false</c>: a subscriber that arrives mid-run has missed rows nobody
    /// can reconstruct, and the protocol says so rather than letting the panel show a
    /// partial log as a complete one. Subscriptions do NOT survive a reconnect — the
    /// daemon forgets a connection's flag with the connection — so a client must re-send
    /// this on every <c>Connected</c>.
    /// </summary>
    public const string SubscribeRunEvents = "SubscribeRunEvents";

    // -------------------------------------------------------------- requests: debugger

    /// <summary>
    /// Request: <see cref="SetBreakpointsRequest"/> → Response: —. Replaces the breakpoint
    /// set of ONE macro.
    ///
    /// <b>Breakpoints live in the daemon's session, not in the macro file.</b> See
    /// <c>MacroDebugSession</c> for the reasoning; the protocol consequences are that they
    /// survive a panel restart (the daemon is resident), that they are lost when the daemon
    /// exits, and that they never appear in a <c>SaveMacro</c> payload or in a git diff.
    ///
    /// Settable while nothing is running — arming a breakpoint before pressing Run is the
    /// normal way to use one.
    /// </summary>
    public const string SetBreakpoints = "SetBreakpoints";

    /// <summary>
    /// Request: — → Response: <c>BreakpointSetDto[]</c>. Every macro that has breakpoints.
    ///
    /// A pull, like <see cref="GetHotkeyFailures"/> and for the same reason: the set only
    /// changes when a panel changes it. Re-read on every <c>Connected</c>, which is what makes
    /// a breakpoint survive the panel being closed and reopened.
    /// </summary>
    public const string GetBreakpoints = "GetBreakpoints";

    /// <summary>
    /// Request: <see cref="DebugCommandRequest"/> → Response: <c>DebugAckDto</c>.
    /// Pause / resume / step / run-to-node, addressed to ONE WALK.
    ///
    /// <b>Requires the caller to be subscribed to <see cref="SubscribeRunEvents"/></b>, which
    /// is also what keeps a paused walk from outliving its audience: the daemon counts run
    /// event subscribers as attached debuggers, and the last one leaving releases every
    /// parked walk. A walk parked with nobody watching would hold its macro's single-flight
    /// slot — and therefore kill that macro's hotkey — until the daemon restarted.
    /// </summary>
    public const string DebugCommand = "DebugCommand";

    // --------------------------------------------------------------- requests: hotkeys

    /// <summary>
    /// Request: — → Response: —. Unregisters the daemon's global hotkeys so the editor's
    /// hotkey picker can capture a chord instead of firing a macro with it.
    /// </summary>
    public const string SuspendHotkeys = "SuspendHotkeys";

    /// <summary>Request: — → Response: —. Re-registers the hotkeys suspended by <see cref="SuspendHotkeys"/>.</summary>
    public const string ResumeHotkeys = "ResumeHotkeys";

    /// <summary>
    /// Request: — → Response: <c>HotkeyFailureDto[]</c>. Chords the daemon has bound to a
    /// macro but could not register with Win32, because something outside this application
    /// owns them.
    ///
    /// A pull rather than a push, deliberately: the list only ever changes when the daemon
    /// (re-)registers, and the panel is the thing that causes that — it re-fetches after
    /// <see cref="ResumeHotkeys"/> completes, on <see cref="MacrosChanged"/>, and on every
    /// reconnect. Adding an event type for a value nobody can change behind the panel's back
    /// would be protocol for its own sake.
    ///
    /// While the panel holds hotkeys suspended the answer describes the LAST real
    /// registration, which is the useful answer — see <c>IHotkeyRegistration.Failures</c>.
    /// </summary>
    public const string GetHotkeyFailures = "GetHotkeyFailures";

    // ------------------------------------------------------------- requests: diagnostics

    /// <summary>Request: — → Response: JSON string, the directory the captures were written to. Vision debugging.</summary>
    public const string DumpCaptures = "DumpCaptures";

    /// <summary>Request: — → Response: —. Orderly daemon shutdown; the reply is sent before the process exits.</summary>
    public const string Shutdown = "Shutdown";

    // -------------------------------------------------------------- requests: lifecycle

    /// <summary>
    /// Request: — → Response: —. "Bring the panel to the front." The daemon answers by
    /// broadcasting <see cref="ActivateWindow"/> to every client, INCLUDING the one that
    /// asked — the sender is normally a second UI launch that is about to exit, and the
    /// recipient is the panel already on screen.
    ///
    /// It exists as a round trip through the daemon rather than as a direct
    /// process-to-process poke because the second instance knows nothing about the first:
    /// no window handle, no pid, only the pipe they share.
    /// </summary>
    public const string RequestActivate = "RequestActivate";

    // ------------------------------------------------------------------------- events

    /// <summary>Payload: <c>WindowDto</c>. A new window of a monitored process was registered.</summary>
    public const string WindowAppeared = "WindowAppeared";

    /// <summary>Payload: <c>WindowDto</c> (full new state, not a delta). The window's tag set changed.</summary>
    public const string WindowTagsChanged = "WindowTagsChanged";

    /// <summary>Payload: <see cref="WindowClosedEvent"/>. The window is gone and its tags with it.</summary>
    public const string WindowClosed = "WindowClosed";

    /// <summary>Payload: —. The macro library changed on disk; the client re-fetches with <see cref="GetMacros"/>.</summary>
    public const string MacrosChanged = "MacrosChanged";

    /// <summary>Payload: <c>RunningMacroDto[]</c>. The run registry changed; carries the new snapshot.</summary>
    public const string RunningMacrosChanged = "RunningMacrosChanged";

    /// <summary>
    /// Payload: <c>RunEventBatch</c>. What the executor did since the last flush — nodes
    /// entered and left, walks started and finished.
    ///
    /// Sent ONLY to connections that asked via <see cref="SubscribeRunEvents"/>, and only
    /// in coalesced batches. Both properties are deliberate: this is the one event whose
    /// natural rate (hundreds per second during a fan-out) exceeds what a per-connection
    /// queue of 256 can absorb, and a dropped panel mid-run is precisely the failure the
    /// user would be watching.
    ///
    /// Also carries the debugger's <c>Paused</c> / <c>BreakpointHit</c> / <c>Resumed</c> and
    /// the variables panel's <c>VariableSet</c>. The first three BYPASS the coalescing
    /// window: a step that takes 50 ms longer than it had to feels like a stuck button, and
    /// they are three events, not three hundred.
    /// </summary>
    public const string RunEvents = "RunEvents";

    /// <summary>
    /// Payload: —. Someone asked for the panel to come to the foreground: the tray's
    /// "Открыть панель" when a panel is already running, or a second UI launch via
    /// <see cref="RequestActivate"/>.
    /// </summary>
    public const string ActivateWindow = "ActivateWindow";
}
