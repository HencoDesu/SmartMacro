using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Hotkeys;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Macros.Validation;
using SmartMacro.Orchestration;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Ipc;

/// <summary>
/// The server half of the protocol: one <see cref="IpcRequest"/> in, one
/// <see cref="IpcResponse"/> out, every <see cref="IpcMessageTypes"/> request constant
/// wired to the engine service that already implements it.
///
/// Deliberately knows nothing about pipes, connections or framing — it is handed a parsed
/// envelope and hands back a parsed envelope, which is what makes the whole catalogue
/// testable as plain method calls. <see cref="IpcServer"/> owns everything else.
///
/// <b>It never throws across the wire.</b> An unknown <see cref="IpcRequest.Type"/>, a
/// missing payload, or a handler that blows up all come back as <c>Ok = false</c> with a
/// human-readable <see cref="IpcResponse.Error"/>; unexpected failures are additionally
/// logged with their stack. The one exception that IS allowed to propagate is cancellation
/// of the caller's own token — at that point the connection is going away and there is
/// nobody left to answer.
/// </summary>
public sealed partial class IpcRequestDispatcher
{
    // Grace period between answering Shutdown and asking the host to stop; see ShutdownAsync.
    private const int ShutdownGraceMs = 250;

    private readonly WindowRegistry _windows;
    private readonly MacroGraphStore _macros;
    private readonly MacroRunRegistry _runs;
    private readonly IMacroRunner _runner;
    private readonly IHotkeyRegistration _hotkeys;
    private readonly CaptureDumpService _captures;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly RunEventPublisher _runEvents;
    private readonly MacroDebugSession _debug;
    private readonly ILogger<IpcRequestDispatcher> _logger;

    // Set by IpcServer's constructor, not by DI — see AttachBroadcaster. Null in the
    // dispatcher tests, which drive the catalogue without a server; RequestActivate is the
    // only handler that needs it and it rejects politely when it is missing.
    private IIpcBroadcaster? _broadcaster;

    public IpcRequestDispatcher(
        WindowRegistry windows,
        MacroGraphStore macros,
        MacroRunRegistry runs,
        IMacroRunner runner,
        IHotkeyRegistration hotkeys,
        CaptureDumpService captures,
        IHostApplicationLifetime lifetime,
        RunEventPublisher runEvents,
        MacroDebugSession debug,
        ILogger<IpcRequestDispatcher> logger)
    {
        _windows = windows;
        _macros = macros;
        _runs = runs;
        _runner = runner;
        _hotkeys = hotkeys;
        _captures = captures;
        _lifetime = lifetime;
        _runEvents = runEvents;
        _debug = debug;
        _logger = logger;
    }

    /// <summary>
    /// Supplies the event fan-out that <c>RequestActivate</c> pushes through. Called once,
    /// by <see cref="IpcServer"/>'s constructor: the server is built FROM this dispatcher,
    /// so it cannot also be injected into it.
    /// </summary>
    public void AttachBroadcaster(IIpcBroadcaster broadcaster) => _broadcaster = broadcaster;

    /// <summary>
    /// Routes one request to its handler and produces the reply, with no connection behind
    /// it. Everything in the catalogue except <c>SubscribeRunEvents</c> is per-engine rather
    /// than per-client and works fine this way; that one handler rejects politely.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired; the connection is closing.</exception>
    public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default) =>
        DispatchAsync(request, session: null, cancellationToken);

    /// <summary>Routes one request on behalf of a particular connection.</summary>
    /// <param name="request">The parsed envelope.</param>
    /// <param name="session">
    /// Per-connection protocol state, or <c>null</c> when there is no connection (tests).
    /// See <see cref="IIpcSession"/> for why exactly one handler needs it.
    /// </param>
    /// <param name="cancellationToken">Fires when the connection is closing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired; the connection is closing.</exception>
    public async Task<IpcResponse> DispatchAsync(IpcRequest request, IIpcSession? session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await HandleAsync(request, session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IpcRequestRejectedException ex)
        {
            // Client-side protocol misuse (missing/garbled payload, unknown macro). Expected
            // enough not to deserve a stack trace, loud enough to deserve a line.
            LogRejected(request.Type, request.Id, ex.Message);
            return Fail(request, ex.Message);
        }
        catch (Exception ex)
        {
            // A handler bug or an engine failure. The client gets a message; we keep the stack.
            LogHandlerFailed(ex, request.Type, request.Id);
            return Fail(request, ex.Message);
        }
    }

    private async Task<IpcResponse> HandleAsync(IpcRequest request, IIpcSession? session, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            // ------------------------------------------------------------------ windows

            case IpcMessageTypes.GetWindows:
                return Ok(request, IpcJson.Write(_windows.Snapshot().ToDto()));

            case IpcMessageTypes.AddTag:
            {
                var payload = Require<AddTagRequest>(request);
                // false = unknown hwnd or duplicate tag. Both are no-ops rather than
                // errors: the UI works from a snapshot that is always slightly stale, and
                // "the window you tagged just died" is not a client bug. The registry logs it.
                _windows.AddTag((IntPtr)payload.Hwnd, payload.Tag);
                return Ok(request);
            }

            case IpcMessageTypes.RemoveTag:
            {
                var payload = Require<RemoveTagRequest>(request);
                _windows.RemoveTag((IntPtr)payload.Hwnd, payload.Tag);
                return Ok(request);
            }

            // ------------------------------------------------------------------- macros

            case IpcMessageTypes.RunMacro:
            {
                var payload = Require<RunMacroRequest>(request);
                // Per the catalogue, an unknown name FAILS the request rather than being a
                // silent no-op: the Run button in the editor must not look like it worked.
                // Everything after this point is fire-and-forget — a macro can run for
                // hours, so the reply means "started", not "finished".
                if (_macros.TryGet(payload.Name) is null)
                {
                    throw new IpcRequestRejectedException($"Макрос '{payload.Name}' не найден.");
                }
                _runner.RunMacro(payload.Name);
                return Ok(request);
            }

            case IpcMessageTypes.StopMacro:
            {
                var payload = Require<StopMacroRequest>(request);
                // Awaited: the reply means the runner has actually acknowledged the cancel,
                // which is what lets the UI clear the row without guessing. An unknown
                // (already finished) run completes immediately — documented as success.
                await _runs.StopAsync(payload.RunId).WaitAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request);
            }

            case IpcMessageTypes.GetRunningMacros:
                return Ok(request, IpcJson.Write(_runs.Snapshot().ToDto()));

            case IpcMessageTypes.GetMacros:
                // Materialised to an array so the polymorphic node converters see
                // MacroGraph[] rather than an interface-typed sequence.
                return Ok(request, IpcJson.Write<MacroGraph[]>([.. _macros.All]));

            case IpcMessageTypes.SaveMacro:
                return await SaveMacroAsync(request, cancellationToken).ConfigureAwait(false);

            case IpcMessageTypes.DeleteMacro:
            {
                var payload = Require<DeleteMacroRequest>(request);
                // false = no such file. A no-op by contract, not an error.
                await _macros.DeleteAsync(payload.Name, cancellationToken).ConfigureAwait(false);
                return Ok(request);
            }

            case IpcMessageTypes.SubscribeRunEvents:
            {
                var payload = Require<SubscribeRunEventsRequest>(request);
                var connection = session
                                 ?? throw new IpcRequestRejectedException("Подписка на события прогона возможна только по соединению.");
                connection.SetRunEventSubscription(payload.Enabled);

                // The live walks, so a panel that arrived mid-run knows a run exists at all.
                // Each is flagged FromStart = false: its leading node rows happened before
                // anyone was recording and cannot be reconstructed, and the panel is
                // required to say so rather than render the tail as a whole log. Turning the
                // subscription OFF answers with an empty list — there is nothing to follow.
                return Ok(request, IpcJson.Write(payload.Enabled
                    ? _runEvents.LiveWalks()
                    : Array.Empty<RunWalkDto>()));
            }

            // ----------------------------------------------------------------- debugger

            case IpcMessageTypes.SetBreakpoints:
            {
                var payload = Require<SetBreakpointsRequest>(request);
                // No "does this macro exist" check on purpose: a breakpoint can legitimately
                // be armed on an unsaved draft, and the set is keyed by name — the moment the
                // draft is saved under that name it starts biting.
                _debug.SetBreakpoints(payload.MacroName, payload.NodeIds ?? []);
                return Ok(request);
            }

            case IpcMessageTypes.GetBreakpoints:
                return Ok(request, IpcJson.Write<BreakpointSetDto[]>([.. _debug.Breakpoints()]));

            case IpcMessageTypes.DebugCommand:
            {
                var payload = Require<DebugCommandRequest>(request);
                // Rejecting an unattached caller is not pedantry: the attach count is what
                // guarantees a paused walk has someone able to release it, and a command from
                // a connection outside that count could park a walk nobody would ever unpark.
                if (session is not { WantsRunEvents: true })
                {
                    throw new IpcRequestRejectedException(
                        "Команды отладчика доступны только подписчику событий прогона.");
                }
                return Ok(request, IpcJson.Write(_debug.Command(payload.WalkId, payload.Command, payload.NodeId)));
            }

            // ------------------------------------------------------------------ hotkeys

            case IpcMessageTypes.SuspendHotkeys:
                await _hotkeys.SuspendAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request);

            case IpcMessageTypes.ResumeHotkeys:
                await _hotkeys.ResumeAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request);

            case IpcMessageTypes.GetHotkeyFailures:
                // Materialised to an array so the response is a JSON array even when the
                // implementation hands back an empty read-only list.
                return Ok(request, IpcJson.Write<HotkeyFailureDto[]>([.. _hotkeys.Failures ?? []]));

            // -------------------------------------------------------------- diagnostics

            case IpcMessageTypes.DumpCaptures:
            {
                var folder = await _captures.DumpAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request, IpcJson.Write(folder));
            }

            case IpcMessageTypes.Shutdown:
                return ShutdownAsync(request);

            // ---------------------------------------------------------------- lifecycle

            case IpcMessageTypes.RequestActivate:
            {
                // Broadcast, not "reply to the sender": the asker is a second UI launch
                // that is about to exit, and the panel that must come forward is a
                // DIFFERENT connection. Sending it to everyone costs nothing (there is
                // normally exactly one panel) and needs no client bookkeeping here.
                var broadcaster = _broadcaster
                                  ?? throw new IpcRequestRejectedException("Событие активации некому разослать.");
                broadcaster.Broadcast(new IpcEvent(IpcMessageTypes.ActivateWindow));
                return Ok(request);
            }

            default:
                LogUnknownType(request.Type, request.Id);
                return Fail(request, $"unknown request type: {request.Type}");
        }
    }

    /// <summary>
    /// Validate → reject-or-write. The response IS the issue list: empty means the graph
    /// was written, non-empty means it was refused and carries the reasons.
    /// </summary>
    private async Task<IpcResponse> SaveMacroAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = Require<SaveMacroRequest>(request);
        var graph = payload.Macro
                    ?? throw new IpcRequestRejectedException("SaveMacro payload has no macro.");

        var issues = new List<ValidationIssue>(MacroGraphValidator.Validate(graph));

        // The validator checks the GRAPH; the store checks the NAME (it is the file stem)
        // and throws on a bad one. Surfacing it as a graph-level issue instead lets the
        // editor render "имя содержит '/'" next to the structural errors rather than as an
        // opaque failed request.
        if (MacroGraphStore.ValidateName(graph.Name) is { } nameError)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, null, nameError));
        }

        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            // Warnings ride along with the errors — the editor may as well show everything
            // it is about to be asked to fix.
            LogSaveRejected(graph.Name, issues.Count(i => i.Severity == ValidationSeverity.Error));
            return Ok(request, IpcJson.Write(issues.ToDto()));
        }

        await _macros.SaveAsync(graph, cancellationToken).ConfigureAwait(false);
        // Empty list = written. Warnings are deliberately NOT reported on success: the
        // protocol gives this field one meaning (rejection reasons) and overloading it
        // would make every warning look like a failed save to the client.
        return Ok(request, IpcJson.Write(Array.Empty<ValidationIssueDto>()));
    }

    /// <summary>
    /// Answers first, stops second.
    ///
    /// <see cref="IpcServer"/> is registered last among the hosted services, so it is the
    /// FIRST to be torn down when the host stops — which would close this very connection
    /// out from under the reply if we called <c>StopApplication</c> inline. The stop is
    /// therefore detached and delayed by <see cref="ShutdownGraceMs"/>: the reply is written
    /// and flushed microseconds after this method returns, so the margin is enormous, and
    /// nothing about correctness depends on the exact number — a client that misses the
    /// reply just sees the pipe close, which is the same signal.
    /// </summary>
    private IpcResponse ShutdownAsync(IpcRequest request)
    {
        LogShutdownRequested();
        _ = Task.Run(async () =>
        {
            await Task.Delay(ShutdownGraceMs).ConfigureAwait(false);
            _lifetime.StopApplication();
        });
        return Ok(request);
    }

    private static IpcResponse Ok(IpcRequest request, System.Text.Json.JsonElement? payload = null) =>
        new(request.Id, Ok: true, payload);

    private static IpcResponse Fail(IpcRequest request, string error) =>
        new(request.Id, Ok: false, Payload: null, error);

    private static T Require<T>(IpcRequest request)
        where T : class =>
        IpcJson.Read<T>(request.Payload)
        ?? throw new IpcRequestRejectedException($"{request.Type} requires a payload.");
}

/// <summary>
/// A request the client got wrong (no payload, unknown macro name). Carries a message
/// meant for a human reading the UI, and is logged without a stack — unlike an unexpected
/// handler failure, there is no bug here to investigate.
/// </summary>
public sealed class IpcRequestRejectedException : Exception
{
    public IpcRequestRejectedException(string message) : base(message)
    {
    }

    public IpcRequestRejectedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public IpcRequestRejectedException()
    {
    }
}
