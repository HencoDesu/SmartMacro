using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Hotkeys;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Storage;
using SmartMacro.Contracts.Settings;
using SmartMacro.Orchestration;
using SmartMacro.Resources;
using SmartMacro.Settings;
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

    // Насколько свежая запись бандла считается «наблюдатель хранилища мог ещё не успеть»;
    // см. WrittenInsideTheWatcherWindow. С запасом к его 300 мс гашения дребезга.
    private static readonly TimeSpan WatcherLagWindow = TimeSpan.FromSeconds(2);

    private readonly WindowRegistry _windows;
    private readonly MacroGraphStore _macros;
    private readonly MacroRunRegistry _runs;
    private readonly IMacroRunner _runner;
    private readonly IHotkeyRegistration _hotkeys;
    private readonly CaptureDumpService _captures;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly RunEventPublisher _runEvents;
    private readonly LogEventPublisher _log;
    private readonly MacroDebugSession _debug;
    private readonly SettingsStore _settingsStore;
    private readonly SettingsSnapshotProvider _settings;
    private readonly EnvironmentDiagnostics _diagnostics;
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
        IHostApplicationLifetime lifetime,
        RunEventPublisher runEvents,
        LogEventPublisher log,
        MacroDebugSession debug,
        SettingsStore settingsStore,
        SettingsSnapshotProvider settings,
        EnvironmentDiagnostics diagnostics,
        ILogger<IpcRequestDispatcher> logger)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _diagnostics = diagnostics;
        _windows = windows;
        _macros = macros;
        _runs = runs;
        _runner = runner;
        _hotkeys = hotkeys;
        _captures = captures;
        _lifetime = lifetime;
        _runEvents = runEvents;
        _log = log;
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
    /// в каталоге, кроме <c>SubscribeRunEvents</c>, <c>SubscribeLog</c>, <c>DebugCommand</c> и
    /// пары <c>SuspendHotkeys</c>/<c>ResumeHotkeys</c>, относится к движку, а не к клиенту, и в
    /// таком виде работает прекрасно; эти вежливо отказывают.
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
                //
                // ГОНКА, КОТОРУЮ ЗАВЕЛА F3, И У НЕЁ ДВЕ СТОРОНЫ. Пишет теперь панель, а демон
                // узнаёт о файле наблюдателем с гашением дребезга в 300 мс, так что «Сохранить»,
                // а сразу следом «Запустить» попадают в промежуток, где снимок демона отстал.
                // Промах («макроса ещё нет») закрывали сразу; вторую сторону — «макрос ЕСТЬ, но
                // прошлой версии» — нет, а она хуже: неизвестное имя хотя бы отвечает ошибкой, а
                // устаревший граф молча гоняет по живым клиентам предыдущую правку вместе со
                // старыми шаблонами, и симптом у этого один — «моя правка не работает».
                var entry = _macros.TryGetEntry(payload.Name);
                if (entry is null || WrittenInsideTheWatcherWindow(entry))
                {
                    _macros.Refresh();
                    if (_macros.TryGet(payload.Name) is null)
                    {
                        throw new IpcRequestRejectedException(string.Format(
                            CultureInfo.CurrentCulture, Strings.Ipc_Rejected_MacroNotFound, payload.Name));
                    }
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

            case IpcMessageTypes.SubscribeRunEvents:
            {
                var payload = Require<SubscribeRunEventsRequest>(request);
                var connection = session
                                 ?? throw new IpcRequestRejectedException(
                                     Strings.Ipc_Rejected_RunEventsNeedConnection);
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
                // То же и про под-макрос: он может быть только что выделен и ещё не записан.
                //
                // NodeIds размечен nullable — см. пояснение у SetBreakpointsRequest: поле
                // приезжает из JSON, и клиент вправе его не положить.
                _debug.SetBreakpoints(payload.MacroName, payload.SubmacroId, payload.NodeIds ?? []);
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
                        Strings.Ipc_Rejected_DebuggerNeedsSubscription);
                }

                return Ok(request, IpcJson.Write(_debug.Command(payload.WalkId, payload.Command, payload.NodeId)));
            }

            // --------------------------------------------------------- горячие клавиши

            // Обе идут ЧЕРЕЗ СЕССИЮ, а не прямо в слушателя, и это не украшение. Приостановка —
            // состояние движка, снять которое способна только панель; уходит же она не только
            // ответным ResumeHotkeys, но и «Снять задачу», падением и тем, что демон сам
            // выбрасывает клиента за забитую очередь. Владельцем поэтому назначено соединение —
            // ровно так же, как счётом подключённых отладчиков владеет подписка на события
            // прогона. См. IIpcSession.SetHotkeySuspensionAsync.
            case IpcMessageTypes.SuspendHotkeys:
            {
                var connection = session
                                 ?? throw new IpcRequestRejectedException(
                                     Strings.Ipc_Rejected_HotkeySuspensionNeedsConnection);
                await connection.SetHotkeySuspensionAsync(true, cancellationToken).ConfigureAwait(false);
                return Ok(request);
            }

            case IpcMessageTypes.ResumeHotkeys:
            {
                var connection = session
                                 ?? throw new IpcRequestRejectedException(
                                     Strings.Ipc_Rejected_HotkeySuspensionNeedsConnection);
                await connection.SetHotkeySuspensionAsync(false, cancellationToken).ConfigureAwait(false);
                return Ok(request);
            }

            case IpcMessageTypes.GetHotkeyFailures:
                // Материализуем в массив, чтобы ответ был JSON-массивом даже тогда, когда
                // реализация отдаёт пустой список только для чтения. Защиты `?? []` здесь,
                // в отличие от SetBreakpoints, нет и не нужно: список приходит не из провода,
                // а от реализации в этом же процессе, и интерфейс обещает его ненулевым.
                return Ok(request, IpcJson.Write<HotkeyFailureDto[]>([.. _hotkeys.Failures]));

            // ------------------------------------------------------------------ журнал

            case IpcMessageTypes.SubscribeLog:
            {
                var payload = Require<SubscribeLogRequest>(request);
                var connection = session
                                 ?? throw new IpcRequestRejectedException(
                                     Strings.Ipc_Rejected_LogNeedsConnection);

                // ПОРЯДОК ЗДЕСЬ ЗНАЧИМ. Сперва включаем ленту, и только потом снимаем
                // предысторию: в обратном порядке между снимком и подпиской образовалась бы
                // дыра, которую уже ничем не заполнить, — а в этом возможен лишь ПОВТОР записи,
                // попавшей ровно между двумя строками, и от него у панели есть Seq.
                connection.SetLogSubscription(payload.Enabled);

                // Выключение отвечает пустым массивом: предыстория без ленты никому не нужна.
                return Ok(request, IpcJson.Write(payload.Enabled
                    ? _log.History()
                    : Array.Empty<LogEntryDto>()));
            }

            // --------------------------------------------------------------- настройки

            case IpcMessageTypes.GetSettings:
                return Ok(request, IpcJson.Write(_settings.Snapshot()));

            case IpcMessageTypes.SaveSettings:
            {
                var payload = Require<SaveSettingsRequest>(request);
                var settings = payload.Settings
                               ?? throw new IpcRequestRejectedException(
                                   Strings.Ipc_Rejected_SaveSettingsWithoutPayload);

                // Пустой список = записано; непустой = НЕ записано, вот причины. Договорённость
                // дословно та же, что у SaveMacro. Проверяет хранилище — там же, где пишет, —
                // чтобы «проверено» и «записано» не могли разъехаться во времени.
                var issues = await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                return Ok(request, IpcJson.Write<SettingsIssue[]>([.. issues]));
            }

            case IpcMessageTypes.ResetSettings:
            {
                await _settingsStore.ResetAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request, IpcJson.Write(_settings.Snapshot()));
            }

            case IpcMessageTypes.SetLogLevel:
            {
                var payload = Require<SetLogLevelRequest>(request);
                // Не сохраняется никуда — см. каталог и ILogLevelSwitch. Ответ несёт весь
                // снимок, потому что у панели один обработчик и на ответ, и на пуш.
                _settings.SetLogLevel(payload.Level);
                return Ok(request, IpcJson.Write(_settings.Snapshot()));
            }

            // ------------------------------------------------------------ диагностика

            case IpcMessageTypes.RunDiagnostics:
            {
                var results = await _diagnostics.RunAsync(cancellationToken).ConfigureAwait(false);
                return Ok(request, IpcJson.Write<DiagnosticDto[]>([.. results]));
            }

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
                                  ?? throw new IpcRequestRejectedException(
                                      Strings.Ipc_Rejected_NoBroadcaster);
                broadcaster.Broadcast(new IpcEvent(IpcMessageTypes.ActivateWindow));
                return Ok(request);
            }

            default:
                LogUnknownType(request.Type, request.Id);
                return Fail(request, $"unknown request type: {request.Type}");
        }
    }

    /// <summary>
    /// Мог ли бандл быть переписан уже ПОСЛЕ того, как хранилище сняло свой снимок.
    ///
    /// <b>Спрашиваем у файловой системы одну отметку времени, а не перечитываем папку.</b>
    /// Безусловный <c>Refresh</c> был бы проще и честнее по результату, но он разбирает КАЖДЫЙ
    /// бандл в <c>macros/</c> на каждое нажатие ▸ — то есть платит полным чтением библиотеки за
    /// случай, который бывает раз в сеанс. Здесь же одна проверка атрибутов файла: ни открытия
    /// архива, ни разбора JSON.
    ///
    /// <b>Почему «возраст файла», а не сверка версий.</b> Единственный способ отстать — попасть
    /// внутрь окна гашения дребезга наблюдателя хранилища: тот стреляет НА ЗАПИСЬ, и весь вопрос
    /// в том, успел ли он перечитать папку. Значит, подозрителен ровно тот файл, что записан
    /// только что. Сравнивать дату правки из паспорта с датой файла было бы точнее на бумаге, но
    /// эти две отметки ставятся в разные мгновения одной и той же записи (писатель штампует
    /// паспорт до сериализации), и разошлись бы они на неизвестное «время записи» — то есть
    /// сверке потребовался бы допуск, который сам по себе врал бы при двух сохранениях подряд.
    ///
    /// Окно намеренно с большим запасом к 300 мс дребезга: лишний <c>Refresh</c> стоит одного
    /// чтения папки и <see cref="MacroGraphStore.Refresh"/> поднимает <c>MacrosChanged</c>, только
    /// если снимок действительно изменился, — а пропущенная правка стоит прогона старого графа по
    /// живым клиентам. По той же причине «дата в будущем» (файл принесли с машины со спешащими
    /// часами) считается подозрительной, а не свежей: возраст выйдет отрицательным и попадёт в
    /// окно.
    /// </summary>
    private static bool WrittenInsideTheWatcherWindow(MacroLibraryEntry entry)
    {
        try
        {
            // Файла нет — GetLastWriteTimeUtc отдаёт 1601 год, возраст выходит гигантским, и это
            // верный ответ: «перечитывать незачем». Удалённый бандл снимку вернёт наблюдатель, а
            // запустить только что удалённый макрос куда безобиднее, чем прогнать старый.
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(entry.Path) < WatcherLagWindow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не смогли посмотреть — считаем, что могли отстать. Ошибиться здесь в сторону
            // лишнего чтения папки дёшево, в другую сторону — нет.
            return true;
        }
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
