using PerfectWorldAgent.ProcessMonitoring;

namespace PerfectWorldAgent.GameWindows;

// Creates an IGameWindow bound to a specific game-client process. Hides all the input-
// strategy composition (which keyboard/mouse impls, which decorators) behind a single
// "give me a process, get a window" call — that way the orchestrator's ProcessAppeared
// handler doesn't have to know about Native types.
public interface IGameWindowFactory
{
    /// <summary>
    /// Creates an <see cref="IGameWindow"/> wrapping <paramref name="info"/>'s main window
    /// handle, composing the configured input strategy (PostMessage + WM_ACTIVATEAPP
    /// wake-up by default).
    /// </summary>
    IGameWindow Create(ProcessInfo info);
}
