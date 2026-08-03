using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

/// <summary>
/// Однонаправленный байтовый канал в памяти со <see cref="Stream"/> на каждом конце.
///
/// Он существует затем, чтобы ни один тест IPC никогда не трогал
/// <c>NamedPipeServerStream</c>: настоящему named pipe нужны имя (а значит, тесты не смогут
/// идти параллельно) и сеанс рабочего стола, да и разыграть на нём «собеседник умер прямо
/// посреди записи» неудобно. Здесь оба режима отказа — это один вызов метода:
/// <see cref="CompleteWriting"/> для чистого EOF и <see cref="Break"/> для разорванной трубы.
///
/// Записи намеренно проходят через <c>Task.Yield()</c>: поверх синхронного потока асинхронные
/// записи <see cref="StreamWriter"/> завершаются на месте, и два одновременных писателя никогда
/// по-настоящему не перемешались бы, — а это позволило бы отсутствующей блокировке записи в
/// <c>IpcConnection</c> пройти ровно тот тест, который написан её ловить.
/// </summary>
internal sealed class InMemoryPipe
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private byte[] _current = [];
    private int _offset;
    private volatile bool _broken;

    public InMemoryPipe()
    {
        ReadEnd = new PipeReadStream(this);
        WriteEnd = new PipeWriteStream(this);
    }

    /// <summary>Поток, из которого потребитель вычитывает байты.</summary>
    public Stream ReadEnd { get; }

    /// <summary>Поток, в который производитель байты пишет.</summary>
    public Stream WriteEnd { get; }

    /// <summary>
    /// Чистое закрытие: читатель увидит EOF, как только разберёт всё уже написанное.
    /// </summary>
    public void CompleteWriting() => _chunks.Writer.TryComplete();

    /// <summary>
    /// Собеседник сломался: последующие записи бросают <see cref="IOException"/>, читатель
    /// видит EOF.
    /// </summary>
    public void Break()
    {
        _broken = true;
        _chunks.Writer.TryComplete();
    }

    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        if (_broken)
        {
            throw new IOException("Канал разорван (тестовая имитация мёртвого пайпа).");
        }

        if (!_chunks.Writer.TryWrite(buffer.ToArray()))
        {
            throw new IOException("Канал закрыт.");
        }
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (_offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0; // конец потока
            }

            if (!_chunks.Reader.TryRead(out var next))
            {
                continue;
            }

            _current = next;
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    private sealed class PipeReadStream(InMemoryPipe pipe) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            pipe.ReadCoreAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            pipe.ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            pipe.ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter()
                .GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PipeWriteStream(InMemoryPipe pipe) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            // Уступка потока здесь и есть весь смысл — см. комментарий к классу.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            pipe.WriteCore(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            pipe.WriteCore(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// Две трубы <see cref="InMemoryPipe"/>, из которых складывается двусторонний разговор, плюс
/// удобства со стороны клиента, нужные тесту: отправить конверт, прочитать следующую строку,
/// вежливо попрощаться или умереть. СЕРВЕРНЫЕ половины — это то, что получает
/// <c>IpcServer.ServeConnectionAsync</c>.
/// </summary>
internal sealed class DuplexStreamPair : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly InMemoryPipe _clientToServer = new();
    private readonly InMemoryPipe _serverToClient = new();
    private readonly StreamReader _clientReader;
    private readonly StreamWriter _clientWriter;

    public DuplexStreamPair()
    {
        _clientReader = new StreamReader(_serverToClient.ReadEnd, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        _clientWriter = new StreamWriter(_clientToServer.WriteEnd, Utf8NoBom) { NewLine = "\n", AutoFlush = true };
    }

    /// <summary>Поток, из которого сервер читает запросы.</summary>
    public Stream ServerInput => _clientToServer.ReadEnd;

    /// <summary>Поток, в который сервер пишет ответы и события.</summary>
    public Stream ServerOutput => _serverToClient.WriteEnd;

    /// <summary>Отправляет один конверт строкой JSON.</summary>
    public Task SendAsync<T>(T message) => SendLineAsync(JsonSerializer.Serialize(message, IpcJson.Options));

    /// <summary>Отправляет сырую строку — этим серверу нарочно скармливают мусор.</summary>
    public async Task SendLineAsync(string line)
    {
        await _clientWriter.WriteLineAsync(line);
        await _clientWriter.FlushAsync();
    }

    /// <summary>
    /// Читает следующую выданную сервером строку — и роняет тест, а не виснет навсегда.
    /// </summary>
    public async Task<string> ReadLineAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var line = await _clientReader.ReadLineAsync(cts.Token);
        return line ?? throw new IOException("Сервер закрыл соединение — строки нет.");
    }

    /// <summary>Читает <paramref name="count"/> строк в порядке поступления.</summary>
    public async Task<List<string>> ReadLinesAsync(int count, int timeoutMs = 5000)
    {
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            lines.Add(await ReadLineAsync(timeoutMs));
        }

        return lines;
    }

    /// <summary>Вежливое прощание: читатель сервера видит EOF и сворачивается.</summary>
    public void CloseClient() => _clientToServer.CompleteWriting();

    /// <summary>
    /// Убивает только направление сервер→клиент: следующий ПУШ сервера бросит исключение, а его
    /// читатель останется стоять на месте. Это и есть случай «клиент умер, а никто ещё не
    /// заметил», который рассылка обязана пережить.
    /// </summary>
    public void BreakServerWrites() => _serverToClient.Break();

    public void Dispose()
    {
        _clientToServer.CompleteWriting();
        _serverToClient.CompleteWriting();
    }
}
