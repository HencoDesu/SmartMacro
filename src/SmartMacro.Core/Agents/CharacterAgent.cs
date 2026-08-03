using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.Windows;

namespace SmartMacro.Agents;

// По одному агенту на каждый отслеживаемый процесс игрового клиента — теперь это чистый
// ВЛАДЕЛЕЦ ВРЕМЕНИ ЖИЗНИ ОКНА.
//
// Всё, что делало этот класс интересным (загрузочный сценарий, идентификация, команды из
// входящей очереди, клавиши под класс персонажа), переехало в графы макросов: оркестратор
// гоняет их по дескрипторам окон, а слой примитивов возвращается от дескриптора к этому окну
// через WindowRegistry. Осталось то, что макрос не может сделать сам за себя:
//
//   * Start()  — регистрирует окно (дескриптор + имя процесса + фасад, через который окном
//                управляют) в WindowRegistry и запускает цикл проверки живости.
//   * цикл     — замечает, что клиента больше нет, и сносит регистрацию, а это и есть то, что
//                убирает окно из зоны досягаемости всех селекторов.
//   * Stop()   — отменяет цикл при штатном выключении.
//
// Идентичность целиком живёт в реестре: «опознан» означает всего лишь «несёт хотя бы один
// тег». Теги проставляют RecognizeTagNode/AddTagNode или руками из UI — сам агент их никогда
// не читает и логирует hwnd, стабильный ключ, по которому к этому окну обращается всё
// остальное.
//
// TODO(W0.4): раз это уже оболочка над временем жизни, естественное следующее упрощение —
// сложить её в WindowRegistry (или в маленький WindowHost); мешает пока то, что у реестра нет
// ни асинхронного пути создания, ни собственного цикла опроса на окно.
public sealed partial class CharacterAgent
{
    private readonly IGameWindow _window;
    private readonly string _processName;
    private readonly WindowRegistry _registry;
    private readonly ChannelWriter<AgentMessage> _outbox;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<CharacterAgent> _logger;

    private CancellationTokenSource? _runCts;

    public CharacterAgent(
        IGameWindow window,
        string processName,
        WindowRegistry registry,
        ChannelWriter<AgentMessage> outbox,
        IOptions<AgentOptions> options,
        ILogger<CharacterAgent> logger)
    {
        _window = window;
        _processName = processName;
        _registry = registry;
        _outbox = outbox;
        _pollInterval = TimeSpan.FromSeconds(options.Value.AgentPollIntervalSeconds);
        _logger = logger;
    }

    /// <summary>Дескриптор игрового окна под капотом — ключ, по которому к этому окну обращаются макросы.</summary>
    public IntPtr Handle => _window.Handle;

    public Task? RunningTask { get; private set; }

    /// <summary>
    /// Регистрирует окно в <see cref="WindowRegistry"/> — дескриптор, имя процесса и фасад,
    /// через который им управляют примитивы макросов, — и запускает цикл проверки живости.
    /// Макросы, которые запускает процесс этого окна, не должны стартовать до возврата отсюда.
    /// </summary>
    /// <exception cref="InvalidOperationException">Бросается при вызове на уже запущенном агенте.</exception>
    public void Start()
    {
        if (RunningTask is not null)
        {
            throw new InvalidOperationException(
                $"Agent for hwnd=0x{Handle.ToInt64():X} is already started.");
        }

        _registry.Register(Handle, _processName, _window);

        _runCts = new CancellationTokenSource();
        RunningTask = Task.Run(() => RunLoopAsync(_runCts.Token));
    }

    /// <summary>
    /// Даёт рабочему циклу сигнал остановиться. Цикл выходит, снимает окно с регистрации в
    /// <see cref="WindowRegistry"/>, пишет <see cref="AgentStoppingMessage"/>, и задача
    /// завершается через <see cref="RunningTask"/>.
    /// </summary>
    public void Stop()
    {
        _runCts?.Cancel();
    }

    // Обнаружение смерти окна и больше ничего. Тик дешёвый (один вызов IsWindow), а запись в
    // реестре обязана исчезнуть быстро: мёртвый hwnd, оставшийся зарегистрированным, продолжал
    // бы подходить под теговые селекторы, и каждое разветвление тратило бы на него цикл
    // активации впустую.
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        LogStarted(Handle.ToInt64(), _pollInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_window.IsAlive)
                {
                    LogWindowGone();
                    break;
                }

                try
                {
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogRunFailed(ex);
        }
        finally
        {
            LogStopped(Handle.ToInt64());
            // Окна больше нет (или мы выключаемся) — запись в реестре и её теги умирают
            // вместе с ним. Поднимает WindowClosed для подписчиков реестра.
            _registry.Unregister(Handle);
            _outbox.TryWrite(new AgentStoppingMessage(this));
            _runCts?.Dispose();
            _runCts = null;
        }
    }
}
