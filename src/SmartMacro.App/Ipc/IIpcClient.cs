using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Ipc;

/// <summary>
/// The panel's only way to reach the engine: a request/response + event connection to the
/// daemon's control pipe.
///
/// After stage 3 the UI process owns no domain state at all — every window, tag, macro and
/// run it shows arrived through here. Two consequences shape this interface:
///
///   * <b>Events are the truth, responses are a snapshot.</b> A view-model subscribes to
///     <see cref="EventReceived"/> and treats the daemon's pushes as authoritative; the
///     <c>Get*</c> requests exist only to seed that stream.
///   * <b><see cref="Connected"/> is a re-fetch signal, not a nicety.</b> The server DROPS a
///     client that stops draining events, and the reconnect that follows leaves a hole in
///     the stream. Every view-model must therefore re-fetch its snapshots on this event —
///     that is the ONLY thing keeping the UI from silently diverging after a hiccup.
///
/// Kept as an interface so every view-model in this assembly can be exercised headlessly
/// against a fake, with no pipe, no daemon and no desktop session.
/// </summary>
public interface IIpcClient : IAsyncDisposable
{
    /// <summary>Whether a live connection exists right now. Racy by nature — a request may still fail.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Raised after every successful (re)connect, on a thread-pool thread. Subscribers
    /// re-fetch their snapshots; see the note on the interface.
    /// </summary>
    event Action? Connected;

    /// <summary>
    /// Raised when a live connection is lost, on a thread-pool thread. NOT raised for an
    /// orderly <see cref="IAsyncDisposable.DisposeAsync"/> — that is the UI shutting itself
    /// down, not the daemon going away.
    /// </summary>
    event Action? Disconnected;

    /// <summary>
    /// Raised for every unsolicited daemon push, on a thread-pool thread. Handlers must
    /// return promptly and must marshal to the UI thread themselves.
    /// </summary>
    event Action<IpcEvent>? EventReceived;

    /// <summary>
    /// Sends a request and materialises its response payload.
    /// </summary>
    /// <param name="type">One of the <see cref="IpcMessageTypes"/> request constants.</param>
    /// <param name="payload">Typed request payload, or <c>null</c> for the argument-less requests.</param>
    /// <param name="timeout">Overrides the client's default; use a generous one for <c>DumpCaptures</c>.</param>
    /// <returns>The deserialized payload, or <c>default</c> when the daemon replied without one.</returns>
    /// <exception cref="IpcRequestException">No connection, or the daemon answered <c>Ok = false</c>.</exception>
    /// <exception cref="TimeoutException">No reply within the timeout. The request may still have been executed.</exception>
    Task<TResult?> RequestAsync<TResult>(string type, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Sends a request and waits for the acknowledgement, discarding any payload.</summary>
    /// <inheritdoc cref="RequestAsync{TResult}"/>
    Task RequestAsync(string type, object? payload = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A request that did not succeed: the daemon answered <c>Ok = false</c>, or there was no
/// connection to send it on in the first place. Carries the request type so a handler that
/// catches it can say what failed without threading the name through itself.
/// </summary>
public sealed class IpcRequestException : Exception
{
    public IpcRequestException(string requestType, string message)
        : base($"{requestType}: {message}")
    {
        RequestType = requestType;
    }

    public IpcRequestException(string requestType, string message, Exception innerException)
        : base($"{requestType}: {message}", innerException)
    {
        RequestType = requestType;
    }

    public IpcRequestException()
    {
        RequestType = string.Empty;
    }

    public IpcRequestException(string message) : base(message)
    {
        RequestType = string.Empty;
    }

    public IpcRequestException(string message, Exception innerException) : base(message, innerException)
    {
        RequestType = string.Empty;
    }

    /// <summary>The <see cref="IpcMessageTypes"/> constant the failed call used.</summary>
    public string RequestType { get; }
}
