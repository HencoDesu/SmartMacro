using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Ipc;

/// <summary>
/// Единственный способ панели дотянуться до движка: соединение «запрос/ответ» плюс события с
/// управляющей трубой демона.
///
/// После стадии 3 процесс UI не владеет вообще никаким доменным состоянием — каждое окно, тег,
/// макрос и прогон, которые он показывает, приехали отсюда. Из этого вытекают две вещи, которые
/// и определили форму интерфейса:
///
///   * <b>Истина — в событиях, ответ — это снимок.</b> View-model подписывается на
///     <see cref="EventReceived"/> и считает пуши демона источником истины; запросы <c>Get*</c>
///     существуют лишь для того, чтобы засеять этот поток.
///   * <b><see cref="Connected"/> — сигнал перечитать, а не любезность.</b> Сервер ВЫБРАСЫВАЕТ
///     клиента, переставшего вычерпывать события, и переподключение вслед за этим оставляет в
///     потоке дыру. Поэтому каждая view-model обязана по этому событию перезапросить свои
///     снимки — это ЕДИНСТВЕННОЕ, что удерживает UI от тихого расхождения после сбоя.
///
/// Оставлен интерфейсом, чтобы любую view-model этой сборки можно было гонять headless против
/// подделки — без трубы, без демона и без сессии рабочего стола.
/// </summary>
public interface IIpcClient : IAsyncDisposable
{
    /// <summary>Есть ли живое соединение прямо сейчас. По природе своей гонка — запрос всё равно может провалиться.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Поднимается после каждого удачного (пере)подключения, в потоке пула. Подписчики
    /// перезапрашивают свои снимки; см. заметку на самом интерфейсе.
    /// </summary>
    event Action? Connected;

    /// <summary>
    /// Поднимается, когда живое соединение потеряно, в потоке пула. НЕ поднимается при
    /// упорядоченном <see cref="IAsyncDisposable.DisposeAsync"/> — это UI гасит сам себя, а не
    /// демон уходит.
    /// </summary>
    event Action? Disconnected;

    /// <summary>
    /// Поднимается на каждый непрошеный пуш демона, в потоке пула. Обработчики обязаны быстро
    /// возвращать управление и сами перекладывать работу в поток UI.
    /// </summary>
    event Action<IpcEvent>? EventReceived;

    /// <summary>
    /// Отправляет запрос и материализует нагрузку ответа.
    /// </summary>
    /// <param name="type">Одна из констант-запросов <see cref="IpcMessageTypes"/>.</param>
    /// <param name="payload">Типизированная нагрузка запроса либо <c>null</c> для запросов без аргументов.</param>
    /// <param name="timeout">Перекрывает значение по умолчанию у клиента; для <c>DumpCaptures</c> берите с запасом.</param>
    /// <param name="cancellationToken">
    /// Снимает с ожидания ответа нас, но не демона: отменённый запрос уже ушёл в трубу, и
    /// выполнить его демон вполне может — ровно как при <see cref="TimeoutException"/>.
    /// </param>
    /// <returns>Десериализованная нагрузка либо <c>default</c>, если демон ответил без неё.</returns>
    /// <exception cref="IpcRequestException">Соединения нет либо демон ответил <c>Ok = false</c>.</exception>
    /// <exception cref="TimeoutException">Ответа в отведённое время не пришло. Запрос при этом мог и выполниться.</exception>
    Task<TResult?> RequestAsync<TResult>(string type, object? payload = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Отправляет запрос и ждёт подтверждения, отбрасывая любую нагрузку.</summary>
    /// <inheritdoc cref="RequestAsync{TResult}"/>
    Task RequestAsync(string type, object? payload = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Запрос, который не удался: демон ответил <c>Ok = false</c> либо соединения, по которому его
/// было бы отправить, изначально не было. Несёт в себе тип запроса, чтобы поймавший исключение
/// обработчик мог сказать, что именно провалилось, не протаскивая имя через себя сам.
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

    /// <summary>Константа <see cref="IpcMessageTypes"/>, с которой шёл провалившийся вызов.</summary>
    public string RequestType { get; }
}
