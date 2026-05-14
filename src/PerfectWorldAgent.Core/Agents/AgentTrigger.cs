namespace PerfectWorldAgent.Agents;

// Triggers for the per-agent state machine. Mode triggers come from the Orchestrator's
// ModeChangedMessage broadcasts; the rest are internal to the agent's RunLoopAsync.
public enum AgentTrigger
{
    // Fired by Identify() when the agent is promoted from placeholder to real character.
    Identified,

    // Fired by RunLoopAsync when a ModeChangedMessage arrives in the inbox.
    SetHold,
    SetFollow,
    SetCombat,

    // Fired by RunLoopAsync when StuckDetector confirms the character hasn't moved
    // while in Following.
    StuckDetected,
}
