using System.Text;
using System.Text.Json;

namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// Один разговор в формате JSON Lines поверх дуплексного потока байт: прочитали строку — один
/// конверт, записали один конверт — одна строка.
///
/// Класс принимает <see cref="Stream"/>, а не
/// <see cref="System.IO.Pipes.NamedPipeServerStream"/>, и в этом весь смысл: каждый
/// протокольный тест в наборе гоняет настоящий <see cref="IpcConnection"/> поверх половинок
/// в памяти, так что корреляция, кадрирование и сериализация записи покрыты без named pipe,
/// без второго процесса и без сессии рабочего стола.
///
/// Два инварианта, за которые он отвечает:
///
///   * <b>Одно сообщение — одна строка.</b> <see cref="IpcJson.Options"/> никогда не ставит
///     отступов, а System.Text.Json экранирует управляющие символы внутри строк, так что ни
///     одна нагрузка не протащит перевод строки в середину сообщения.
///   * <b>Записи сериализованы.</b> У соединения два независимых писателя — цикл
///     «запрос/ответ» и насос событий, — а <see cref="StreamWriter"/> не потокобезопасен: два
///     одновременных <c>WriteLineAsync</c> портят буфер или бросают исключение. Поэтому любая
///     запись проходит через <see cref="_writeLock"/>. Чтению такая защита не нужна: цикл
///     чтения на соединение ровно один.
///
/// Намеренно не имеет ни одной зависимости за пределами Contracts — ни логгера, ни типов
/// Core. Ради этого класс сюда и переехал (стадия 3): обе стороны трубы используют одно и
/// то же обрамление, вместо того чтобы панель переписывала его у себя заново. Появится
/// зависимость на Core — переезд придётся отменять.
/// </summary>
public sealed class IpcConnection : IAsyncDisposable
{
    // Без BOM: метка порядка байтов в начале первой строки сломала бы читателя на том конце —
    // он разбирает строки как голый JSON.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    /// <param name="duplex">Поток, из которого и читают, и в который пишут (named pipe).</param>
    /// <param name="leaveOpen">Оставить <paramref name="duplex"/> открытым, когда это соединение освобождают.</param>
    public IpcConnection(Stream duplex, bool leaveOpen = false)
        : this(duplex, duplex, leaveOpen)
    {
    }

    /// <param name="input">Поток, по которому приходят строки собеседника.</param>
    /// <param name="output">Поток, в который уходят наши строки. Может быть тем же объектом, что и <paramref name="input"/>.</param>
    /// <param name="leaveOpen">Оставить оба потока открытыми, когда это соединение освобождают.</param>
    public IpcConnection(Stream input, Stream output, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _reader = new StreamReader(input, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 8192,
            leaveOpen: leaveOpen);
        _writer = new StreamWriter(output, Utf8NoBom, bufferSize: 8192, leaveOpen: leaveOpen)
        {
            // "\n", а не платформенное значение по умолчанию: один байт терминатора, и его
            // принимает любой существующий читатель JSON Lines (StreamReader.ReadLineAsync
            // принимает и "\r\n", так что собеседник с виндовым значением по умолчанию тоже
            // разберётся).
            NewLine = "\n",
            // Сброс буфера делаем явно и внутри блокировки записи: AutoFlush сбрасывал бы на
            // каждый вызов Write, а для WriteLine это два системных вызова на сообщение.
            AutoFlush = false,
        };
    }

    /// <summary>
    /// Читает следующее сообщение. Пустые строки (некоторые собеседники ими добивают) пропускаются.
    /// </summary>
    /// <typeparam name="T">
    /// <see cref="IpcRequest"/> на стороне сервера, <see cref="IpcResponse"/> /
    /// <see cref="IpcEvent"/> на стороне клиента.
    /// </typeparam>
    /// <returns>
    /// Разобранное сообщение или <c>null</c> в конце потока (собеседник закрыл свою половину
    /// на запись либо канал чисто оборвался). Собеседник, приславший строку с литеральным
    /// <c>null</c>, тоже читается как EOF: это вырожденное сообщение, которое ни у кого нет
    /// причин отправлять, и трактовать его как «повесили трубку» — безопасное прочтение.
    /// </returns>
    /// <exception cref="JsonException">
    /// Строка не является корректным JSON под <typeparamref name="T"/>. ВОССТАНОВИМО: поток
    /// по-прежнему стоит в начале следующей строки, поэтому цикл чтения должен записать это в
    /// лог и продолжить, а не рвать соединение из-за одного плохого сообщения.
    /// </exception>
    public async Task<T?> ReadAsync<T>(CancellationToken cancellationToken = default)
        where T : class
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return JsonSerializer.Deserialize<T>(line, IpcJson.Options);
        }
    }

    /// <summary>
    /// Сериализует <paramref name="message"/>, пишет его одной строкой и сбрасывает буфер.
    /// Одновременные вызовы выстраиваются в очередь; сообщение превращается в строку ДО взятия
    /// блокировки, чтобы стоимость сериализации никогда не расширяла критическую секцию.
    /// </summary>
    /// <exception cref="IOException">Собеседника больше нет. Вызывающему следует закрыть соединение.</exception>
    public async Task WriteAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(message, IpcJson.Options);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // По возможности сбрасываем всё, что отменённая запись оставила в буфере. Мёртвый
            // собеседник бросит здесь исключение — IOException из транспорта или
            // InvalidOperationException, если запись ещё шла, — и это ровно тот случай, когда
            // делать уже нечего. Освобождение не имеет права бросать.
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        // Перехват намеренно всеохватный, а не по списку типов: транспорт волен бросить что
        // угодно, а освобождение не имеет права бросать вообще ничего. Сузить catch — значит
        // однажды уронить путь очистки исключением, которого не было в списке.
        // ReSharper disable once EmptyGeneralCatchClause
        catch (Exception)
        {
        }

        try
        {
            _reader.Dispose();
        }
        // То же самое: см. выше.
        // ReSharper disable once EmptyGeneralCatchClause
        catch (Exception)
        {
        }

        _writeLock.Dispose();
    }
}
