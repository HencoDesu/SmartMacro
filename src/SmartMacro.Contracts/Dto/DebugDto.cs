namespace SmartMacro.Contracts.Dto;

/// <summary>
/// What a <c>DebugCommand</c> asks of ONE WALK.
///
/// The unit is the walk, not the run, for the same reason the canvas follows one walk: a
/// <c>RunMacroNode</c> fan-out is ten walks of one graph sharing a run id, and stepping "the
/// run" would mean stepping one of them while freezing nine the user is not looking at.
///
/// <b>Stop is deliberately absent.</b> It is the pre-existing <c>StopMacro</c>, which takes a
/// RUN id and cancels every walk in it — see <c>IpcMessageTypes.StopMacro</c> for why the
/// asymmetry is the honest answer rather than an oversight.
/// </summary>
public enum DebugCommand
{
    /// <summary>Park the walk at the next node it reaches. A REQUEST: the walk honours it at the next node boundary, which can be a 60-second wait away.</summary>
    Pause,

    /// <summary>Release a parked walk and let it run. Breakpoints still apply.</summary>
    Resume,

    /// <summary>Release a parked walk and park it again at the very next node.</summary>
    Step,

    /// <summary>Release a parked walk and park it again when it reaches <c>NodeId</c>. Never reaching it is a legal outcome — the walk simply finishes.</summary>
    RunToNode,
}

/// <summary>
/// Breakpoints on one macro, as <c>GetBreakpoints</c> reports them.
/// </summary>
/// <param name="MacroName">Graph the breakpoints belong to.</param>
/// <param name="NodeIds">Nodes that halt a walk. Never empty — a macro with none is simply absent from the list.</param>
public sealed record BreakpointSetDto(string MacroName, IReadOnlyList<string> NodeIds);

/// <summary>
/// The debugger's answer to <c>DebugCommand</c>: what the daemon now believes about the walk.
///
/// Returned rather than pushed because the command is a round trip the panel is already
/// awaiting, and a rejected command («этого обхода больше нет») has to reach the button that
/// sent it. The eventual truth still arrives as <c>RunEventKind.Paused</c> /
/// <c>Resumed</c> — this is the acknowledgement, not the state.
/// </summary>
/// <param name="Accepted">
/// <c>false</c> when the walk is not (or no longer) known: it finished, or the daemon
/// restarted. The panel clears its debugger state rather than leaving a Pause button lit for
/// a walk that ended.
/// </param>
/// <param name="Paused"><c>true</c> when the walk is parked right now.</param>
/// <param name="PauseRequested"><c>true</c> when a pause has been asked for but the walk is still inside a node.</param>
public sealed record DebugAckDto(bool Accepted, bool Paused, bool PauseRequested);
