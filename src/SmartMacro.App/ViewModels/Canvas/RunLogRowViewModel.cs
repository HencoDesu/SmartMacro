namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// One line of the run-log strip under the canvas:
/// <c>0:01.2 · click-server-select · ок · PostMessage 1192,1805 · 40 мс</c>.
///
/// <b>Nothing produces these yet.</b> The strip is reserved and rendered empty by D3a; the
/// engine has no structured run-event stream and the protocol has no message to carry one
/// (see <c>docs/spec.md</c> §13). This type IS the surface wave D3b has to fill — it is
/// declared now so the strip's layout is pinned by something real rather than by sample
/// rows that would have to be deleted later.
/// </summary>
/// <param name="Elapsed">Time since the run started, <c>m:ss.f</c>.</param>
/// <param name="NodeId">Node the event belongs to.</param>
/// <param name="Outcome">Which way the node went: <c>ок</c>, <c>нашёл</c>, <c>таймаут</c>, …</param>
/// <param name="Detail">Free-form specifics — coordinates, tick count, duration.</param>
/// <param name="IsCurrent">The node the executor is standing on right now.</param>
/// <param name="OutcomeIsAccent">Outcome worth the accent colour (a conditional that matched).</param>
public sealed record RunLogRowViewModel(
    string Elapsed,
    string NodeId,
    string Outcome,
    string Detail,
    bool IsCurrent = false,
    bool OutcomeIsAccent = false);
