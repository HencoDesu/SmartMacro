namespace SmartMacro.Ipc;

/// <summary>
/// The bit of protocol state that belongs to ONE connection rather than to the engine.
///
/// Everything else in the catalogue is stateless per client: a request names what it wants
/// and the answer is the same whoever asked. Subscriptions are not — "send me run events"
/// is a fact about a particular pipe, and the whole point of making it opt-in is that a
/// second client which never asked keeps getting nothing. The dispatcher is handed one of
/// these alongside the request so that <c>SubscribeRunEvents</c> can be answered without
/// <see cref="IpcRequestDispatcher"/> learning what a connection is.
///
/// <c>null</c> in the dispatcher tests, which drive the catalogue with no server underneath;
/// the one handler that needs a session rejects politely when there isn't one.
///
/// Wave D5's debugger commands (pause / step / run-to-node) attach to a walk, not to a
/// connection, so they will NOT need to grow this interface — but a per-client "follow only
/// this walk" filter would live here if the volume ever justified one.
/// </summary>
public interface IIpcSession
{
    /// <summary>Whether this connection is receiving the <c>RunEvents</c> stream.</summary>
    bool WantsRunEvents { get; }

    /// <summary>
    /// Starts or stops the stream for this connection. Idempotent — the subscriber count
    /// the engine gates on must not drift when a client asks twice.
    /// </summary>
    void SetRunEventSubscription(bool enabled);
}
