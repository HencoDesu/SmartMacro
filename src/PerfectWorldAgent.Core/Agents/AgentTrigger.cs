namespace PerfectWorldAgent.Agents;

// Triggers for the per-agent state machine.
public enum AgentTrigger
{
    // Fired by Identify() when the agent is promoted from placeholder to real character.
    Identified,

    // Fired by the inbox-message handler when an EnterCombatMessage arrives. Transitions
    // Idle → InCombat; ignored if already InCombat (per-design "re-press does nothing").
    SetCombat,

    // Fired by the combat runner when the 10-second window elapses (or the macro
    // completes and we've waited out the remaining window). Transitions InCombat → Idle.
    SetIdle,
}
