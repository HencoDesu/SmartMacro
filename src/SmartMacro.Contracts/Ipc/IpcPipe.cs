namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// The daemon's control endpoint address. Lives in Contracts because both ends need it
/// and they share no other assembly: the daemon's <c>IpcServer</c> listens on it, the
/// UI's client dials it. Duplicating the literal on either side is the one drift that
/// would fail silently at runtime — a UI that connects to nothing looks exactly like a
/// daemon that isn't running.
/// </summary>
public static class IpcPipe
{
    /// <summary>Pipe name. Full path: <c>\\.\pipe\smartmacro-control</c>.</summary>
    public const string Name = "smartmacro-control";

    /// <summary>Server-side cap on simultaneous UI/CLI clients.</summary>
    public const int MaxServerInstances = 8;
}
