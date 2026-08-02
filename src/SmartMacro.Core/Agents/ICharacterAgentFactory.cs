using System.Threading.Channels;
using SmartMacro.ProcessMonitoring;

namespace SmartMacro.Agents;

// Creates a CharacterAgent bound to a game-client process. Always returns an agent — if
// identification fails the agent comes back with a placeholder Character and IsIdentified
// == false, ready to be promoted later via CharacterAgent.Identify(). Keeping the unknown
// case as a "fully constructed but un-promoted" agent removes the parallel pending-unknown
// state machine from the orchestrator.
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
