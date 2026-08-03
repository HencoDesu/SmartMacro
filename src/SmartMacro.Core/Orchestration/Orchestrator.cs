using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Agents;
using SmartMacro.Hotkeys;
using SmartMacro.Input;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.ProcessMonitoring;

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
// Сверх этого оркестратор владеет только жизненным циклом агентов: породить по одному на каждый
// появившийся процесс и держать их, чтобы при выключении остановить всех. Наружу это множество
// никто не наблюдает — с W0.3 UI смотрит на WindowRegistry и MacroRunRegistry. Команд агенты
// тоже больше не получают: широковещательной рассылки во входящие нет, есть только прогоны
// макросов по дескрипторам.
public sealed partial class Orchestrator : IHostedService, IMacroRunner, IDisposable
{
    private readonly ILogger<Orchestrator> _logger;
    private readonly Channel<AgentMessage> _inbox;
    private readonly ProcessMonitor _processMonitor;
    private readonly HotkeyListener _hotkeyListener;
    private readonly ICharacterAgentFactory _agentFactory;
    private readonly MacroGraphStore _macros;
    private readonly MacroExecutor _executor;
    private readonly MacroRunRegistry _runs;
    private readonly CursorPositionProvider _cursor;
    private readonly IMacroRunObserver? _observer;
    private readonly IMacroDebugger? _debugger;

    private readonly Lock _agentsLock = new();
    private readonly HashSet<CharacterAgent> _agents = [];

    private CancellationTokenSource? _dispatchCts;
    private Task? _dispatchLoop;

    public Orchestrator(
        ProcessMonitor processMonitor,
        HotkeyListener hotkeyListener,
        ICharacterAgentFactory agentFactory,
        MacroGraphStore macros,
        MacroExecutor executor,
        MacroRunRegistry runs,
        CursorPositionProvider cursor,
        ILogger<Orchestrator> logger,
        IMacroRunObserver? observer = null,
        IMacroDebugger? debugger = null)
    {
        _logger = logger;
        _agentFactory = agentFactory;
        _macros = macros;
        _executor = executor;
        _runs = runs;
        _cursor = cursor;
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
        // На ProcessDisappeared не подписываемся — агенты сами замечают мёртвые окна через
        // IGameWindow.IsAlive и сами завершаются, сообщая об этом AgentStoppingMessage.

        _hotkeyListener = hotkeyListener;
        _hotkeyListener.MacroTriggered += OnMacroTriggered;

        _inbox = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
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

        var graph = _macros.TryGet(macroName);
        if (graph is null)
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
                RunId = handle.RunId,
                OnNodeEntered = nodeId => handle.CurrentNodeId = nodeId,
                Observer = _observer,
                Debugger = _debugger,
            };
            var result = await _executor.RunAsync(graph, context, handle.Token).ConfigureAwait(false);
            if (result.Status == MacroRunStatus.Aborted)
            {
                LogMacroAborted(macroName, result.Error ?? "(no details)");
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

    private void OnProcessAppeared(ProcessInfo info)
    {
        LogProcessAppearedNotification(info.Pid, info.ProcessName, info.MainWindowHandle.ToInt64());
        _ = HandleProcessAppearedAsync(info);
    }

    private async Task HandleProcessAppearedAsync(ProcessInfo info)
    {
        CharacterAgent agent;
        try
        {
            agent = await _agentFactory.CreateAsync(info, _inbox.Writer).ConfigureAwait(false);
            lock (_agentsLock)
            {
                _agents.Add(agent);
            }

            // Start регистрирует окно (и фасад управления им) в WindowRegistry — это обязано
            // случиться раньше, чем какой-нибудь макрос нацелится на этот дескриптор.
            agent.Start();
        }
        catch (Exception ex)
        {
            LogAgentCreationFailed(ex, info.Pid);
            return;
        }

        StartProcessAppearedMacros(info.ProcessName, agent.Handle);
    }

    // По новому окну прогоняется каждый граф с подходящим ProcessAppearedTrigger. Подойти может
    // сразу несколько — загрузочный макрос плюс, скажем, расстановщик окон, — поэтому все они
    // стартуют параллельно, каждый со своей записью о прогоне.
    private void StartProcessAppearedMacros(string processName, IntPtr hwnd)
    {
        foreach (var graph in _macros.All)
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_dispatchCts is not null)
        {
            throw new InvalidOperationException("Dispatch loop already running.");
        }

        _dispatchCts = new CancellationTokenSource();
        _dispatchLoop = Task.Run(() => DispatchLoopAsync(_dispatchCts.Token), _dispatchCts.Token);
        LogDispatchLoopStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Сначала отменяем прогоны на лету: они управляют окнами, которые агенты вот-вот
        // снесут, а прогон, брошенный посреди активации, оставил бы клиент разбуженным.
        try
        {
            await _runs.StopAllAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await StopAllAgentsAsync(cancellationToken).ConfigureAwait(false);

        if (_dispatchCts is null)
        {
            return;
        }

        await _dispatchCts.CancelAsync();
        try
        {
            if (_dispatchLoop is not null)
            {
                await _dispatchLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _dispatchCts.Dispose();
            _dispatchCts = null;
            _dispatchLoop = null;
            LogDispatchLoopStopped();
        }
    }

    // Просит каждого агента остановиться и ждёт их RunningTask. По возможности укладывается в
    // отведённый хостом срок на выключение; выжившие сносятся вместе с выходом процесса.
    private async Task StopAllAgentsAsync(CancellationToken cancellationToken)
    {
        CharacterAgent[] snapshot;
        lock (_agentsLock)
        {
            snapshot = _agents.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        foreach (var agent in snapshot)
        {
            agent.Stop();
        }

        try
        {
            await Task.WhenAll(snapshot.Select(a => a.RunningTask ?? Task.CompletedTask))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _processMonitor.ProcessAppeared -= OnProcessAppeared;
        _hotkeyListener.MacroTriggered -= OnMacroTriggered;
        _dispatchCts?.Cancel();
        _dispatchCts?.Dispose();
        _dispatchCts = null;
    }

    private async Task ProcessIncomingAsync()
    {
        while (_inbox.Reader.TryRead(out var message))
        {
            try
            {
                await HandleMessageAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle {MessageType} from agent", message.GetType().Name);
            }
        }
    }

    private Task HandleMessageAsync(AgentMessage message)
    {
        switch (message)
        {
            case AgentStoppingMessage stopping:
                lock (_agentsLock)
                {
                    _agents.Remove(stopping.Agent);
                }

                break;

            default:
                LogUnhandledUpstreamMessageType(message.GetType().Name);
                break;
        }

        return Task.CompletedTask;
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessIncomingAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
