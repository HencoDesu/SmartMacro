using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Windows;

namespace SmartMacro.Agents;

// Default factory — composes IGameWindow + WindowRegistry into a fresh agent. The agent
// starts tagless; nothing tries to identify it here (when ProcessMonitor first sees the
// game process the user is still on the server-select screen). Identification is a macro
// concern now. Holds no state; safe as a singleton in DI.
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

        // Filter out processes whose main window can't be captured — typically launcher
        // instances of elementclient.exe that don't have a real game client surface.
        // ProcessMonitor matches by process name so launchers slip through; we drop them
        // here so the orchestrator, the macro registry, and the UI never see a doomed agent.
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

    [LoggerMessage(LogLevel.Information, "Agent created for pid={Pid} (awaiting identification)")]
    partial void LogAgentCreated(int pid);

    [LoggerMessage(LogLevel.Information, "Skipping pid={Pid} — main window has zero client area (likely a launcher process)")]
    partial void LogSkippedZeroSize(int pid);
}
