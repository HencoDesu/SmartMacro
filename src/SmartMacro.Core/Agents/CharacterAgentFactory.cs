using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Windows;

namespace SmartMacro.Agents;

// Фабрика по умолчанию — собирает IGameWindow и WindowRegistry в свежего агента. Агент
// рождается без тегов; опознавать его здесь никто не пытается (когда ProcessMonitor впервые
// видит процесс игры, пользователь ещё на экране выбора сервера). Идентификация теперь дело
// макросов. Состояния не держит, синглтоном в DI безопасна.
[SupportedOSPlatform("windows")]
public sealed partial class CharacterAgentFactory : ICharacterAgentFactory
{
    private readonly IGameWindowFactory _windowFactory;
    private readonly WindowRegistry _registry;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CharacterAgentFactory> _logger;

    public CharacterAgentFactory(
        IGameWindowFactory windowFactory,
        WindowRegistry registry,
        IOptions<AgentOptions> options,
        ILoggerFactory loggerFactory)
    {
        _windowFactory = windowFactory;
        _registry = registry;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = _loggerFactory.CreateLogger<CharacterAgentFactory>();
    }

    public Task<CharacterAgent> CreateAsync(
        ProcessInfo info,
        ChannelWriter<AgentMessage> outbox,
        CancellationToken cancellationToken = default)
    {
        var window = _windowFactory.Create(info);

        // Отсеиваем процессы, чьё главное окно нечего захватывать, — обычно это экземпляры
        // elementclient.exe от лаунчера, у которых нет настоящей поверхности игрового клиента.
        // ProcessMonitor сопоставляет по имени процесса, поэтому лаунчеры просачиваются; мы
        // выбрасываем их здесь, чтобы ни оркестратор, ни реестр макросов, ни UI никогда не
        // увидели заведомо обречённого агента.
        var (w, h) = window.ClientSize;
        if (w <= 0 || h <= 0)
        {
            LogSkippedZeroSize(info.Pid);
            throw new InvalidOperationException(
                $"Process pid={info.Pid} has no usable client area ({w}×{h}); skipping agent.");
        }

        LogAgentCreated(info.Pid);
        var agent = new CharacterAgent(
            window,
            info.ProcessName,
            _registry,
            outbox,
            _options,
            _loggerFactory.CreateLogger<CharacterAgent>());
        return Task.FromResult(agent);
    }

    // «Опознание впереди» отсюда убрано: с W0.1 фабрика ничего такого не обещает. Опознание
    // перестало быть встроенным и стало макросами pw-identify / pw-boot, так что дойдёт ли до
    // него дело — зависит от того, завёл ли пользователь такой макрос с триггером на появление
    // процесса. Агент — это только время жизни окна.
    [LoggerMessage(LogLevel.Information, "Создан агент для pid={Pid}")]
    partial void LogAgentCreated(int pid);

    [LoggerMessage(LogLevel.Information,
        "Пропускаем pid={Pid} — у главного окна нулевая клиентская область (скорее всего процесс лаунчера)")]
    partial void LogSkippedZeroSize(int pid);
}
