using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

/// <summary>Один запрос, отправленный проверяемой view-model.</summary>
/// <param name="Type">Использованная константа из <see cref="IpcMessageTypes"/>.</param>
/// <param name="Payload">Объект нагрузки ровно в том виде, в каком его передал вызывающий.</param>
internal sealed record RecordedRequest(string Type, object? Payload);

/// <summary>
/// Программируемый <see cref="IIpcClient"/> для тестов view-model: запоминает, о чём просили,
/// отвечает тем, что подложил тест, и по требованию шлёт пуши.
///
/// Написан руками, а не взят моком FakeItEasy, по двум причинам. Во-первых, заготовленные ответы
/// уходят через <see cref="IpcJson"/>, поэтому тест, подкладывающий <c>MacroGraph[]</c>, гоняет
/// тот самый полиморфный round trip по <c>$type</c>, который демон и положил бы в провод, —
/// view-model, работающая только с объектами в памяти, здесь развалится. Во-вторых, всё
/// завершается синхронно, так что запущенный и брошенный <c>_ = RefreshAsync()</c> уже закончен
/// к возврату из конструктора, и опрашивать тестам ничего не нужно.
/// </summary>
internal sealed class FakeIpcClient : IIpcClient
{
    private readonly Dictionary<string, Func<object?, object?>> _responders = new(StringComparer.Ordinal);

    /// <summary>Все запросы по порядку.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    public bool IsConnected { get; set; } = true;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<IpcEvent>? EventReceived;

    // ---- программирование ответов -------------------------------------------------------

    /// <summary>Отвечает на <paramref name="type"/> фиксированным значением.</summary>
    public FakeIpcClient Respond(string type, object? result)
    {
        _responders[type] = _ => result;
        return this;
    }

    /// <summary>Отвечает на <paramref name="type"/> значением, посчитанным по нагрузке запроса.</summary>
    public FakeIpcClient Respond(string type, Func<object?, object?> responder)
    {
        _responders[type] = responder;
        return this;
    }

    /// <summary>Заставляет <paramref name="type"/> падать так же, как падает отказ от демона.</summary>
    public FakeIpcClient Fail(string type, string error)
    {
        _responders[type] = _ => throw new IpcRequestException(type, error);
        return this;
    }

    // ---- пуши ----------------------------------------------------------------------------

    public void RaiseConnected() => Connected?.Invoke();

    public void RaiseDisconnected() => Disconnected?.Invoke();

    /// <summary>Шлёт пуш, сериализуя <paramref name="payload"/> ровно так же, как это сделал бы демон.</summary>
    public void RaiseEvent(string type, object? payload = null) =>
        EventReceived?.Invoke(new IpcEvent(type, payload is null ? null : IpcJson.Write(payload)));

    // ---- проверки --------------------------------------------------------------------------

    /// <summary>Сколько раз запрашивали <paramref name="type"/>.</summary>
    public int CountOf(string type) =>
        Requests.Count(request => string.Equals(request.Type, type, StringComparison.Ordinal));

    /// <summary>Нагрузки всех запросов <paramref name="type"/>, приведённые к <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> PayloadsOf<T>(string type) =>
    [
        .. Requests
            .Where(request => string.Equals(request.Type, type, StringComparison.Ordinal))
            .Select(request => request.Payload)
            .OfType<T>()
    ];

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

        // Намеренно через проводной сериализатор — см. комментарий к классу.
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
