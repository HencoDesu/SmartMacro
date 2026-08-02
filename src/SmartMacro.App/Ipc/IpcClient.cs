using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Ipc;

/// <summary>
/// The client half of the control protocol: one <see cref="IpcConnection"/> to the daemon's
/// named pipe, kept alive for the lifetime of the panel.
///
/// <b>Correlation, not ordering.</b> The server does not await a handler before reading the
/// next request, so <c>StopMacro</c> (which waits for a runner to acknowledge) and
/// <c>DumpCaptures</c> (which screenshots every window) come back whenever they come back —
/// possibly long after requests sent later. Every reply is therefore matched by
/// <see cref="IpcRequest.Id"/> against <see cref="_pending"/>, and nothing in this class
/// assumes replies arrive in order.
///
/// <b>One reader, many writers.</b> A single loop owns the read side and demultiplexes:
/// a line with an id completes a pending request, a line without one is an unsolicited
/// event. Writes come from arbitrary UI threads and are serialised by
/// <see cref="IpcConnection"/> itself.
///
/// <b>Reconnection is expected, not exceptional.</b> The daemon drops a client that stops
/// draining events, and the user may restart it. The maintain loop therefore reconnects for
/// the whole life of the process with a 250 ms → 2 s backoff, and raises
/// <see cref="Connected"/> every time it succeeds — which is what tells the view-models to
/// re-fetch, because everything they missed while disconnected is simply gone.
/// </summary>
public sealed class IpcClient : IIpcClient
{
    /// <summary>
    /// Opens one framed connection to the daemon. Injected — and returning an
    /// <see cref="IpcConnection"/> rather than a <see cref="Stream"/> — so the protocol
    /// tests can run the real client over in-memory halves, which are two streams rather
    /// than one duplex object.
    /// </summary>
    public delegate Task<IpcConnection> ConnectionFactory(CancellationToken cancellationToken);

    /// <summary>Default per-request patience. Generous: the pipe is local and the daemon is not busy.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private const int MinBackoffMs = 250;
    private const int MaxBackoffMs = 2000;

    // Per-attempt pipe timeout. Short, because failing fast just feeds the backoff loop.
    private const int PipeConnectTimeoutMs = 1000;

    private readonly ConnectionFactory _factory;
    private readonly TimeSpan _defaultTimeout;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>> _pending = new();
    private readonly CancellationTokenSource _stopping = new();

    // Not `volatile`: DropAsync swaps it with Interlocked, and C# forbids passing a volatile
    // field by ref. Reference reads are atomic and the Interlocked write publishes.
    private IpcConnection? _connection;
    private Task? _maintainLoop;
    private int _nextId;
    private int _disposed;

    /// <param name="factory">Transport opener; defaults to the daemon's named pipe.</param>
    /// <param name="defaultTimeout">Per-request timeout when a call doesn't override it.</param>
    /// <param name="logger">Diagnostics sink; defaults to the ambient Serilog logger.</param>
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
    /// Connects, then keeps the connection up until disposal.
    /// </summary>
    /// <param name="initialConnectWindow">
    /// How long to keep retrying the FIRST connect. The daemon may have just been launched
    /// by us and still be building its composition root, so this is seconds, not milliseconds.
    /// </param>
    /// <returns>
    /// <c>false</c> when the daemon never answered within the window — the caller shows an
    /// error and exits. Once this returns <c>true</c> a later drop is handled internally.
    /// </returns>
    public async Task<bool> StartAsync(TimeSpan initialConnectWindow, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var deadline = Environment.TickCount64 + (long)initialConnectWindow.TotalMilliseconds;

        while (!linked.IsCancellationRequested)
        {
            if (await TryConnectAsync(linked.Token).ConfigureAwait(false))
            {
                // The maintain loop takes over from here: it starts the reader for the
                // connection we just made, and owns every reconnect after it.
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
            // Bounded: the loop may be parked in a read that the cancellation should unblock,
            // but a pipe that refuses to cancel must not hold up process exit.
            await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
            _maintainLoop = null;
        }

        // notify: false — this is the UI closing itself, not the daemon disappearing, and
        // the Disconnected handler's job is to tell the user the daemon died.
        await DropAsync(_connection, notify: false).ConfigureAwait(false);
        _stopping.Dispose();
    }

    // ------------------------------------------------------------------ request plumbing

    private async Task<IpcResponse> SendAsync(string type, object? payload, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var connection = _connection
                         ?? throw new IpcRequestException(type, "нет соединения с демоном");

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
                : throw new IpcRequestException(type, response.Error ?? "запрос отклонён без описания причины");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe broke under the write. The maintain loop will notice and reconnect.
            throw new IpcRequestException(type, "соединение с демоном разорвано", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // ------------------------------------------------------------------- connection loop

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

            // Raised off the reader thread so a handler that fires requests (which every
            // view-model does) can never delay the loop that has to read their replies.
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
            // Overwhelmingly "the daemon isn't listening yet" (TimeoutException from
            // NamedPipeClientStream). Debug, not Warning: the backoff loop retries this
            // several times a second while the daemon boots.
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
                // Recoverable by contract: the reader is already positioned at the next
                // line, so one malformed message is not a broken connection.
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
                return; // EOF — the daemon closed the pipe
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
                // A reply to a request that already timed out or was cancelled. Normal.
                _log.Debug("Ответ на неизвестный запрос #{Id} — проигнорирован", id);
            }
            return;
        }

        var evt = new IpcEvent(inbound.Type ?? string.Empty, inbound.Payload);
        try
        {
            // Inline, NOT posted: event order is meaningful (WindowAppeared before the
            // WindowTagsChanged that follows it), and offloading each one would scramble it.
            // Handlers are contractually non-blocking — they marshal to the UI thread.
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

        // Only the owner of THIS connection clears the field: a reconnect may already have
        // installed a newer one.
        Interlocked.CompareExchange(ref _connection, null, connection);
        await connection.DisposeAsync().ConfigureAwait(false);

        // Every in-flight request dies with the connection. Failing them explicitly beats
        // letting each one burn its own timeout — the UI learns immediately.
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(new IpcRequestException("(соединение)", "демон отключился"));
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
        // leaveOpen: false — disposing the connection closes the pipe, which is what the
        // reconnect path relies on.
        return new IpcConnection(pipe);
    }

    /// <summary>
    /// One inbound line, before we know what it is. The protocol has two daemon → client
    /// shapes on one stream and no discriminator field: an <see cref="IpcResponse"/> carries
    /// an <c>Id</c>, an <see cref="IpcEvent"/> never does. Reading into this permissive
    /// union and testing <see cref="Id"/> is what makes that decidable without peeking at
    /// raw JSON.
    /// </summary>
    private sealed record IpcInbound(int? Id, bool? Ok, string? Type, JsonElement? Payload, string? Error);
}
