using SmartMacro.Orchestration;

namespace SmartMacro.App.Services;

/// <summary>
/// Starts a macro from the UI — the manual equivalent of pressing its hotkey (no context
/// window; the graph routes by tag selector). Same testability/daemon-split rationale as
/// <see cref="IHotkeySuspension"/>: the editor VM must not need a live
/// <see cref="Orchestrator"/> (which drags in the whole engine) to be unit-tested, and
/// stage 2 replaces this with a <c>RunMacro</c> IPC call.
/// </summary>
public interface IMacroLauncher
{
    /// <summary>Fire-and-forget start of <paramref name="macroName"/>. Failures are logged, never thrown.</summary>
    void RunMacro(string macroName);
}

/// <summary>Adapter over the in-process <see cref="Orchestrator"/>.</summary>
public sealed class OrchestratorMacroLauncher : IMacroLauncher
{
    private readonly Orchestrator _orchestrator;

    public OrchestratorMacroLauncher(Orchestrator orchestrator) => _orchestrator = orchestrator;

    public void RunMacro(string macroName) => _orchestrator.RunMacro(macroName);
}
