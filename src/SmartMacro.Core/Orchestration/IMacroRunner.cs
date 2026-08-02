namespace SmartMacro.Orchestration;

/// <summary>
/// "Start this macro by name, no context window" — the manual equivalent of pressing its
/// hotkey. Implemented by <see cref="Orchestrator"/>.
///
/// It exists as an interface purely as a test seam for the IPC layer: the dispatcher's
/// <c>RunMacro</c> handler needs exactly this one call, while a real
/// <see cref="Orchestrator"/> drags in the process monitor, the agent factory, the
/// executor and a live cursor provider. FakeItEasy can only fake interfaces and virtuals,
/// so the seam has to be declared here rather than mocked away.
/// </summary>
public interface IMacroRunner
{
    /// <summary>
    /// Fire-and-forget start of <paramref name="macroName"/>. Returns as soon as the run is
    /// scheduled; failures (unknown macro, already running) are logged, never thrown.
    /// </summary>
    void RunMacro(string macroName);
}
