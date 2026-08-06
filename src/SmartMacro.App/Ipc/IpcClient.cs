using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Resources;

namespace SmartMacro.App.Ipc;

/// <summary>
/// Клиентская половина управляющего протокола: одно <see cref="IpcConnection"/> к named pipe
/// демона, живущее столько же, сколько сама панель.
///
/// <b>Корреляция, а не порядок.</b> Сервер не дожидается обработчика, прежде чем читать
/// следующий запрос, поэтому <c>StopMacro</c> (который ждёт подтверждения от исполнителя) и
/// <c>DumpCaptures</c> (который снимает каждое окно) возвращаются тогда, когда возвращаются, —
/// возможно, много позже отправленных после них запросов. Поэтому каждый ответ сопоставляется
/// по <see cref="IpcRequest.Id"/> с <see cref="_pending"/>, и ничто в этом классе не полагается
/// на то, что ответы приходят по порядку.
///
/// <b>Один читатель, много писателей.</b> Единственный цикл владеет стороной чтения и
/// разделяет поток: строка с id завершает ожидающий запрос, строка без него — непрошеное
/// событие. Записи приходят из произвольных потоков UI, и упорядочивает их само
/// <see cref="IpcConnection"/>.
///
/// <b>Переподключение — норма, а не исключение.</b> Демон выбрасывает клиента, переставшего
/// вычерпывать события, да и пользователь может его перезапустить. Поэтому поддерживающий цикл
/// переподключается всю жизнь процесса, с откатом от 250 мс до 2 с, и поднимает
/// <see cref="Connected"/> при каждой удаче — именно это и велит view-model'ям перезапросить
/// всё заново, потому что пропущенное за время разрыва просто исчезло.
/// </summary>
public sealed class IpcClient : IIpcClient
{
    /// <summary>
    /// Открывает одно кадрированное соединение с демоном. Внедряется извне — и возвращает
    /// <see cref="IpcConnection"/>, а не <see cref="Stream"/>, — чтобы тесты протокола могли
    /// гонять настоящего клиента поверх половинок в памяти, а это два потока, а не один
    /// дуплексный объект.
    /// </summary>
    public delegate Task<IpcConnection> ConnectionFactory(CancellationToken cancellationToken);

    /// <summary>Сколько по умолчанию ждём один запрос. С запасом: труба локальная, а демон не занят.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private const int MinBackoffMs = 250;
    private const int MaxBackoffMs = 2000;

    // Таймаут трубы на одну попытку. Короткий, потому что быстрый отказ просто подкармливает
    // цикл отката.
    private const int PipeConnectTimeoutMs = 1000;

    private readonly ConnectionFactory _factory;
    private readonly TimeSpan _defaultTimeout;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>> _pending = new();
    private readonly CancellationTokenSource _stopping = new();

    // НЕ `volatile`: DropAsync подменяет его через Interlocked, а C# запрещает передавать
    // volatile-поле по ссылке. Чтение ссылки атомарно, а запись через Interlocked публикует
    // значение.
    private IpcConnection? _connection;
    private Task? _maintainLoop;
    private int _nextId;
    private int _disposed;

    /// <param name="factory">Чем открывать транспорт; по умолчанию — named pipe демона.</param>
    /// <param name="defaultTimeout">Таймаут на запрос, когда вызов не задал свой.</param>
    /// <param name="logger">Куда сливать диагностику; по умолчанию — окружающий логгер Serilog.</param>
    public IpcClient(ConnectionFactory? factory = null, TimeSpan? defaultTimeout = null, ILogger? logger = null)
    {
        _factory = factory ?? ConnectPipeAsync;
        _defaultTimeout = defaultTimeout ?? DefaultRequestTimeout;
        _log = logger ?? Log.ForContext<IpcClient>();
    }

    public bool IsConnected => _connection is not null;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<IpcEvent>? EventReceived;

    /// <summary>
    /// Подключается и держит соединение поднятым до самого освобождения.
    /// </summary>
    /// <param name="initialConnectWindow">
    /// Сколько повторять ПЕРВОЕ подключение. Демон, возможно, только что запущен нами и всё ещё
    /// собирает свой корень зависимостей, так что счёт тут на секунды, а не на миллисекунды.
    /// </param>
    /// <param name="cancellationToken">Прекращает попытки подключиться досрочно.</param>
    /// <returns>
    /// <c>false</c>, когда демон так и не ответил за отведённое окно, — вызывающий показывает
    /// ошибку и выходит. После <c>true</c> все последующие обрывы разбираются внутри.
    /// </returns>
    public async Task<bool> StartAsync(TimeSpan initialConnectWindow, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var deadline = Environment.TickCount64 + (long)initialConnectWindow.TotalMilliseconds;

        while (!linked.IsCancellationRequested)
        {
            if (await TryConnectAsync(linked.Token).ConfigureAwait(false))
            {
                // Дальше эстафету берёт поддерживающий цикл: он запускает читателя для только
                // что установленного соединения и владеет всеми переподключениями после.
                _maintainLoop = Task.Run(() => MaintainAsync(_stopping.Token), CancellationToken.None);
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            try
            {
                await Task.Delay(MinBackoffMs, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    public async Task<TResult?> RequestAsync<TResult>(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(type, payload, timeout, cancellationToken).ConfigureAwait(false);
        return IpcJson.Read<TResult>(response.Payload);
    }

    public async Task RequestAsync(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        await SendAsync(type, payload, timeout, cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_maintainLoop is { } loop)
        {
            // С потолком: цикл может стоять в чтении, которое отмена должна расшевелить, но
            // труба, отказывающаяся отменяться, не имеет права задерживать выход процесса.
            await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
            _maintainLoop = null;
        }

        // notify: false — это UI гасит сам себя, а не демон пропадает, тогда как работа
        // обработчика Disconnected — сообщить пользователю, что демон умер.
        await DropAsync(_connection, notify: false).ConfigureAwait(false);
        _stopping.Dispose();
    }

    // ------------------------------------------------------------------ проводка запросов

    private async Task<IpcResponse> SendAsync(string type, object? payload, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var connection = _connection
                         ?? throw new IpcRequestException(type, Strings_App.Dialog_Ipc_NotConnected);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var element = payload is null ? (JsonElement?)null : IpcJson.Write(payload);
            await connection.WriteAsync(new IpcRequest(id, type, element), cancellationToken).ConfigureAwait(false);

            var response = await completion.Task
                .WaitAsync(timeout ?? _defaultTimeout, cancellationToken)
                .ConfigureAwait(false);

            return response.Ok
                ? response
                : throw new IpcRequestException(type, response.Error ?? Strings_App.Dialog_Ipc_RejectedSilently);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Труба порвалась прямо под записью. Поддерживающий цикл это заметит и переподключится.
            throw new IpcRequestException(type, Strings_App.Dialog_Ipc_Broken, ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // ------------------------------------------------------------------- цикл соединения

    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        var backoffMs = MinBackoffMs;

        while (!cancellationToken.IsCancellationRequested)
        {
            var connection = _connection;
            if (connection is null)
            {
                if (await TryConnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    backoffMs = MinBackoffMs;
                    continue;
                }

                try
                {
                    await Task.Delay(backoffMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                continue;
            }

            // Поднимается вне потока чтения, чтобы обработчик, отправляющий запросы (а это
            // делает каждая view-model), не мог задержать тот самый цикл, которому предстоит
            // читать ответы на них.
            Raise(Connected);

            await ReadLoopAsync(connection, cancellationToken).ConfigureAwait(false);
            await DropAsync(connection, notify: !cancellationToken.IsCancellationRequested).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        IpcConnection connection;
        try
        {
            connection = await _factory(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // В подавляющем большинстве случаев это «демон ещё не слушает» (TimeoutException от
            // NamedPipeClientStream). Debug, а не Warning: пока демон поднимается, цикл отката
            // повторяет это по нескольку раз в секунду.
            _log.Debug(ex, "Не удалось подключиться к каналу демона '{Pipe}'", IpcPipe.Name);
            return false;
        }

        _connection = connection;
        _log.Information("Подключено к демону по каналу '{Pipe}'", IpcPipe.Name);
        return true;
    }

    private async Task ReadLoopAsync(IpcConnection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IpcInbound? inbound;
            try
            {
                inbound = await connection.ReadAsync<IpcInbound>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // По контракту это поправимо: читатель уже стоит на следующей строке, так что
                // одно покорёженное сообщение — не порванное соединение.
                _log.Warning(ex, "Неразбираемая строка от демона — пропущена");
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Debug(ex, "Чтение из канала прервано");
                return;
            }

            if (inbound is null)
            {
                return; // EOF — демон закрыл трубу
            }

            Dispatch(inbound);
        }
    }

    private void Dispatch(IpcInbound inbound)
    {
        if (inbound.Id is { } id)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetResult(new IpcResponse(id, inbound.Ok ?? false, inbound.Payload, inbound.Error));
            }
            else
            {
                // Ответ на запрос, который уже отвалился по таймауту или был отменён. Нормально.
                _log.Debug("Ответ на неизвестный запрос #{Id} — проигнорирован", id);
            }

            return;
        }

        var evt = new IpcEvent(inbound.Type ?? string.Empty, inbound.Payload);
        try
        {
            // Прямо здесь, НЕ через постановку в очередь: порядок событий значим
            // (WindowAppeared раньше идущего следом WindowTagsChanged), а раскидывание каждого
            // по потокам его бы перемешало. Обработчики по контракту не блокируют — они
            // перекладывают работу в поток UI.
            EventReceived?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Обработчик события '{Type}' бросил исключение", evt.Type);
        }
    }

    private async Task DropAsync(IpcConnection? connection, bool notify)
    {
        if (connection is null)
        {
            return;
        }

        // Поле обнуляет только владелец ИМЕННО ЭТОГО соединения: переподключение могло уже
        // поставить туда новое.
        Interlocked.CompareExchange(ref _connection, null, connection);
        await connection.DisposeAsync().ConfigureAwait(false);

        // Каждый запрос в полёте умирает вместе с соединением. Завалить их явно лучше, чем дать
        // каждому выжечь свой таймаут, — UI узнаёт об этом сразу.
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(new IpcRequestException("(соединение)", Strings_App.Dialog_Ipc_Disconnected));
            }
        }

        if (notify)
        {
            _log.Warning("Соединение с демоном потеряно");
            Raise(Disconnected);
        }
    }

    private static void Raise(Action? handler)
    {
        if (handler is not null)
        {
            Task.Run(handler);
        }
    }

    private static async Task<IpcConnection> ConnectPipeAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", IpcPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(PipeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // leaveOpen: false — освобождение соединения закрывает трубу, и на это как раз и
        // рассчитан путь переподключения.
        return new IpcConnection(pipe);
    }

    /// <summary>
    /// Одна входящая строка, пока мы ещё не знаем, что это. У протокола две формы «демон →
    /// клиент» в одном потоке и никакого различающего поля: у <see cref="IpcResponse"/> есть
    /// <c>Id</c>, у <see cref="IpcEvent"/> его не бывает никогда. Чтение в это снисходительное
    /// объединение и проверка <see cref="Id"/> — вот что делает вопрос разрешимым, не
    /// подглядывая в сырой JSON.
    /// </summary>
    private sealed record IpcInbound(int? Id, bool? Ok, string? Type, JsonElement? Payload, string? Error);
}
