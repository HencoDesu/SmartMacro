using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

/// <summary>One request the view-model under test sent.</summary>
/// <param name="Type">The <see cref="IpcMessageTypes"/> constant used.</param>
/// <param name="Payload">The payload object, exactly as the caller passed it.</param>
internal sealed record RecordedRequest(string Type, object? Payload);

/// <summary>
/// A scriptable <see cref="IIpcClient"/> for the view-model tests: record what was asked,
/// answer with whatever the test staged, and push events on demand.
///
/// Hand-written rather than a FakeItEasy mock for two reasons. First, canned answers go
/// through <see cref="IpcJson"/> on the way out, so a test that stages a
/// <c>MacroGraph[]</c> exercises the real polymorphic <c>$type</c> round trip the daemon
/// would put on the wire — a VM that only works against in-memory objects fails here.
/// Second, everything completes synchronously, so a VM's fire-and-forget
/// <c>_ = RefreshAsync()</c> is already finished when its constructor returns and the tests
/// need no polling.
/// </summary>
internal sealed class FakeIpcClient : IIpcClient
{
    private readonly Dictionary<string, Func<object?, object?>> _responders = new(StringComparer.Ordinal);

    /// <summary>Every request, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    public bool IsConnected { get; set; } = true;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<IpcEvent>? EventReceived;

    // ---- scripting ---------------------------------------------------------------------

    /// <summary>Answers <paramref name="type"/> with a fixed value.</summary>
    public FakeIpcClient Respond(string type, object? result)
    {
        _responders[type] = _ => result;
        return this;
    }

    /// <summary>Answers <paramref name="type"/> with a value computed from the request payload.</summary>
    public FakeIpcClient Respond(string type, Func<object?, object?> responder)
    {
        _responders[type] = responder;
        return this;
    }

    /// <summary>Makes <paramref name="type"/> fail the way a daemon rejection does.</summary>
    public FakeIpcClient Fail(string type, string error)
    {
        _responders[type] = _ => throw new IpcRequestException(type, error);
        return this;
    }

    // ---- pushes ------------------------------------------------------------------------

    public void RaiseConnected() => Connected?.Invoke();

    public void RaiseDisconnected() => Disconnected?.Invoke();

    /// <summary>Pushes an event, serialising <paramref name="payload"/> exactly as the daemon would.</summary>
    public void RaiseEvent(string type, object? payload = null) =>
        EventReceived?.Invoke(new IpcEvent(type, payload is null ? null : IpcJson.Write(payload)));

    // ---- assertions --------------------------------------------------------------------

    /// <summary>How many times <paramref name="type"/> was requested.</summary>
    public int CountOf(string type) =>
        Requests.Count(request => string.Equals(request.Type, type, StringComparison.Ordinal));

    /// <summary>Payloads of every <paramref name="type"/> request, cast to <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> PayloadsOf<T>(string type) =>
        [.. Requests
            .Where(request => string.Equals(request.Type, type, StringComparison.Ordinal))
            .Select(request => request.Payload)
            .OfType<T>()];

    // ---- IIpcClient --------------------------------------------------------------------

    public Task<TResult?> RequestAsync<TResult>(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = Invoke(type, payload);
        if (result is null)
        {
            return Task.FromResult<TResult?>(default);
        }

        // Through the wire serialiser deliberately — see the class comment.
        return Task.FromResult(IpcJson.Read<TResult>(IpcJson.Write(result)));
    }

    public Task RequestAsync(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Invoke(type, payload);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private object? Invoke(string type, object? payload)
    {
        Requests.Add(new RecordedRequest(type, payload));
        return _responders.TryGetValue(type, out var responder) ? responder(payload) : null;
    }
}
