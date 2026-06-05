using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Config;
using PerfectWorldAgent.Core;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Agents;

// Default factory — composes IGameWindow + ICharacterProvider + agent options into a
// fresh agent. The agent constructs its own placeholder Character internally from the
// pid; no identification work happens here (when ProcessMonitor sees the game process,
// the user is still on the server-select screen and the nameplate isn't visible yet).
// Stateless; safe as a singleton in DI.
[SupportedOSPlatform("windows")]
public sealed partial class CharacterAgentFactory : ICharacterAgentFactory
{
    private readonly IGameWindowFactory _windowFactory;
    private readonly ICharacterProvider _provider;
    private readonly IOptions<AgentOptions> _options;
    private readonly IOptions<ActivatingInputOptions> _inputOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CharacterAgentFactory> _logger;

    public CharacterAgentFactory(
        IGameWindowFactory windowFactory,
        ICharacterProvider provider,
        IOptions<AgentOptions> options,
        IOptions<ActivatingInputOptions> inputOptions,
        ILoggerFactory loggerFactory)
    {
        _windowFactory = windowFactory;
        _provider = provider;
        _options = options;
        _inputOptions = inputOptions;
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
        // here so the orchestrator and UI never see a doomed agent.
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
            outbox,
            _provider,
            _options,
            _inputOptions,
            _loggerFactory.CreateLogger<CharacterAgent>());
        return Task.FromResult(agent);
    }

    [LoggerMessage(Microsoft.Extensions.Logging.LogLevel.Information, "Agent created for pid={Pid} (awaiting identification)")]
    partial void LogAgentCreated(int pid);

    [LoggerMessage(Microsoft.Extensions.Logging.LogLevel.Information, "Skipping pid={Pid} — main window has zero client area (likely a launcher process)")]
    partial void LogSkippedZeroSize(int pid);
}
