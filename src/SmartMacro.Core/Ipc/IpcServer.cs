using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Windows;

namespace SmartMacro.Ipc;

/// <summary>
/// Управляющая точка входа демона: named pipe (<see cref="PipeName"/>), говорящий на JSON
/// Lines, обслуживающий сразу несколько клиентов UI, направляющий их запросы через
/// <see cref="IpcRequestDispatcher"/> и толкающий им всем события движка.
///
/// <b>Устройство.</b> Три слоя, разделённые так, чтобы настоящая труба была нужна только
/// самому внешнему: <see cref="IpcConnection"/> занимается кадрированием и сериализацией
/// записи поверх любого <see cref="Stream"/>; <see cref="IpcRequestDispatcher"/> превращает
/// конверт в конверт; этот класс владеет циклом приёма, множеством живых соединений и
/// рассылкой событий. Шов — это
/// <see cref="ServeConnectionAsync(Stream, CancellationToken)"/>: цикл приёма зовёт его с
/// трубой, тесты зовут его с половинками в памяти, и всё, что ниже шва, в обоих случаях
/// одинаково.
///
/// <b>Параллелизм внутри соединения.</b> У каждого соединения две задачи: цикл чтения и насос
/// событий. Цикл чтения НЕ дожидается обработчика, прежде чем прочитать следующий запрос:
/// <c>StopMacro</c> ждёт подтверждения от бегуна, а <c>DumpCaptures</c> снимает скриншот с
/// каждого окна, и ни то ни другое не имеет права заткнуть собой остальной трафик панели.
/// Отсюда следует, что ответы могут приходить не по порядку, — и ровно за этим и нужен
/// <see cref="IpcRequest.Id"/>.
///
/// <b>Доставка событий: по одной ограниченной очереди на соединение.</b> События движка
/// поднимаются на его же потоках (нода макроса ставит тег, наблюдатель хранилища
/// перезагружает файлы), поэтому здешние обработчики не делают ничего, кроме неблокирующего
/// <c>TryWrite</c> в очередь каждого клиента, и возвращаются. Клиент, переставший разбирать
/// очередь, забивает её, <c>TryWrite</c> падает, и это соединение ОТКЛЮЧАЮТ, вместо того чтобы
/// позволить ему тормозить производителя: UI переподключится и заново вытянет свежий снимок, а
/// это и дешевле, и правильнее, чем UI, догоняющий жизнь по накопленному хвосту. Альтернативой
/// была одна общая очередь, и она хуже: один залипший клиент застопорил бы доставку событий
/// всем остальным.
/// </summary>
public sealed partial class IpcServer : IHostedService, IAsyncDisposable, IIpcBroadcaster
{
    /// <summary>
    /// Имя трубы — берётся из <see cref="IpcPipe.Name"/> в Contracts, и это единственное место,
    /// где любому из концов позволено его задать. С этой сборкой UI не делит ни одной.
    /// </summary>
    public const string PipeName = IpcPipe.Name;

    // UI обычно и есть единственный клиент; запас — на отладочную консоль рядом с ним и на
    // промежуток между падением UI и тем моментом, когда Windows заберёт его дескриптор. Сверх
    // этого цикл приёма отступает и пробует снова, а не падает.
    private const int MaxServerInstances = IpcPipe.MaxServerInstances;

    // Сколько событий на соединение мы терпим, прежде чем поставить на нём крест. UI, не
    // разобравший 256 событий, не медленный — его уже нет (или он в клинче), и путь
    // переподключения справляется и с тем и с другим.
    private const int EventQueueCapacity = 256;

    private const int PipeBufferBytes = 64 * 1024;
    private const int AcceptRetryDelayMs = 500;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IpcRequestDispatcher _dispatcher;
    private readonly WindowRegistry _windows;
    private readonly MacroGraphStore _macros;
    private readonly MacroRunRegistry _runs;
    private readonly RunEventPublisher _runEvents;
    private readonly MacroDebugSession _debug;
    private readonly ILogger<IpcServer> _logger;

    private readonly ConcurrentDictionary<ClientConnection, byte> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _subscriptionLock = new();

    private bool _subscribed;
    private Task? _acceptLoop;
    private int _disposed;

    public IpcServer(
        IpcRequestDispatcher dispatcher,
        WindowRegistry windows,
        MacroGraphStore macros,
        MacroRunRegistry runs,
        RunEventPublisher runEvents,
        MacroDebugSession debug,
        ILogger<IpcServer> logger)
    {
        _dispatcher = dispatcher;
        _windows = windows;
        _macros = macros;
        _runs = runs;
        _runEvents = runEvents;
        _debug = debug;
        _logger = logger;

        // Вручаем себя диспетчеру, чтобы у RequestActivate было через что рассылать. Делается
        // здесь, а не через DI, потому что зависимость по-настоящему циклическая
        // (сервер → диспетчер → сервер), и это тот её конец, который другой объект уже держит.
        dispatcher.AttachBroadcaster(this);
        // Тот же цикл, то же решение: насос событий прогона толкает через нас, а мы держим его,
        // чтобы каждое соединение могло щёлкать своей подпиской.
        runEvents.AttachBroadcaster(this);
    }

    /// <summary>Сколько клиентов подключено прямо сейчас. Диагностика и тесты.</summary>
    public int ConnectionCount => _clients.Count;

    // ------------------------------------------------- жизненный цикл размещённой службы

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeToEngine();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token), CancellationToken.None);
        LogListening(PipeName, MaxServerInstances);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeFromEngine();
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_acceptLoop is { } loop)
        {
            // Цикл стоит в WaitForConnectionAsync; отмена его разблокирует. Таймаут — это
            // подстраховка на случай трубы, которая отменяться отказывается.
            try
            {
                await loop.WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                LogAcceptLoopDidNotStop();
            }

            _acceptLoop = null;
        }

        // Каждое живое соединение отключается, а это закрывает его трубу — тот самый сигнал,
        // который клиент панели превращает в «служба остановлена» (и в выход), а не в молчаливое
        // зависание.
        foreach (var client in _clients.Keys)
        {
            client.Drop();
        }

        await WaitForClientsAsync(cancellationToken).ConfigureAwait(false);
        LogStopped();
    }

    /// <summary>
    /// Подводит события изменений движка к рассылке. Идемпотентно.
    ///
    /// Публичный и отдельный от <see cref="StartAsync"/>, потому что протокольным тестам нужна
    /// проводка событий БЕЗ named pipe: они подписываются, гоняют соединение поверх потоков в
    /// памяти и правят реестр напрямую.
    /// </summary>
    public void SubscribeToEngine()
    {
        lock (_subscriptionLock)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            _windows.WindowAppeared += OnWindowAppeared;
            _windows.WindowTagsChanged += OnWindowTagsChanged;
            _windows.WindowClosed += OnWindowClosed;
            _macros.MacrosChanged += OnMacrosChanged;
            _runs.RunsChanged += OnRunsChanged;
        }
    }

    /// <summary>Отцепляет все подписки на движок. Идемпотентно.</summary>
    public void UnsubscribeFromEngine()
    {
        lock (_subscriptionLock)
        {
            if (!_subscribed)
            {
                return;
            }

            _subscribed = false;
            _windows.WindowAppeared -= OnWindowAppeared;
            _windows.WindowTagsChanged -= OnWindowTagsChanged;
            _windows.WindowClosed -= OnWindowClosed;
            _macros.MacrosChanged -= OnMacrosChanged;
            _runs.RunsChanged -= OnRunsChanged;
        }
    }

    // ------------------------------------------------------------------- цикл приёма

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }
            catch (Exception ex)
            {
                // Обычно это «все экземпляры трубы заняты» (упёрлись в MaxServerInstances) —
                // отступаем, пока не освободится слот. Всё прочее (беда с ACL, коллизия имён со
                // вторым демоном, обошедшим мьютекс) приземляется сюда же и заслуживает строки
                // в логе.
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                LogAcceptFailed(ex, PipeName);
                try
                {
                    await Task.Delay(AcceptRetryDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            var accepted = pipe;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ServeConnectionAsync(accepted, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogConnectionFaulted(ex);
                    }
                    finally
                    {
                        await accepted.DisposeAsync().ConfigureAwait(false);
                    }
                },
                CancellationToken.None);
        }
    }

    private static NamedPipeServerStream CreatePipe() =>
        NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: PipeBufferBytes,
            outBufferSize: PipeBufferBytes,
            CreatePipeSecurity());

    /// <summary>
    /// Одна ACE: пользователь, под которым работает демон, получает полный доступ. Больше
    /// говорить с этой трубой не нужно никому — ни администраторам, ни SYSTEM.
    ///
    /// Это намеренно простой случай, потому что сегодня ОБА процесса работают с повышением от
    /// одного и того же интерактивного пользователя (пункт ⚠ СОМНИТЕЛЬНО про повышение в плане
    /// разделения). Если UI когда-нибудь лишат повышения, DACL ниже по-прежнему подойдёт — SID
    /// пользователя одинаков и в отфильтрованном, и в полном токене, — но труба ВДОБАВОК
    /// унаследует у демона высокую метку обязательной целостности, а обязательная политика
    /// Windows не даст клиенту средней целостности открыть её на запись. Чинить это — значит
    /// добавить SACL с ACE <c>SYSTEM_MANDATORY_LABEL</c> уровня Medium (или Low) и, что важнее,
    /// решить, что менее привилегированному собеседнику всё ещё позволено управлять
    /// повышенными игровыми окнами. Это решение про безопасность, а не про сантехнику, поэтому
    /// заранее оно здесь не принимается.
    /// </summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        // identity.User бывает null только у экзотических токенов (анонимный, без SID
        // пользователя); имя учётной записи — запасной вариант, который ОС ещё способна
        // разрешить.
        IdentityReference user = (IdentityReference?)identity.User ?? new NTAccount(identity.Name);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // --------------------------------------------------------------- одно соединение

    /// <summary>
    /// Обслуживает один уже подключённый дуплексный поток, пока собеседник не отключится, поток
    /// не оборвётся или сервер не остановится. При сбое на стороне собеседника не бросает
    /// никогда.
    /// </summary>
    /// <param name="duplex">Поток, из которого и читают, и в который пишут.</param>
    /// <param name="cancellationToken">Срабатывает, когда соединение или сервер закрываются.</param>
    public Task ServeConnectionAsync(Stream duplex, CancellationToken cancellationToken = default) =>
        ServeConnectionAsync(duplex, duplex, cancellationToken);

    /// <summary>
    /// Перегрузка с раздельными потоками — та форма, которую имеет пара потоков в памяти в
    /// тестах (и которая понадобилась бы паре анонимных труб). <paramref name="input"/> и
    /// <paramref name="output"/> остаются открытыми; ими владеет вызывающий.
    /// </summary>
    /// <param name="input">Поток, по которому приходят строки собеседника.</param>
    /// <param name="output">Поток, в который уходят наши строки.</param>
    /// <param name="cancellationToken">Срабатывает, когда соединение или сервер закрываются.</param>
    public async Task ServeConnectionAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var client = new ClientConnection(new IpcConnection(input, output, leaveOpen: true), _runEvents, _debug);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token,
            client.Cts.Token);

        _clients.TryAdd(client, 0);
        LogClientConnected(_clients.Count);

        // Запускаем до цикла чтения, чтобы событие, поднятое, пока разбирается первый запрос,
        // попало в очередь, а не пропало.
        var pump = PumpEventsAsync(client, linked.Token);
        try
        {
            await ReadLoopAsync(client, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(client, out _);
            // Прежде всего прочего: клиент, умерший, не отписавшись, не имеет права оставить
            // исполнителя под съёмом показаний до конца жизни демона.
            client.SetRunEventSubscription(false);
            client.CompleteEvents();
            // Насос может стоять в записи в трубу, которую никто не читает; разблокирует его
            // именно отмена, а WhenAny страхует случай, когда даже она не помогла.
            client.Drop();
            await Task.WhenAny(pump, Task.Delay(DrainTimeout, CancellationToken.None)).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            LogClientDisconnected(_clients.Count);
        }
    }

    private async Task ReadLoopAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        // Завершённые обработчики выпалываются на каждой итерации, так что на долгоживущем
        // соединении список остаётся маленьким и при этом путь сноса всё ещё может дождаться
        // настоящей незаконченной работы.
        var inFlight = new List<Task>();

        while (!cancellationToken.IsCancellationRequested)
        {
            IpcRequest? request;
            try
            {
                request = await client.Connection.ReadAsync<IpcRequest>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // Одна неразбираемая строка ещё не сломанное соединение: читатель уже стоит на
                // следующей. Пропускаем её и продолжаем обслуживать.
                LogMalformedLine(ex);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                LogReadFailed(ex);
                break;
            }

            if (request is null)
            {
                break; // чистый EOF — собеседник закрыл свою половину на запись
            }

            inFlight.RemoveAll(static task => task.IsCompleted);
            inFlight.Add(HandleRequestAsync(client, request, cancellationToken));
        }

        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // HandleRequestAsync проглатывает всё; это лишь страховка от будущей правки в нём,
            // которая превратила бы снос соединения в падение из-за неперехваченного
            // исключения.
        }
    }

    private async Task HandleRequestAsync(ClientConnection client, IpcRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _dispatcher.DispatchAsync(request, client, cancellationToken).ConfigureAwait(false);
            await client.Connection.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Соединение или сервер уходят; этого ответа никто не ждёт.
        }
        catch (Exception ex)
        {
            LogResponseWriteFailed(ex, request.Type, request.Id);
            client.Drop();
        }
    }

    private async Task PumpEventsAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in client.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await client.Connection.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogEventWriteFailed(ex);
            client.Drop();
        }
    }

    // ---------------------------------------------------------------------- рассылка

    /// <summary>
    /// Ставит <paramref name="evt"/> в очередь каждому живому соединению. Не блокирует —
    /// вызывать с потока движка безопасно. Соединение с заполненной очередью отключается.
    /// </summary>
    public void Broadcast(IpcEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_clients.IsEmpty)
        {
            return;
        }

        foreach (var client in _clients.Keys)
        {
            Deliver(client, evt);
        }
    }

    /// <summary>
    /// Ставит <paramref name="evt"/> в очередь тем соединениям, которые просили поток событий
    /// прогона, и никаким другим.
    ///
    /// Фильтр здесь и есть смысл, а не оптимизация: <c>RunEvents</c> — единственное событие в
    /// протоколе, чей естественный темп способен обогнать очередь соединения, а расплата за
    /// заполненную очередь — отключение. Вторая панель (или отладочная консоль), которая ни на
    /// что не подписывалась, не должна попадать под эту раздачу.
    /// </summary>
    public void BroadcastToRunSubscribers(IpcEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_clients.IsEmpty)
        {
            return;
        }

        foreach (var client in _clients.Keys)
        {
            if (client.WantsRunEvents)
            {
                Deliver(client, evt);
            }
        }
    }

    private void Deliver(ClientConnection client, IpcEvent evt)
    {
        if (client.TryEnqueue(evt))
        {
            return;
        }

        LogClientBacklogged(evt.Type, EventQueueCapacity);
        client.Drop();
    }

    // Построение нагрузки отложено, чтобы демон, работающий без подключённой панели, не
    // сериализовал WindowDto на каждый тег, который проставляет макрос.
    private void Broadcast(string type, Func<JsonElement?>? payload = null)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        Broadcast(new IpcEvent(type, payload?.Invoke()));
    }

    private void OnWindowAppeared(ManagedWindowInfo window) =>
        Broadcast(IpcMessageTypes.WindowAppeared, () => IpcJson.Write(window.ToDto()));

    private void OnWindowTagsChanged(ManagedWindowInfo window) =>
        Broadcast(IpcMessageTypes.WindowTagsChanged, () => IpcJson.Write(window.ToDto()));

    // Окна уже нет, так что описывать, кроме дескриптора, нечего.
    private void OnWindowClosed(ManagedWindowInfo window) =>
        Broadcast(IpcMessageTypes.WindowClosed, () => IpcJson.Write(new WindowClosedEvent(window.Hwnd.ToInt64())));

    // По протоколу без нагрузки: библиотека бывает большой, а клиент дотянет её сам через
    // GetMacros.
    private void OnMacrosChanged(IReadOnlyList<MacroGraph> macros) =>
        Broadcast(IpcMessageTypes.MacrosChanged);

    // Список прогонов маленький, а UI он нужен немедленно, поэтому это событие состояние всё же
    // несёт.
    private void OnRunsChanged() =>
        Broadcast(IpcMessageTypes.RunningMacrosChanged, () => IpcJson.Write(_runs.Snapshot().ToDto()));

    // У IpcMessageTypes.ActivateWindow два производителя, оба вне этого класса и оба идущие
    // через публичную перегрузку Broadcast(IpcEvent) по IIpcBroadcaster: пункт трея «Открыть
    // панель», когда запущенная им панель ещё жива, и обработчик RequestActivate в диспетчере
    // (второй запуск UI просит первый выйти вперёд).

    // ---------------------------------------------------------------------------- снос

    private async Task WaitForClientsAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)DrainTimeout.TotalMilliseconds;
        while (!_clients.IsEmpty && Environment.TickCount64 < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
        }

        if (!_clients.IsEmpty)
        {
            LogClientsDidNotDrain(_clients.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        UnsubscribeFromEngine();
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var client in _clients.Keys)
        {
            client.Drop();
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// Один подключённый клиент: кадрированный поток плюс собственная очередь событий,
    /// собственный источник отмены и собственное состояние подписки. Отключение клиента
    /// отменяет только его источник, и именно поэтому один мёртвый собеседник не утащит за
    /// собой ни цикл приёма, ни своих соседей.
    /// </summary>
    private sealed class ClientConnection : IAsyncDisposable, IIpcSession
    {
        private readonly Channel<IpcEvent> _events = Channel.CreateBounded<IpcEvent>(
            new BoundedChannelOptions(EventQueueCapacity)
            {
                // Режим Wait при вызывающем, который пользуется TryWrite: на заполненной
                // очереди TryWrite вернёт false, а не заблокирует поток движка, поднявший
                // событие, и вызывающий превратит это в «отключить соединение».
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });

        private readonly RunEventPublisher _runEvents;
        private readonly MacroDebugSession _debug;
        private readonly Lock _subscriptionLock = new();
        private bool _wantsRunEvents;

        public ClientConnection(IpcConnection connection, RunEventPublisher runEvents, MacroDebugSession debug)
        {
            Connection = connection;
            _runEvents = runEvents;
            _debug = debug;
        }

        public IpcConnection Connection { get; }

        public CancellationTokenSource Cts { get; } = new();

        public ChannelReader<IpcEvent> Events => _events.Reader;

        /// <inheritdoc />
        public bool WantsRunEvents
        {
            get
            {
                lock (_subscriptionLock)
                {
                    return _wantsRunEvents;
                }
            }
        }

        /// <inheritdoc />
        public void SetRunEventSubscription(bool enabled)
        {
            // Под блокировкой и по фронту: публикатор запирает всего исполнителя на СЧЁТЧИКЕ
            // подписчиков, так что двойная подписка (или отключение, наперегонки с явной
            // отпиской) с утечкой одной ссылки оставила бы движок под съёмом показаний, хотя
            // смотреть уже некому.
            lock (_subscriptionLock)
            {
                if (_wantsRunEvents == enabled)
                {
                    return;
                }

                _wantsRunEvents = enabled;
            }

            // Счёт подключённых ОТЛАДЧИКОВ едет на этом же фронте, и это сделано намеренно.
            // Соединение, которое смотрит события прогона, — это ровно то соединение, которое
            // способно увидеть обход на паузе и нажать «продолжить», так что два времени жизни
            // здесь суть одно время жизни. Связав их, мы и получаем, что «последняя панель
            // ушла» распускает каждый припаркованный обход — через путь отключения ниже,
            // который и так вызывает это с false.
            if (enabled)
            {
                _runEvents.Acquire();
                _debug.Acquire();
            }
            else
            {
                _runEvents.Release();
                _debug.Release();
            }
        }

        public bool TryEnqueue(IpcEvent evt) => _events.Writer.TryWrite(evt);

        public void CompleteEvents() => _events.Writer.TryComplete();

        public void Drop()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Уже снесено — отключить дважды это норма (сломанную трубу могут заметить и
                // насос, и цикл чтения).
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            Cts.Dispose();
        }
    }
}
