using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

/// <summary>
/// A one-directional in-memory byte channel with a <see cref="Stream"/> on each end.
///
/// This exists so no IPC test ever touches <c>NamedPipeServerStream</c>: a real pipe needs
/// a name (so tests can't run in parallel), a desktop session, and it makes "the peer just
/// died mid-write" awkward to stage. Here both failure modes are one method call —
/// <see cref="CompleteWriting"/> for a clean EOF, <see cref="Break"/> for a broken pipe.
///
/// Writes deliberately go through <c>Task.Yield()</c>: with a synchronous underlying
/// stream <see cref="StreamWriter"/>'s async writes complete inline, and two concurrent
/// writers would never actually interleave — which would let a missing write lock in
/// <c>IpcConnection</c> pass the very test written to catch it.
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

    /// <summary>Stream the consumer reads bytes out of.</summary>
    public Stream ReadEnd { get; }

    /// <summary>Stream the producer writes bytes into.</summary>
    public Stream WriteEnd { get; }

    /// <summary>Clean close: the reader sees EOF once everything already written is drained.</summary>
    public void CompleteWriting() => _chunks.Writer.TryComplete();

    /// <summary>Broken peer: subsequent writes throw <see cref="IOException"/>, the reader sees EOF.</summary>
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
                return 0; // EOF
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

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            pipe.ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            pipe.ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // The yield is the point — see the class comment.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            pipe.WriteCore(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => pipe.WriteCore(buffer.AsSpan(offset, count));

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
/// The two <see cref="InMemoryPipe"/>s that make a duplex conversation, plus the client-side
/// conveniences a test needs: send an envelope, read the next line, hang up, or die.
/// The SERVER halves are what <c>IpcServer.ServeConnectionAsync</c> is handed.
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

    /// <summary>Stream the server reads requests from.</summary>
    public Stream ServerInput => _clientToServer.ReadEnd;

    /// <summary>Stream the server writes responses and events to.</summary>
    public Stream ServerOutput => _serverToClient.WriteEnd;

    /// <summary>Sends one envelope as a JSON line.</summary>
    public Task SendAsync<T>(T message) => SendLineAsync(JsonSerializer.Serialize(message, IpcJson.Options));

    /// <summary>Sends a raw line — used to feed the server garbage on purpose.</summary>
    public async Task SendLineAsync(string line)
    {
        await _clientWriter.WriteLineAsync(line);
        await _clientWriter.FlushAsync();
    }

    /// <summary>Reads the next line the server produced, failing the test rather than hanging.</summary>
    public async Task<string> ReadLineAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var line = await _clientReader.ReadLineAsync(cts.Token);
        return line ?? throw new IOException("Сервер закрыл соединение — строки нет.");
    }

    /// <summary>Reads <paramref name="count"/> lines in arrival order.</summary>
    public async Task<List<string>> ReadLinesAsync(int count, int timeoutMs = 5000)
    {
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            lines.Add(await ReadLineAsync(timeoutMs));
        }
        return lines;
    }

    /// <summary>Polite hang-up: the server's reader sees EOF and unwinds.</summary>
    public void CloseClient() => _clientToServer.CompleteWriting();

    /// <summary>
    /// Kills only the server→client direction: the server's next PUSH throws, while its
    /// reader stays parked. That is the "client died and nobody noticed yet" case a
    /// broadcast has to survive.
    /// </summary>
    public void BreakServerWrites() => _serverToClient.Break();

    public void Dispose()
    {
        _clientToServer.CompleteWriting();
        _serverToClient.CompleteWriting();
    }
}
