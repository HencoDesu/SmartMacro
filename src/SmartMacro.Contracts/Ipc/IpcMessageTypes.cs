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

    /// <summary>Request: <see cref="StopMacroRequest"/> → Response: —. Cancels one tracked run.</summary>
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

    // --------------------------------------------------------------- requests: hotkeys

    /// <summary>
    /// Request: — → Response: —. Unregisters the daemon's global hotkeys so the editor's
    /// hotkey picker can capture a chord instead of firing a macro with it.
    /// </summary>
    public const string SuspendHotkeys = "SuspendHotkeys";

    /// <summary>Request: — → Response: —. Re-registers the hotkeys suspended by <see cref="SuspendHotkeys"/>.</summary>
    public const string ResumeHotkeys = "ResumeHotkeys";

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
    /// Payload: —. Someone asked for the panel to come to the foreground: the tray's
    /// "Открыть панель" when a panel is already running, or a second UI launch via
    /// <see cref="RequestActivate"/>.
    /// </summary>
    public const string ActivateWindow = "ActivateWindow";
}
