using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Ipc;

/// <summary>
/// Журнал демона, выведенный в трубу: кольцо последних записей плюс пуш <c>LogEntries</c>
/// подписавшимся соединениям.
///
/// Это ВТОРАЯ подписка на русле, проложенном волной D3b, а не второй механизм. Три меры,
/// которыми <see cref="RunEventPublisher"/> защищается от затопления, применимы здесь один в
/// один и повторены намеренно — только по подписке, склейка в пачки, ограниченная очередь с
/// подсчётом потерь. Объём тут даже выше: <c>SmartMacro.GameWindows</c> крутится на
/// <c>Debug</c>, и одна активация окна игры пишет несколько строк, а очередь соединения в 256
/// записей ОТКЛЮЧАЕТ клиента, который её забил.
///
/// <b>Отличие от событий прогона ровно одно: кольцо.</b> У D3b буфера истории намеренно нет —
/// пока никто не подписан, съём выключен, записывать нечего, и буфер оказался бы протухшим. С
/// журналом всё наоборот: он пишется независимо от того, смотрит ли кто-нибудь, и «Лог»
/// открывают ровно затем, чтобы увидеть, что только что произошло. Поэтому
/// <see cref="Append"/> кладёт запись в кольцо ВСЕГДА, и панель, подключившаяся к работающему
/// демону, получает предысторию, а не пустой экран. Цена названа честно: отрисовка сообщения и
/// хранение <see cref="LogLimits.HistoryCapacity"/> записей платятся и тогда, когда панель
/// закрыта. Это третий проход по шаблону поверх двух (консоль и файл), которые демон делает и
/// так.
///
/// <b>Защита от рекурсии — конструктивная, а не аккуратность.</b> Сток, который отправляет
/// записи по IPC, живёт внутри процесса, который логирует; любая запись, сделанная на пути
/// отправки, породила бы новую. Три среза, по убыванию строгости:
///
///   1. <b>У этого класса нет логгера.</b> Ни поля <c>ILogger</c>, ни ссылки на Serilog — их
///      неоткуда взять, потому что в конструкторе их нет, и добавить их нельзя: логгер
///      конструируется ИЗ конвейера Serilog, в который этот объект уже вставлен стоком, то есть
///      попытка внедрить его сюда — это цикл в DI, который упадёт при сборке хоста. Путь
///      «запись → <see cref="Append"/> → кольцо + <c>TryWrite</c>» физически не содержит места,
///      откуда могла бы взяться вторая запись.
///   2. <b>Путь отправки идёт под подавлением.</b> Насос выставляет <see cref="Suppressed"/> на
///      время синхронной рассылки, а <see cref="Append"/> при взведённом флаге не делает ничего.
///      Это покрывает единственное место в существующем коде, которое ЛОГИРУЕТ на пути отправки,
///      — предупреждение <c>IpcServer</c> об отставшем клиенте, — и покрывает по построению:
///      рассылка по контракту синхронна и не блокирует, так что всё, что она успевает записать,
///      случается внутри области действия флага. Запись при этом не пропадает: консоль и файл
///      её получают, из ленты панели выпадает только та строка, которую породил сам показ ленты.
///   3. <b>Выдержка склейки — страховка от будущего.</b> Даже если однажды кто-то залогирует с
///      ДРУГОЙ задачи (например, насос событий конкретного соединения при обрыве трубы) и мимо
///      обоих срезов, петли не выйдет: очередь выгребается раз в
///      <see cref="FlushIntervalMs"/> и публикует то, что в ней лежало на момент выгребания.
///      Просочившаяся запись стоит одной лишней строки за окно склейки, а не лавины, —
///      обратная связь не может обогнать таймер.
/// </summary>
public sealed class LogEventPublisher : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Выдержка перед разбором очереди — она же окно склейки.
    ///
    /// Вдвое больше, чем у <see cref="RunEventPublisher"/>, и это не копипаста с опечаткой.
    /// Там 50 мс держат подтверждение шага отладчика в пределах «мгновенно»; здесь ничего
    /// подобного нет — ленту журнала читают, а не нажимают. Зато поток вдвое-втрое плотнее, и
    /// удвоение окна ровно вдвое режет число конвертов на самом шумном сообщении протокола.
    /// Вязкости на глаз это не даёт: строка появляется на экране в пределах одного-двух кадров
    /// после того, как демон её записал.
    /// </summary>
    private const int FlushIntervalMs = 100;

    /// <summary>
    /// Сколько записей буферизуем, прежде чем начать выбрасывать. Та же глубина, что у событий
    /// прогона: достаточно, чтобы пережить всплеск от разветвления на десять окон (каждое
    /// пробуждение и заморозка окна — строка на <c>Debug</c>), и достаточно мало, чтобы
    /// залипший насос не растил кучу демона без предела.
    /// </summary>
    private const int QueueCapacity = 4096;

    /// <summary>Записей в конверте. Потолок размера строки JSON, а не пропускной способности — насос крутится дальше.</summary>
    private const int MaxBatchSize = 400;

    /// <summary>
    /// Взведён, пока управление находится на пути отправки. <c>AsyncLocal</c>, а не
    /// <c>[ThreadStatic]</c>: рассылка сегодня синхронна, но флаг, который молча перестал бы
    /// покрывать путь после первого же <c>await</c> внутри неё, — это ровно та защита-по-
    /// -аккуратности, от которой здесь и уходим.
    /// </summary>
    private static readonly AsyncLocal<bool> Suppressed = new();

    private readonly Channel<LogEntryDto> _queue = Channel.CreateBounded<LogEntryDto>(
        new BoundedChannelOptions(QueueCapacity)
        {
            // Wait при вызывающем, который пользуется TryWrite: на заполненной очереди TryWrite
            // вернёт false, а не заблокирует пишущий поток, и мы превратим это в подсчитанную
            // потерю. Логирует в том числе и обход макроса, идущий между двумя сообщениями живой
            // игре, — ждать ему нельзя.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    // Кольцо. Обычная очередь под замком, а не что-нибудь без блокировок: пишут в неё все потоки
    // демона, но по одной короткой операции на запись журнала, и это на порядки реже, чем
    // происходит хоть что-нибудь, за что стоило бы бороться.
    private readonly Queue<LogEntryDto> _history = new(LogLimits.HistoryCapacity);
    private readonly Lock _historyLock = new();

    private IIpcBroadcaster? _broadcaster;
    private CancellationTokenSource? _stopping;
    private Task? _pump;
    private long _seq;
    private int _subscribers;
    private int _dropped;
    private int _disposed;

    /// <summary><c>true</c>, когда ленту просит хотя бы одно соединение.</summary>
    public bool IsEnabled => Volatile.Read(ref _subscribers) > 0;

    /// <summary>Сколько соединений подписано прямо сейчас. Диагностика и тесты.</summary>
    public int SubscriberCount => Volatile.Read(ref _subscribers);

    /// <summary>
    /// Подаёт рассылку. Вызывается один раз конструктором <see cref="IpcServer"/> — по той же
    /// причине, что и у <see cref="RunEventPublisher.AttachBroadcaster"/>: сервер → публикатор →
    /// сервер настоящий цикл, и это тот его конец, который другой объект уже держит.
    /// </summary>
    public void AttachBroadcaster(IIpcBroadcaster broadcaster) => _broadcaster = broadcaster;

    // ---------------------------------------------------------------------- приём записей

    /// <summary>
    /// Принимает одну запись журнала. Зовётся стоком Serilog в демоне со ЛЮБОГО потока и никогда
    /// не блокирует.
    ///
    /// Аргументы намеренно примитивные, без единого типа Serilog: <c>SmartMacro.Core</c>
    /// логирует через <c>Microsoft.Extensions.Logging</c> и ссылки на Serilog не имеет,
    /// а перекладывание <c>LogEvent</c> в эти пять значений — работа стока, который живёт
    /// в проекте демона, где Serilog и так есть.
    /// </summary>
    /// <param name="timestamp">Когда запись сделана.</param>
    /// <param name="level">Уровень.</param>
    /// <param name="source">Полный <c>SourceContext</c> или <c>null</c>.</param>
    /// <param name="message">Уже отрисованное сообщение.</param>
    /// <param name="exception">Исключение со стеком или <c>null</c>.</param>
    public void Append(
        DateTimeOffset timestamp,
        LogLevelDto level,
        string? source,
        string message,
        string? exception)
    {
        // Срез 2 защиты от рекурсии: запись, порождённая самим показом ленты, в ленту не
        // попадает. Ни в очередь, ни в кольцо — иначе она приехала бы следующей предысторией.
        // Консоль и файл её получают как обычно; это решение про ленту, а не про журнал.
        if (Suppressed.Value)
        {
            return;
        }

        var entry = new LogEntryDto(
            Interlocked.Increment(ref _seq),
            timestamp,
            level,
            source,
            message,
            exception);

        // В кольцо — ВСЕГДА, независимо от подписки. В этом весь смысл кольца: см. комментарий
        // к классу.
        lock (_historyLock)
        {
            if (_history.Count == LogLimits.HistoryCapacity)
            {
                _history.Dequeue();
            }

            _history.Enqueue(entry);
        }

        if (!IsEnabled)
        {
            return;
        }

        if (!_queue.Writer.TryWrite(entry))
        {
            // Никогда не блокировать, никогда не расти. Счёт уедет со следующей пачкой, чтобы
            // панель могла признаться в дыре.
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Последние записи кольца, от старой к новой, — ответ на <c>SubscribeLog</c>.</summary>
    public IReadOnlyList<LogEntryDto> History()
    {
        lock (_historyLock)
        {
            return [.. _history];
        }
    }

    // ---------------------------------------------------------------------------- подписки

    /// <summary>
    /// Ещё одно соединение хочет ленту. <see cref="IpcServer"/> спаривает это с
    /// <see cref="Release"/>, в том числе на пути отключения: клиент, умерший, не отписавшись,
    /// не имеет права навсегда оставить демон сериализующим свой журнал в никуда.
    /// </summary>
    public void Acquire() => Interlocked.Increment(ref _subscribers);

    /// <summary>Одним подписчиком меньше. Последний уходящий выгребает очередь.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _subscribers) > 0)
        {
            return;
        }

        // Оставшееся в очереди адресовано никому. Сохрани мы это — первой пачкой СЛЕДУЮЩЕГО
        // подписчика стал бы кусок прошлого, который он уже получил в предыстории (кольцо-то
        // заполнялось всё это время), то есть ровно двоение, от которого спасает Seq. Проще не
        // создавать его вовсе.
        while (_queue.Reader.TryRead(out _))
        {
        }

        Interlocked.Exchange(ref _dropped, 0);
    }

    // ------------------------------------------------------------------------------ насос

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
    /// Выгребает очередь пачками. Задержка <see cref="FlushIntervalMs"/> после ПЕРВОЙ записи и
    /// есть окно склейки: она позволяет всплеску, приходящему в ближайшие миллисекунды, уехать
    /// одним конвертом.
    ///
    /// Срочного сигнала, который эту выдержку обрывает, здесь нет — в отличие от
    /// <see cref="RunEventPublisher"/>, где он подтверждает нажатую кнопку отладчика. В ленте
    /// журнала подтверждать нечего.
    /// </summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var batch = new List<LogEntryDto>(MaxBatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(FlushIntervalMs, cancellationToken).ConfigureAwait(false);

                batch.Clear();
                while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
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
        catch (Exception)
        {
            // Проглатывается молча, и это единственное место в кодовой базе, где такое
            // оправдано: залогировать провал НАСОСА ЖУРНАЛА было бы либо бесполезно (запись
            // ушла бы в очередь, которую больше некому разбирать), либо, не будь среза 1,
            // рекурсией. Последствие названо честно: лента у панели замолкает до перезапуска
            // демона, а консоль и файл продолжают писать как ни в чём не бывало.
        }
    }

    private void Publish(List<LogEntryDto> batch, int dropped)
    {
        if (_broadcaster is not { } broadcaster)
        {
            return;
        }

        // Срез 2 защиты от рекурсии. Область действия прижата к самой рассылке: всё, что она
        // способна записать (сегодня это единственное предупреждение IpcServer об отставшем
        // клиенте), случается синхронно внутри неё.
        Suppressed.Value = true;
        try
        {
            broadcaster.BroadcastToLogSubscribers(new IpcEvent(
                IpcMessageTypes.LogEntries,
                IpcJson.Write(new LogEntryBatch([.. batch], dropped))));
        }
        finally
        {
            Suppressed.Value = false;
        }
    }
}
