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
/// Серверная половина протокола: на входе один <see cref="IpcRequest"/>, на выходе один
/// <see cref="IpcResponse"/>, и каждая константа запроса из <see cref="IpcMessageTypes"/>
/// подведена к той службе движка, которая её уже реализует.
///
/// Намеренно ничего не знает ни про трубы, ни про соединения, ни про кадрирование — ему дают
/// разобранный конверт, он отдаёт разобранный конверт, и благодаря этому весь каталог
/// проверяется обычными вызовами методов. Всем остальным владеет <see cref="IpcServer"/>.
///
/// <b>Он никогда не бросает через провод.</b> Неизвестный <see cref="IpcRequest.Type"/>,
/// отсутствующая нагрузка, взорвавшийся обработчик — всё возвращается как <c>Ok = false</c> с
/// человекочитаемым <see cref="IpcResponse.Error"/>; неожиданные сбои вдобавок пишутся в лог
/// вместе со стеком. Единственное исключение, которому ПОЗВОЛЕНО распространяться, — отмена по
/// собственному токену вызывающего: в этот момент соединение уже уходит и отвечать некому.
/// </summary>
public sealed partial class IpcRequestDispatcher
{
    // Отсрочка между ответом на Shutdown и просьбой к хосту остановиться; см. ShutdownAsync.
    private const int ShutdownGraceMs = 250;

    private readonly WindowRegistry _windows;
    private readonly MacroGraphStore _macros;
    private readonly MacroRunRegistry _runs;
    private readonly IMacroRunner _runner;
    private readonly IHotkeyRegistration _hotkeys;
    private readonly CaptureDumpService _captures;
    private readonly TemplateSetProvider _templates;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly RunEventPublisher _runEvents;
    private readonly MacroDebugSession _debug;
    private readonly ILogger<IpcRequestDispatcher> _logger;

    // Проставляется конструктором IpcServer, а не через DI, — см. AttachBroadcaster. Null в
    // тестах диспетчера, которые гоняют каталог без сервера; RequestActivate — единственный
    // обработчик, которому вещатель нужен, и при его отсутствии он вежливо отказывает.
    private IIpcBroadcaster? _broadcaster;

    public IpcRequestDispatcher(
        WindowRegistry windows,
        MacroGraphStore macros,
        MacroRunRegistry runs,
        IMacroRunner runner,
        IHotkeyRegistration hotkeys,
        CaptureDumpService captures,
        TemplateSetProvider templates,
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
        _templates = templates;
        _lifetime = lifetime;
        _runEvents = runEvents;
        _debug = debug;
        _logger = logger;
    }

    /// <summary>
    /// Подаёт ту рассылку событий, через которую толкается <c>RequestActivate</c>. Вызывается
    /// один раз, конструктором <see cref="IpcServer"/>: сервер строится ИЗ этого диспетчера, а
    /// значит, не может быть заодно и внедрён в него.
    /// </summary>
    public void AttachBroadcaster(IIpcBroadcaster broadcaster) => _broadcaster = broadcaster;

    /// <summary>
    /// Направляет один запрос его обработчику и порождает ответ — без соединения за спиной. Всё
    /// в каталоге, кроме <c>SubscribeRunEvents</c> и <c>DebugCommand</c>, относится к движку, а
    /// не к клиенту, и в таком виде работает прекрасно; эти двое вежливо отказывают.
    /// </summary>
    /// <param name="request">Разобранный конверт.</param>
    /// <param name="cancellationToken">Срабатывает, когда соединение закрывается.</param>
    /// <exception cref="OperationCanceledException">Сработал <paramref name="cancellationToken"/>; соединение закрывается.</exception>
    public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default) =>
        DispatchAsync(request, session: null, cancellationToken);

    /// <summary>Направляет один запрос от имени конкретного соединения.</summary>
    /// <param name="request">Разобранный конверт.</param>
    /// <param name="session">
    /// Состояние протокола, привязанное к соединению, или <c>null</c>, когда соединения нет
    /// (тесты). Зачем оно понадобилось двум обработчикам, см. <see cref="IIpcSession"/>.
    /// </param>
    /// <param name="cancellationToken">Срабатывает, когда соединение закрывается.</param>
    /// <exception cref="OperationCanceledException">Сработал <paramref name="cancellationToken"/>; соединение закрывается.</exception>
    public async Task<IpcResponse> DispatchAsync(IpcRequest request, IIpcSession? session,
        CancellationToken cancellationToken = default)
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
            // Клиент нарушил протокол (нет нагрузки, нагрузка исковеркана, неизвестный
            // макрос). Достаточно ожидаемо, чтобы не заслуживать стека, и достаточно громко,
            // чтобы заслуживать строчки в логе.
            LogRejected(request.Type, request.Id, ex.Message);
            return Fail(request, ex.Message);
        }
        catch (Exception ex)
        {
            // Баг в обработчике или сбой движка. Клиенту достаётся сообщение, нам — стек.
            LogHandlerFailed(ex, request.Type, request.Id);
            return Fail(request, ex.Message);
        }
    }

    private async Task<IpcResponse> HandleAsync(IpcRequest request, IIpcSession? session,
        CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            // -------------------------------------------------------------------- окна

            case IpcMessageTypes.GetWindows:
                return Ok(request, IpcJson.Write(_windows.Snapshot().ToDto()));

            case IpcMessageTypes.AddTag:
            {
                var payload = Require<AddTagRequest>(request);
                // false = неизвестный hwnd либо повторный тег. И то и другое ничего не делает
                // и ошибкой не считается: UI работает по снимку, который всегда слегка
                // устарел, а «окно, которому вы поставили тег, только что умерло» — не баг
                // клиента. Реестр это запишет.
                _windows.AddTag((IntPtr)payload.Hwnd, payload.Tag);
                return Ok(request);
            }

            case IpcMessageTypes.RemoveTag:
            {
                var payload = Require<RemoveTagRequest>(request);
                _windows.RemoveTag((IntPtr)payload.Hwnd, payload.Tag);
                return Ok(request);
            }

            // ----------------------------------------------------------------- макросы

            case IpcMessageTypes.RunMacro:
            {
                var payload = Require<RunMacroRequest>(request);
                // По каталогу неизвестное имя ПРОВАЛИВАЕТ запрос, а не превращается в тихое
                // ничегонеделание: кнопка «Запустить» в редакторе не должна выглядеть
                // сработавшей. Всё после этой точки — «отправил и забыл»: макрос может идти
                // часами, поэтому ответ означает «начали», а не «закончили».
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
                // Ждём: ответ означает, что бегун действительно принял отмену, и именно это
                // позволяет UI убрать строку, ничего не додумывая. Неизвестный (уже
                // завершившийся) прогон завершается мгновенно — по документации это успех.
                await _runs.StopAsync(payload.RunId).WaitAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request);
            }

            case IpcMessageTypes.GetRunningMacros:
                return Ok(request, IpcJson.Write(_runs.Snapshot().ToDto()));

            case IpcMessageTypes.GetMacros:
                // Материализуем в массив, чтобы полиморфные конвертеры нод видели MacroGraph[],
                // а не последовательность интерфейсного типа.
                return Ok(request, IpcJson.Write<MacroGraph[]>([.. _macros.All]));

            case IpcMessageTypes.SaveMacro:
                return await SaveMacroAsync(request, cancellationToken).ConfigureAwait(false);

            case IpcMessageTypes.DeleteMacro:
            {
                var payload = Require<DeleteMacroRequest>(request);
                // false = такого файла нет. По контракту это ничегонеделание, а не ошибка.
                await _macros.DeleteAsync(payload.Name, cancellationToken).ConfigureAwait(false);
                return Ok(request);
            }

            case IpcMessageTypes.SubscribeRunEvents:
            {
                var payload = Require<SubscribeRunEventsRequest>(request);
                var connection = session
                                 ?? throw new IpcRequestRejectedException(
                                     "Подписка на события прогона возможна только по соединению.");
                connection.SetRunEventSubscription(payload.Enabled);

                // Живые обходы — чтобы панель, пришедшая посреди прогона, вообще узнала, что
                // прогон есть. У каждого выставлен FromStart = false: его начальные строки нод
                // случились, когда никто ещё не записывал, и восстановить их нельзя, а панель
                // обязана об этом сказать, а не рисовать хвост как целый лог. Выключение
                // подписки отвечает пустым списком — следить не за чем.
                return Ok(request, IpcJson.Write(payload.Enabled
                    ? _runEvents.LiveWalks()
                    : Array.Empty<RunWalkDto>()));
            }

            // --------------------------------------------------------------- отладчик

            case IpcMessageTypes.SetBreakpoints:
            {
                var payload = Require<SetBreakpointsRequest>(request);
                // Проверки «а существует ли такой макрос» здесь нет намеренно: точку останова
                // законно ставят и на несохранённый черновик, а набор ключуется по имени —
                // и в тот момент, когда черновик сохранят под этим именем, она начнёт кусаться.
                //
                // NodeIds размечен nullable — см. пояснение у SetBreakpointsRequest: поле
                // приезжает из JSON, и клиент вправе его не положить.
                _debug.SetBreakpoints(payload.MacroName, payload.NodeIds ?? []);
                return Ok(request);
            }

            case IpcMessageTypes.GetBreakpoints:
                return Ok(request, IpcJson.Write<BreakpointSetDto[]>([.. _debug.Breakpoints()]));

            case IpcMessageTypes.DebugCommand:
            {
                var payload = Require<DebugCommandRequest>(request);
                // Отказ неподключённому вызывающему — не занудство: счёт подключённых
                // отладчиков и есть гарантия того, что у обхода на паузе найдётся кому его
                // отпустить, а команда от соединения вне этого счёта могла бы припарковать
                // обход, который никто уже не распустит.
                if (session is not { WantsRunEvents: true })
                {
                    throw new IpcRequestRejectedException(
                        "Команды отладчика доступны только подписчику событий прогона.");
                }

                return Ok(request, IpcJson.Write(_debug.Command(payload.WalkId, payload.Command, payload.NodeId)));
            }

            // --------------------------------------------------------- горячие клавиши

            case IpcMessageTypes.SuspendHotkeys:
                await _hotkeys.SuspendAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request);

            case IpcMessageTypes.ResumeHotkeys:
                await _hotkeys.ResumeAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request);

            case IpcMessageTypes.GetHotkeyFailures:
                // Материализуем в массив, чтобы ответ был JSON-массивом даже тогда, когда
                // реализация отдаёт пустой список только для чтения. Защиты `?? []` здесь,
                // в отличие от SetBreakpoints, нет и не нужно: список приходит не из провода,
                // а от реализации в этом же процессе, и интерфейс обещает его ненулевым.
                return Ok(request, IpcJson.Write<HotkeyFailureDto[]>([.. _hotkeys.Failures]));

            // ---------------------------------------------------------------- шаблоны

            case IpcMessageTypes.GetTemplates:
                return Ok(request, IpcJson.Write<TemplateDto[]>([.. _templates.Catalog().ToDto()]));

            case IpcMessageTypes.GetTemplateImage:
                return GetTemplateImage(request);

            // ------------------------------------------------------------ диагностика

            case IpcMessageTypes.DumpCaptures:
            {
                var folder = await _captures.DumpAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request, IpcJson.Write(folder));
            }

            case IpcMessageTypes.Shutdown:
                return ShutdownAsync(request);

            // ------------------------------------------------------- жизненный цикл

            case IpcMessageTypes.RequestActivate:
            {
                // Рассылка, а не «ответить отправителю»: спрашивает второй запуск UI, который
                // вот-вот завершится, а выйти вперёд должна панель на ДРУГОМ соединении.
                // Отправить всем не стоит ничего (панель обычно ровно одна) и не требует
                // вести здесь учёт клиентов.
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
    /// Байты одного шаблона для превью.
    ///
    /// Оба отказа здесь — это отказы, а не пустой ответ, и намеренно: панель просит картинку по
    /// строке, которую сама же нарисовала из <c>GetTemplates</c>, так что «нет такого файла»
    /// означает, что список устарел (или что имя пришло не из списка), и молчаливая пустая
    /// картинка спрятала бы ровно это. Потолок сверяется здесь ещё раз, хотя панель по размеру
    /// из списка обычно и не спрашивает: между перечислением и запросом файл мог смениться, а
    /// многомегабайтная строка base64 встала бы в трубе перед событиями работающего макроса
    /// (см. <see cref="TemplateLimits.MaxImageBytes"/>).
    /// </summary>
    private IpcResponse GetTemplateImage(IpcRequest request)
    {
        var payload = Require<GetTemplateImageRequest>(request);
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            throw new IpcRequestRejectedException("GetTemplateImage: имя шаблона не задано.");
        }

        // null = файла нет ЛИБО имя несло сегменты пути; провайдер уже написал в лог, какой
        // именно из двух случаев это был.
        var bytes = _templates.TryReadFile(payload.Set, payload.Name)
                    ?? throw new IpcRequestRejectedException(
                        $"Шаблон '{Describe(payload.Set, payload.Name)}' не найден.");

        if (bytes.Length > TemplateLimits.MaxImageBytes)
        {
            throw new IpcRequestRejectedException(
                $"Шаблон '{Describe(payload.Set, payload.Name)}' — {bytes.Length} Б, "
                + $"это больше потолка превью в {TemplateLimits.MaxImageBytes} Б.");
        }

        return Ok(request, IpcJson.Write(new TemplateImageDto(payload.Set, payload.Name, bytes)));
    }

    private static string Describe(string? set, string name) => set is null ? name : $"{set}/{name}";

    /// <summary>
    /// Проверить → отказать или записать. Ответ И ЕСТЬ список замечаний: пустой означает, что
    /// граф записан, непустой — что в записи отказано, и он несёт причины.
    /// </summary>
    private async Task<IpcResponse> SaveMacroAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = Require<SaveMacroRequest>(request);
        var graph = payload.Macro
                    ?? throw new IpcRequestRejectedException("SaveMacro payload has no macro.");

        var issues = new List<ValidationIssue>(MacroGraphValidator.Validate(graph));

        // Валидатор проверяет ГРАФ; хранилище проверяет ИМЯ (оно же основа имени файла) и на
        // плохом бросает. Если вместо этого поднять его как замечание уровня графа, редактор
        // нарисует «имя содержит '/'» рядом со структурными ошибками, а не покажет невнятно
        // провалившийся запрос.
        if (MacroGraphStore.ValidateName(graph.Name) is { } nameError)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, null, nameError));
        }

        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            // Предупреждения едут вместе с ошибками — пусть редактор сразу покажет всё, что
            // всё равно попросят исправить.
            LogSaveRejected(graph.Name, issues.Count(i => i.Severity == ValidationSeverity.Error));
            return Ok(request, IpcJson.Write(issues.ToDto()));
        }

        await _macros.SaveAsync(graph, cancellationToken).ConfigureAwait(false);
        // Пустой список = записано. При успехе предупреждения намеренно НЕ возвращаются: у
        // этого поля в протоколе ровно один смысл (причины отказа), и перегрузка его вторым
        // смыслом сделала бы так, что каждое предупреждение выглядело бы для клиента как
        // несостоявшееся сохранение.
        return Ok(request, IpcJson.Write(Array.Empty<ValidationIssueDto>()));
    }

    /// <summary>
    /// Сначала отвечаем, потом останавливаемся.
    ///
    /// <see cref="IpcServer"/> зарегистрирован среди размещённых служб последним, а значит,
    /// сносится при остановке хоста ПЕРВЫМ — и вызови мы <c>StopApplication</c> прямо здесь,
    /// это самое соединение закрылось бы прямо из-под ответа. Поэтому остановка отцеплена и
    /// отложена на <see cref="ShutdownGraceMs"/>: ответ пишется и сбрасывается через микросекунды
    /// после возврата из этого метода, так что запас колоссальный, и от точного числа
    /// корректность никак не зависит — клиент, не успевший получить ответ, просто увидит, что
    /// труба закрылась, а это тот же самый сигнал.
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
/// Запрос, который клиент составил неверно (нет нагрузки, неизвестное имя макроса). Несёт
/// сообщение, рассчитанное на человека, читающего интерфейс, и пишется в лог без стека: в
/// отличие от неожиданного сбоя обработчика, расследовать здесь нечего.
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
