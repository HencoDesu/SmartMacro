namespace PerfectWorldAgent.Agents;

// Per-agent runtime state. Each CharacterAgent owns its own state machine — distinct
// from the Orchestrator's AgentMode (which is the global "what does the user want the
// squad to do"). The two can diverge: orchestrator broadcasts FOLLOW, but a particular
// agent may be Stuck and unable to follow until recovery.
public enum AgentState
{
    // Just created; placeholder Character; can't act. Exits via Identify().
    AwaitingIdentification,

    // Identified and not currently doing per-mode work. Default after promotion and
    // after any SetHold trigger.
    Idle,

    // Executing follow behavior (chase the master, restart /follow on lapses).
    Following,

    // Executing the combat rotation (burst, damage rotation, target acquisition).
    InCombat,

    // StuckDetector flagged us as not having moved while orchestrator wants us moving.
    // Recovery attempts (re-issue /follow, jump, notify) live in the Stuck tick.
    Stuck,
}
