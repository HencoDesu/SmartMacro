using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Ipc;

/// <summary>
/// The "push this at every connected panel" capability of <see cref="IpcServer"/>, split
/// out as an interface for the two consumers that need it without needing the server:
/// <see cref="IpcRequestDispatcher"/> (handling <c>RequestActivate</c>) and the daemon's
/// tray controller (bringing an already-running panel forward).
///
/// The dispatcher cannot simply take an <see cref="IpcServer"/> — the server is constructed
/// WITH the dispatcher, so DI would see a cycle. It gets its broadcaster handed to it by
/// the server instead (see <see cref="IpcRequestDispatcher.AttachBroadcaster"/>); the tray,
/// which nothing constructs the server from, resolves this interface normally.
/// </summary>
public interface IIpcBroadcaster
{
    /// <summary>
    /// Queues <paramref name="evt"/> on every live connection. Non-blocking and never
    /// throws — a client that cannot keep up is dropped, not reported back to the caller.
    /// </summary>
    void Broadcast(IpcEvent evt);
}
