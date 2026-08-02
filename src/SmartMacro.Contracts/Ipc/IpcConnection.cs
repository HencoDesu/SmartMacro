using System.Text;
using System.Text.Json;

namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// One JSON Lines conversation over a duplex byte stream: read a line → one envelope,
/// write one envelope → one line.
///
/// It takes <see cref="Stream"/>s, not a <see cref="System.IO.Pipes.NamedPipeServerStream"/>,
/// and that is the whole point — every protocol test in the suite drives a real
/// <see cref="IpcConnection"/> over in-memory halves, so correlation, framing and write
/// serialisation are covered without a pipe, a second process, or a desktop session.
///
/// Two invariants it owns:
///
///   * <b>One message per line.</b> <see cref="IpcJson.Options"/> never indents, and
///     System.Text.Json escapes control characters inside strings, so no payload can
///     smuggle a newline into the middle of a message.
///   * <b>Writes are serialised.</b> A connection has two independent writers — the
///     request/response loop and the event pump — and <see cref="StreamWriter"/> is not
///     thread-safe: two concurrent <c>WriteLineAsync</c> calls corrupt the buffer or throw.
///     Every write therefore goes through <see cref="_writeLock"/>. Reads need no such
///     guard: exactly one reader loop per connection.
///
/// Deliberately free of any dependency beyond Contracts (no logger, no Core types) so it
/// can move to <c>SmartMacro.Contracts</c> in stage 3 and be shared with the UI's client
/// instead of being reimplemented there.
/// </summary>
public sealed class IpcConnection : IAsyncDisposable
{
    // No BOM: a byte-order mark at the head of the first line would break the reader on the
    // other end, which parses lines as bare JSON.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    /// <param name="duplex">Stream that is both read from and written to (a named pipe).</param>
    /// <param name="leaveOpen">Leave <paramref name="duplex"/> open when this connection is disposed.</param>
    public IpcConnection(Stream duplex, bool leaveOpen = false)
        : this(duplex, duplex, leaveOpen)
    {
    }

    /// <param name="input">Stream the peer's lines arrive on.</param>
    /// <param name="output">Stream our lines go out on. May be the same object as <paramref name="input"/>.</param>
    /// <param name="leaveOpen">Leave both streams open when this connection is disposed.</param>
    public IpcConnection(Stream input, Stream output, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _reader = new StreamReader(input, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: leaveOpen);
        _writer = new StreamWriter(output, Utf8NoBom, bufferSize: 8192, leaveOpen: leaveOpen)
        {
            // "\n" rather than the platform default: one byte of terminator, and every
            // JSON Lines reader in existence accepts it (StreamReader.ReadLineAsync also
            // accepts "\r\n", so a peer using the Windows default still parses here).
            NewLine = "\n",
            // Flushing is explicit and happens inside the write lock — AutoFlush would
            // flush per Write call, which for WriteLine means two syscalls per message.
            AutoFlush = false,
        };
    }

    /// <summary>
    /// Reads the next message. Blank lines (some peers pad with them) are skipped.
    /// </summary>
    /// <typeparam name="T">
    /// <see cref="IpcRequest"/> on the server side, <see cref="IpcResponse"/> /
    /// <see cref="IpcEvent"/> on the client side.
    /// </typeparam>
    /// <returns>
    /// The parsed message, or <c>null</c> at end of stream (the peer closed its write half
    /// or the pipe broke cleanly). A peer that sends a literal <c>null</c> line is read as
    /// EOF too — a degenerate message nobody has a reason to send, and treating it as a
    /// hang-up is the safe reading.
    /// </returns>
    /// <exception cref="JsonException">
    /// The line is not valid JSON for <typeparamref name="T"/>. RECOVERABLE — the stream is
    /// still positioned at the start of the next line, so a reader loop should log and keep
    /// going rather than tear the connection down over one bad message.
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
    /// Serialises <paramref name="message"/> and writes it as one line, then flushes.
    /// Concurrent callers are serialised; the message is rendered to a string BEFORE the
    /// lock is taken so serialisation cost never widens the critical section.
    /// </summary>
    /// <exception cref="IOException">The peer is gone. The caller should drop the connection.</exception>
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
            // Best-effort flush of anything a cancelled write left buffered. A dead peer
            // throws here — IOException from the transport, InvalidOperationException if a
            // write was still in flight — and that is exactly the case where there is
            // nothing left to do. Disposal must not throw.
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        try
        {
            _reader.Dispose();
        }
        catch (Exception)
        {
        }

        _writeLock.Dispose();
    }
}
