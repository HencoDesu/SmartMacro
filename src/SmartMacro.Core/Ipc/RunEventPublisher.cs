using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Resources;

namespace SmartMacro.Ipc;

/// <summary>
/// Превращает понодовый прогресс <see cref="MacroExecutor"/> в пуш <c>RunEvents</c> и целиком
/// отвечает на вопрос «а что мешает затопить панель и вышибить её из трубы».
///
/// <b>Опасность.</b> Один хоткей способен начать обход, который разветвляется по окнам; каждый
/// обход проходит десяток нод; каждая нода — это два события. Несколько сотен событий за
/// несколько сотен миллисекунд против очереди <see cref="IpcServer"/> на 256 записей на
/// соединение, которая ОТКЛЮЧАЕТ клиента, если тот не поспевает. Без посредника самый
/// оживлённый момент прогона — это ровно тот момент, когда панель бы и исчезла.
///
/// <b>Три намеренные меры, по убыванию выигрыша:</b>
///
///   1. <b>Только по подписке.</b> Пока соединение не попросило (<c>SubscribeRunEvents</c>), не
///      производится ничего. <see cref="IsEnabled"/> — одно volatile-чтение, которое walker
///      делает на ноду; когда там false, нет ни замера времени, ни строки подробностей, ни DTO,
///      ни JSON. Демон резидентен, а панель нет, так что это обычное положение дел, и стоить
///      оно обязано ноль.
///   2. <b>Склейка.</b> События попадают в одну ограниченную очередь и уходят пачками, не чаще
///      одного конверта на <see cref="FlushIntervalMs"/>. Всплеск в 400 событий превращается в
///      горстку конвертов — на три порядка ниже того, что очередь соединения вообще заметит.
///   3. <b>Ограниченно, с потерями и честно об этом.</b> Если очередь всё-таки забилась,
///      <c>TryWrite</c> на потоке движка не проходит, событие считается и выбрасывается, а счёт
///      уезжает со следующей пачкой, чтобы панель могла сказать, что в логе дыра. Чего быть не
///      должно никогда — так это ожидания на движке: прогон макроса управляет живой игрой между
///      двумя сообщениями Win32.
///
/// <b>Список живых обходов.</b> <see cref="WalkStarted"/>/<see cref="WalkFinished"/> ведут
/// <see cref="LiveWalks"/> независимо от того, подписан кто-нибудь или нет, — это две операции
/// со словарём на прогон макроса, а не на ноду. Без него панель, подключившаяся посреди
/// прогона, не смогла бы даже узнать, что прогон есть, а <c>SubscribeRunEvents</c> было бы
/// нечем честно ответить.
/// </summary>
public sealed partial class RunEventPublisher : IMacroRunObserver, IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Выдержка перед разбором очереди. Задаёт и окно склейки, и худшее запаздывание подсветки
    /// на канве: 50 мс незаметны человеку, который смотрит, как загорается нода, и превращают
    /// самый суровый реальный всплеск в однозначное число конвертов.
    /// </summary>
    private const int FlushIntervalMs = 50;

    /// <summary>
    /// Сколько событий буферизуем, прежде чем начать выбрасывать. Достаточно глубоко для
    /// целого разветвления на десять окон (~500 событий) с запасом и достаточно мелко, чтобы
    /// залипший насос не растил кучу демона без предела.
    /// </summary>
    private const int QueueCapacity = 4096;

    /// <summary>Событий в конверте. Это потолок размера JSON на строку, а не пропускной способности — насос крутится дальше.</summary>
    private const int MaxBatchSize = 400;

    private readonly Channel<RunEventDto> _queue = Channel.CreateBounded<RunEventDto>(
        new BoundedChannelOptions(QueueCapacity)
        {
            // Wait + TryWrite: на заполненной очереди TryWrite вернёт false, а не заблокирует
            // поток движка, и мы превращаем это в подсчитанную потерю. DropOldest делал бы то
            // же молча, а это единственное поведение, которого у этого класса быть не должно.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly ConcurrentDictionary<Guid, RunWalkDto> _live = new();
    private readonly ILogger<RunEventPublisher> _logger;

    private IIpcBroadcaster? _broadcaster;
    private CancellationTokenSource? _stopping;
    private Task? _pump;
    private int _subscribers;
    private int _dropped;
    private int _disposed;

    /// <summary>
    /// Завершается срочной постановкой в очередь, чтобы оборвать выдержку склейки.
    ///
    /// Сигнал, а не флаг, потому что вариант с флагом работал только тогда, когда срочное
    /// событие оказывалось тем самым, которое РАЗБУДИЛО насос. На практике таким оно не бывает
    /// никогда: попаданию в точку останова в ту же миллисекунду предшествуют
    /// <c>WalkStarted</c> и <c>NodeEntered</c>, так что флаг, прочитанный один раз в начале
    /// цикла, к тому моменту уже был прочитан как «не срочно», и пауза всё равно оплачивала
    /// полные 50 мс. Замерено: 63 мс до, однозначные числа после.
    /// </summary>
    private volatile TaskCompletionSource _urgent = NewUrgentSignal();

    public RunEventPublisher(ILogger<RunEventPublisher> logger) => _logger = logger;

    /// <inheritdoc />
    public bool IsEnabled => Volatile.Read(ref _subscribers) > 0;

    /// <summary>Сколько соединений подписано прямо сейчас. Диагностика и тесты.</summary>
    public int SubscriberCount => Volatile.Read(ref _subscribers);

    /// <summary>
    /// Подаёт рассылку. Вызывается один раз конструктором <see cref="IpcServer"/> по той же
    /// причине, по которой <c>AttachBroadcaster</c> есть у диспетчера: сервер → публикатор →
    /// сервер — настоящий цикл, и это тот его конец, который другой объект уже держит.
    /// </summary>
    public void AttachBroadcaster(IIpcBroadcaster broadcaster) => _broadcaster = broadcaster;

    // ---------------------------------------------------------------------- подписки

    /// <summary>
    /// Ещё одно соединение хочет поток. <see cref="IpcServer"/> спаривает это с
    /// <see cref="Release"/>, в том числе на пути отключения: клиент, умерший, не отписавшись,
    /// не имеет права навсегда оставить движок под съёмом показаний.
    /// </summary>
    public void Acquire()
    {
        var count = Interlocked.Increment(ref _subscribers);
        if (count == 1)
        {
            LogTracingOn();
        }
    }

    /// <summary>Одним подписчиком меньше. Последний уходящий выгребает очередь.</summary>
    public void Release()
    {
        var count = Interlocked.Decrement(ref _subscribers);
        if (count > 0)
        {
            return;
        }

        // Всё, что осталось в очереди, адресовано никому, а сохрани мы это — первой пачкой
        // СЛЕДУЮЩЕГО подписчика стал бы всплеск истории того прогона, которого он и не видел.
        while (_queue.Reader.TryRead(out _))
        {
        }

        Interlocked.Exchange(ref _dropped, 0);
        LogTracingOff();
    }

    /// <summary>
    /// Обходы, идущие прямо сейчас, в порядке старта — ответ на <c>SubscribeRunEvents</c>. У
    /// каждой записи <c>FromStart = false</c> по построению: обход начался до того, как
    /// вызывающий стал слушать, и в его логе нод будет недоставать неизвестного числа начальных
    /// строк.
    /// </summary>
    public IReadOnlyList<RunWalkDto> LiveWalks() =>
        [.. _live.Values.OrderBy(walk => walk.StartedUtc).Select(walk => walk with { FromStart = false })];

    // -------------------------------------------------------------- IMacroRunObserver

    public void WalkStarted(MacroWalkStart walk)
    {
        var dto = new RunWalkDto(
            walk.WalkId,
            walk.RunId,
            walk.MacroName,
            walk.ContextWindow?.ToInt64() ?? 0,
            walk.Depth,
            DateTimeOffset.UtcNow,
            FromStart: true,
            walk.SubmacroId,
            walk.SubmacroName);

        // Записывается безусловно — см. комментарий к классу. Дёшево и ограничено числом
        // одновременных обходов, то есть числом игровых окон.
        _live[walk.WalkId] = dto;

        if (IsEnabled)
        {
            Enqueue(new RunEventDto(walk.WalkId, RunEventKind.WalkStarted, ElapsedMs: 0, Walk: dto));
        }
    }

    public void NodeEntered(Guid walkId, int elapsedMs, Guid nodeId, string nodeName) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.NodeEntered, elapsedMs, nodeId, NodeName: nodeName));

    public void NodeExited(Guid walkId, int elapsedMs, Guid nodeId, string nodeName, string outcome, string? detail,
        int durationMs) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.NodeExited, elapsedMs, nodeId, outcome, detail, durationMs,
            NodeName: nodeName));

    public void WalkFinished(Guid walkId, int elapsedMs, string outcome, string? detail)
    {
        _live.TryRemove(walkId, out _);
        if (IsEnabled)
        {
            Enqueue(new RunEventDto(walkId, RunEventKind.WalkFinished, elapsedMs, Outcome: outcome, Detail: detail));
        }
    }

    public void VariableSet(Guid walkId, int elapsedMs, string name, string value, Guid? nodeId, string? nodeName) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.VariableSet, elapsedMs, nodeId, Detail: value, Variable: name,
            NodeName: nodeName));

    /// <summary>
    /// Обход припарковался. <b>Сбрасывается немедленно</b>, в обход окна склейки: это
    /// подтверждение нажатия кнопки, а 50 мс выдержки поверх round trip по трубе — это разница
    /// между шагом, который ощущается мгновенным, и шагом, который ощущается залипшим.
    /// Освободить от склейки безопасно, потому что таких событий за сессию единицы: всплеск,
    /// ради укрощения которого этот класс и существует, — это трафик нод, и он не затронут.
    /// </summary>
    public void WalkPaused(Guid walkId, int elapsedMs, Guid nodeId, string nodeName, DebugPauseReason reason) =>
        Enqueue(
            new RunEventDto(
                walkId,
                reason == DebugPauseReason.Breakpoint ? RunEventKind.BreakpointHit : RunEventKind.Paused,
                elapsedMs,
                nodeId,
                Detail: Describe(reason),
                NodeName: nodeName),
            urgent: true);

    /// <inheritdoc cref="WalkPaused" />
    public void WalkResumed(Guid walkId, int elapsedMs, Guid nodeId, string nodeName) =>
        Enqueue(new RunEventDto(walkId, RunEventKind.Resumed, elapsedMs, nodeId, NodeName: nodeName), urgent: true);

    // По-русски, как и любой другой Detail: панель выводит это дословно в полосе лога и на
    // панели инструментов, а какая из четырёх причин сработала, знает только демон.
    private static string Describe(DebugPauseReason reason) => reason switch
    {
        DebugPauseReason.Breakpoint => Strings_Engine.Run_PauseReason_Breakpoint,
        DebugPauseReason.Step => Strings_Engine.Run_PauseReason_Step,
        DebugPauseReason.Cursor => Strings_Engine.Run_PauseReason_UntilCursor,
        _ => Strings_Engine.Run_PauseReason_Paused,
    };

    // ------------------------------------------------------------------------ насос

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_pump is { } pump)
        {
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            _pump = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping?.Dispose();
        _stopping = null;
    }

    /// <summary>
    /// Выгребает очередь пачками. Задержка <see cref="FlushIntervalMs"/> после ПЕРВОГО события
    /// и есть окно склейки: именно она позволяет всплеску, приходящему в ближайшие несколько
    /// миллисекунд, уехать одним конвертом, а не четырьмя сотнями.
    /// </summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var batch = new List<RunEventDto>(MaxBatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await DwellAsync(cancellationToken).ConfigureAwait(false);

                // Взводим заново ДО выгребания, чтобы срочная запись, побежавшая с нами
                // наперегонки, либо попала в ЭТУ пачку (она встала в очередь до выгребания),
                // либо подожгла новый сигнал и получила собственный немедленный сброс. Никогда
                // и то и другое, никогда ни то ни другое.
                _urgent = NewUrgentSignal();

                batch.Clear();
                while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var evt))
                {
                    batch.Add(evt);
                }

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (batch.Count == 0 && dropped == 0)
                {
                    continue;
                }

                Publish(batch, dropped);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Баг сериализации здесь не имеет права уронить хост: движок продолжает работать, а
            // панель просто перестаёт видеть лог.
            LogPumpFailed(ex);
        }
    }

    /// <summary>
    /// Окно склейки — обрывается в тот момент, когда в очередь встаёт событие отладчика.
    /// </summary>
    private async Task DwellAsync(CancellationToken cancellationToken)
    {
        var urgent = _urgent.Task;
        if (urgent.IsCompleted)
        {
            return;
        }

        // У задержки собственный токен, чтобы проигравший гонку отменялся, а не висел на
        // таймере — по одному такому на каждый цикл сброса за всю жизнь демона.
        using var dwell = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(urgent, Task.Delay(FlushIntervalMs, dwell.Token)).ConfigureAwait(false);
        await dwell.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource NewUrgentSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Publish(List<RunEventDto> batch, int dropped)
    {
        if (_broadcaster is not { } broadcaster)
        {
            return;
        }

        if (dropped > 0)
        {
            LogDropped(dropped);
        }

        broadcaster.BroadcastToRunSubscribers(new IpcEvent(
            IpcMessageTypes.RunEvents,
            IpcJson.Write(new RunEventBatch([.. batch], dropped))));
    }

    private void Enqueue(RunEventDto evt, bool urgent = false)
    {
        if (_queue.Writer.TryWrite(evt))
        {
            if (urgent)
            {
                // ПОСЛЕ записи, чтобы разбуженный сигналом насос гарантированно нашёл эту
                // запись на месте. (До записи был бы безопасный порядок для флага, читаемого
                // один раз в начале цикла; для сигнала, обрывающего выдержку, это порядок
                // задом наперёд.)
                _urgent.TrySetResult();
            }

            return;
        }

        // Никогда не блокировать, никогда не расти: вызывающий — это поток движка между двумя
        // вводами в игру.
        Interlocked.Increment(ref _dropped);
    }

    [LoggerMessage(LogLevel.Debug, "Съём событий прогона включён — клиент подписался")]
    partial void LogTracingOn();

    [LoggerMessage(LogLevel.Debug, "Съём событий прогона выключен — подписчиков не осталось")]
    partial void LogTracingOff();

    [LoggerMessage(LogLevel.Warning, "Очередь событий прогона переполнилась: выброшено событий {Dropped}")]
    partial void LogDropped(int dropped);

    [LoggerMessage(LogLevel.Error, "Насос событий прогона упал; поток мёртв до перезапуска демона")]
    partial void LogPumpFailed(Exception exception);
}
