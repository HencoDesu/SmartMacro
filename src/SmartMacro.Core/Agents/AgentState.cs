namespace SmartMacro.Agents;

// Per-agent runtime state. Minimal lifecycle now — macros run as fire-and-forget
// inside Idle, not as their own state.
public enum AgentState
{
    // Just created; placeholder Character; can't act on broadcasts. Exits via
    // Identify() when ClassMatcher matches the stats-window class text.
    AwaitingIdentification,

    // Identified and waiting. All broadcasts (immunity, assist, click, run-macro)
    // are handled here without state transitions.
    Idle,
}
