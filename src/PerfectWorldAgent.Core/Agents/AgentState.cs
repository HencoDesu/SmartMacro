namespace PerfectWorldAgent.Agents;

// Per-agent runtime state. Each state owns its own concurrent work loop (started on
// OnEntry, cancelled on OnExit) — adding new states means adding new OnEntry/OnExit
// hooks and a loop method, no central switch.
public enum AgentState
{
    // Just created; placeholder Character; can't act on broadcasts. The identification
    // loop runs while in this state, polling screenshots and trying to match against
    // the roster. Exits via Identify() when a match is found (or the user labels via UI).
    AwaitingIdentification,

    // Identified and waiting. Broadcasts (immunity, assist, click) are handled by the
    // main run loop independently of state.
    Idle,

    // Running the character's combat macro. Time-bounded to ~10 seconds — after the
    // window elapses (regardless of where in the macro we are), the agent transitions
    // back to Idle. Re-pressing the Combat hotkey while already in InCombat is ignored.
    InCombat,
}
