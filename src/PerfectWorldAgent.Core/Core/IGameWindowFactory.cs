using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Core;

// Creates an IGameWindow bound to a specific game-client process. Hides all the input-
// strategy composition (which keyboard/mouse impls, which decorators) behind a single
// "give me a process, get a window" call — that way the orchestrator's ProcessAppeared
// handler doesn't have to know about Native types.
public interface IGameWindowFactory
{
    IGameWindow Create(ProcessInfo info);
}
