using System.Threading.Channels;
using SmartMacro.ProcessMonitoring;

namespace SmartMacro.Agents;

// Creates the CharacterAgent that owns a game-client window's lifetime. Agents are born
// tagless: identification is a macro's job now, so there is no "pending unknown" state to
// model here — the agent registers its window and the tags (if any) arrive later through
// WindowRegistry.
public interface ICharacterAgentFactory
{
    /// <summary>
    /// Creates a fresh agent bound to <paramref name="info"/>'s window handle and routes
    /// its outbox messages to the orchestrator's inbox.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the process's main window has zero client area (typically a launcher process with no usable game surface).</exception>
    Task<CharacterAgent> CreateAsync(
        ProcessInfo info,
        ChannelWriter<AgentMessage> outbox,
        CancellationToken cancellationToken = default);
}
