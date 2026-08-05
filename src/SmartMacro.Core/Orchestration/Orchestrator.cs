using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;
using SmartMacro.Hotkeys;
using SmartMacro.Input;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Orchestration;

// Единственное место, где триггер превращается в прогон макроса. Все пути — хоткей, появление
// процесса, кнопка «Запустить» в UI — сходятся в RunAsync:
//
//   триггер → MacroGraphStore.TryGet(name)
//           → MacroRunRegistry.TryBegin(name, singleFlightKey)   [null = уже выполняется]
//           → MacroExecutor.RunAsync(graph, context, handle.Token)
//           → MacroRunRegistry.Complete(runId)                   [всегда, в finally]
//
// Два источника триггеров отличаются только тем, что кладут в контекст:
//   * хоткей            — контекст-окна нет; макрос маршрутизирует по теговым селекторам, а
//                         single-flight идёт по ИМЕНИ макроса (долбить по хоткею бесполезно).
//   * появление процесса — контекстом СЛУЖИТ новое окно, а single-flight идёт по паре
//                         (макрос, окно), чтобы девять одновременно запущенных клиентов
//                         загрузились каждый.
// Оба засевают переменную `cursor`, потому что макрос не может знать, как именно его запустили.
//
// Сверх этого оркестратор берёт новые окна под управление: появился процесс — собрать фасад
// IGameWindow, положить его в WindowRegistry и запустить макросы этого процесса. Собственного
// состояния при этом не остаётся: список окон живёт в реестре, список прогонов — в
// MacroRunRegistry, а смерть окна замечает WindowLifetimeMonitor. До W0.4 всё это было
// множеством CharacterAgent'ов под блокировкой плюс канал сообщений «агент → оркестратор»,
// единственным содержимым которого было «вот этот агент остановился».
public sealed partial class Orchestrator : IHostedService, IMacroRunner, IDisposable
{
    private readonly ILogger<Orchestrator> _logger;
    private readonly ProcessMonitor _processMonitor;
    private readonly HotkeyListener _hotkeyListener;
    private readonly IGameWindowFactory _windowFactory;
    private readonly WindowRegistry _windows;
    private readonly MacroGraphStore _macros;
    private readonly MacroExecutor _executor;
    private readonly MacroRunRegistry _runs;
    private readonly CursorPositionProvider _cursor;
    private readonly MacroTemplateCache _templates;
    private readonly IMacroRunObserver? _observer;
    private readonly IMacroDebugger? _debugger;

    public Orchestrator(
        ProcessMonitor processMonitor,
        HotkeyListener hotkeyListener,
        IGameWindowFactory windowFactory,
        WindowRegistry windows,
        MacroGraphStore macros,
        MacroExecutor executor,
        MacroRunRegistry runs,
        CursorPositionProvider cursor,
        MacroTemplateCache templates,
        ILogger<Orchestrator> logger,
        IMacroRunObserver? observer = null,
        IMacroDebugger? debugger = null)
    {
        _logger = logger;
        _windowFactory = windowFactory;
        _windows = windows;
        _macros = macros;
        _executor = executor;
        _runs = runs;
        _cursor = cursor;
        _templates = templates;
        // Необязателен: демон всегда подаёт его, и именно на нём живёт подсветка на канве в
        // панели. Хост без наблюдателя (или тест) работает без съёма показаний — ровно так же,
        // как по сути ведёт себя и демон, пока никто не подписан.
        _observer = observer;
        // Устроен так же: бездействует, пока не подключится панель, и вот тогда может
        // припарковать обход между двумя нодами. Хост без отладчика просто нельзя поставить на
        // паузу.
        _debugger = debugger;

        _processMonitor = processMonitor;
        _processMonitor.ProcessAppeared += OnProcessAppeared;
        // На ProcessDisappeared не подписываемся — мёртвые окна замечает WindowLifetimeMonitor
        // по IGameWindow.IsAlive, и снятие с регистрации там же.

        _hotkeyListener = hotkeyListener;
        _hotkeyListener.MacroTriggered += OnMacroTriggered;
    }

    /// <summary>
    /// Запускает макрос по имени без контекст-окна — ручной эквивалент нажатия его хоткея. Этим
    /// пользуется кнопка «Запустить» в UI. «Отправил и забыл»; сбои уходят в лог.
    /// </summary>
    public void RunMacro(string macroName)
    {
        _ = RunAsync(macroName, contextWindow: null, singleFlightKey: null);
    }

    /// <summary>
    /// Прогоняет один макрос до конца под присмотром реестра прогонов.
    /// </summary>
    /// <param name="macroName">Граф для прогона; неизвестное имя ничего не делает, только пишет в лог.</param>
    /// <param name="contextWindow">Окно, по которому работают ноды без цели, или <c>null</c> для макросов, живущих одними селекторами.</param>
    /// <param name="singleFlightKey">Ключ схлопывания повторов; <c>null</c> = имя макроса (см. <see cref="MacroRunRegistry.TryBegin"/>).</param>
    public async Task RunAsync(string macroName, IntPtr? contextWindow, string? singleFlightKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);

        var entry = _macros.TryGetEntry(macroName);
        if (entry is null)
        {
            LogMacroNotFound(macroName);
            return;
        }

        var handle = _runs.TryBegin(macroName, singleFlightKey);
        if (handle is null)
        {
            // По этому ключу уже что-то выполняется — реестр это записал.
            return;
        }

        try
        {
            var context = new MacroRunContext
            {
                ContextWindow = contextWindow,
                Variables = MacroVariables.ForTrigger(_cursor.Current()),
                // Источник шаблонов ставится ЗДЕСЬ и ровно один раз на прогон (§5.7, F2): с
                // переездом шаблонов внутрь бандла одно и то же имя у двух макросов означает два
                // разных файла, так что глобального разрешения больше не существует. Дочерние
                // обходы его наследуют — прогон не покидает свой бандл.
                Templates = _templates.For(macroName),
                // Под-макросы ставятся ЗДЕСЬ и ровно один раз на прогон, рядом с шаблонами и по
                // тому же доводу (волна F4): они лежат внутри бандла, прогон его не покидает, и
                // разрешать ссылку глобально попросту негде. Берутся из ТОЙ ЖЕ записи
                // библиотеки, что и граф, — то есть из одного файла и одного чтения.
                Submacros = entry.SubmacrosById,
                RunId = handle.RunId,
                OnNodeEntered = nodeName => handle.CurrentNodeName = nodeName,
                Observer = _observer,
                Debugger = _debugger,
            };
            var result = await _executor.RunAsync(entry.Graph, context, handle.Token).ConfigureAwait(false);
            if (result.Status == MacroRunStatus.Aborted)
            {
                LogMacroAborted(macroName, result.Error ?? "(без подробностей)");
            }
        }
        catch (Exception ex)
        {
            // MacroExecutor превращает сбои уровня прогона в результаты; всё, что долетело
            // сюда, — это баг, и он всё равно не имеет права уронить хост.
            LogMacroFailed(ex, macroName);
        }
        finally
        {
            _runs.Complete(handle.RunId);
        }
    }

    private void OnMacroTriggered(string macroName)
    {
        LogHotkeyTriggered(macroName);
        _ = RunAsync(macroName, contextWindow: null, singleFlightKey: null);
    }

    /// <summary>
    /// Берёт новое окно под управление и запускает его макросы «на появление процесса».
    ///
    /// Единственный путь, которым окно попадает под управление. Работа синхронная и дешёвая
    /// (собрать фасад, спросить размер клиентской области, положить запись в реестр), поэтому
    /// выполняется прямо в обработчике — цикл опроса ProcessMonitor от неё не пострадает, а
    /// взамен видно, что регистрация закончилась ДО того, как стартовал первый макрос.
    ///
    /// В бою зовётся ровно из одного места — события <see cref="ProcessMonitor.ProcessAppeared"/>,
    /// на которое мы подписались в конструкторе. Публичный он потому, что это ВТОРОЙ вход в
    /// оркестратор наравне с <see cref="RunAsync"/>, и без него ветку «появился процесс» нельзя
    /// проверить иначе как запустив настоящий процесс с настоящим окном: событие монитора поднять
    /// снаружи класса невозможно, а вооружение триггеров именно этой ветки — то место, где F3 уже
    /// один раз недосмотрели.
    /// </summary>
    public void OnProcessAppeared(ProcessInfo info)
    {
        LogProcessAppearedNotification(info.Pid, info.ProcessName, info.MainWindowHandle.ToInt64());

        IGameWindow window;
        try
        {
            window = _windowFactory.Create(info);

            // Отсеиваем процессы, чьё главное окно нечего захватывать, — обычно это экземпляры
            // elementclient.exe от лаунчера, у которых нет настоящей поверхности игрового
            // клиента. ProcessMonitor сопоставляет по имени процесса, поэтому лаунчеры
            // просачиваются; мы выбрасываем их здесь, чтобы ни реестр окон, ни макросы, ни UI
            // никогда не увидели заведомо обречённой записи.
            var (width, height) = window.ClientSize;
            if (width <= 0 || height <= 0)
            {
                LogWindowSkippedZeroSize(info.Pid);
                return;
            }
        }
        catch (Exception ex)
        {
            LogWindowAdoptionFailed(ex, info.Pid);
            return;
        }

        // ПОРЯДОК ЭТИХ ДВУХ СТРОК ОБЯЗАТЕЛЕН И ЗНАЧИМ. Регистрация должна завершиться раньше,
        // чем стартует первый макрос на появление процесса: слой примитивов ходит от hwnd к
        // управляемому окну только через WindowRegistry, и нода, добравшаяся до ещё не
        // зарегистрированного дескриптора, падает на исполнении. Между ними ничего вставлять
        // нельзя, и ничего асинхронного — тоже.
        _windows.Register(window.Handle, info.ProcessName, window);
        StartProcessAppearedMacros(info.ProcessName, window.Handle);
    }

    // По новому окну прогоняется каждый граф с подходящим ProcessAppearedTrigger. Подойти может
    // сразу несколько — загрузочный макрос плюс, скажем, расстановщик окон, — поэтому все они
    // стартуют параллельно, каждый со своей записью о прогоне.
    //
    // ИСТОЧНИК — Armed, А НЕ All, И ЭТО ТО ЖЕ ТРЕБОВАНИЕ F3, ЧТО У HotkeyListener. Триггер на
    // появление процесса — второй и последний способ вооружить макрос, а вооружать позволено
    // только тот, чей граф прочитан И признан валидатором при загрузке. Отдельный список для того
    // и заведён, что «забыли отфильтровать в одном из мест» выглядит как макрос, который иногда
    // работает; здесь это было бы хуже дефекта D4, который F3 закрывала: у макроса с ошибкой
    // хоткей честно мёртв и строка библиотеки честно ругается, так что пользователь считает его
    // инертным — а каждый запуск клиента стартовал бы его на десяти окнах и обрывал на битой ноде
    // где-то посреди логин-последовательности. Молчащий макрос лучше макроса, делающего полдела.
    private void StartProcessAppearedMacros(string processName, IntPtr hwnd)
    {
        foreach (var graph in _macros.Armed)
        {
            if (!graph.Triggers.OfType<ProcessAppearedTrigger>()
                    .Any(trigger =>
                        string.Equals(trigger.ProcessName, processName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            LogProcessMacroStarting(graph.Name, processName, hwnd.ToInt64());
            var key = string.Create(CultureInfo.InvariantCulture, $"{graph.Name}@0x{hwnd.ToInt64():X}");
            _ = RunAsync(graph.Name, hwnd, key);
        }
    }

    // Подписки на ProcessMonitor и HotkeyListener наведены в конструкторе, так что запускать
    // здесь нечего: оркестратор реагирует на события, а не крутит собственный цикл. Ролью
    // IHostedService он остаётся исключительно ради StopAsync.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Отменяет прогоны на лету и ждёт, пока они свернутся. Хост останавливает hosted-сервисы
    /// в порядке, обратном регистрации, а <see cref="WindowLifetimeMonitor"/> зарегистрирован
    /// РАНЬШЕ оркестратора, то есть остановится ПОЗЖЕ, — благодаря этому окна снимаются с
    /// регистрации уже после отмены, и ни один прогон не бросают посреди активации, оставив
    /// клиент разбуженным.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _runs.StopAllAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _processMonitor.ProcessAppeared -= OnProcessAppeared;
        _hotkeyListener.MacroTriggered -= OnMacroTriggered;
    }
}
