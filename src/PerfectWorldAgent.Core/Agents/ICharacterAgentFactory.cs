using System.Threading.Channels;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Agents;

// Creates a CharacterAgent bound to a game-client process. Always returns an agent — if
// identification fails the agent comes back with a placeholder Character and IsIdentified
// == false, ready to be promoted later via CharacterAgent.Identify(). Keeping the unknown
// case as a "fully constructed but un-promoted" agent removes the parallel pending-unknown
// state machine from the orchestrator.
public interface ICharacterAgentFactory
{
    Task<CharacterAgent> CreateAsync(
        ProcessInfo info,
        ChannelWriter<AgentMessage> outbox,
        CancellationToken cancellationToken = default);
}
