namespace PerfectWorldAgent.Orchestration;

public enum OrchestratorTrigger
{
    GoFollow,
    GoHold,
    GoCombat,
    CombatFinished,
    Stop,

    // Side-effect trigger — bypasses the state machine. On press, the orchestrator
    // broadcasts SendKeyMessage(HotkeyOptions.ImmunityKey) to every live agent. Bound
    // in-game to a per-character invulnerability skill ("иммунка" — 10s damage immunity);
    // pressing the Stream Deck button hits the panic button on all agents simultaneously.
    BroadcastImmunity,

    // Side-effect trigger — bypasses the state machine. On press, every non-master agent
    // sends Shift+1 (selects party member 1 = master) then presses its own
    // Character.AssistKey (in-game macro = /assist current target). Net effect: all
    // 8 followers target whatever master targets. Used to prime the squad before
    // PreCombat / engaging a boss.
    BroadcastAssist,

    // Side-effect triggers — bypass the state machine. On press, the orchestrator reads
    // the current cursor position, translates it into the foreground window's client
    // coordinates, and broadcasts a ClickAtMessage to every live agent. Each agent then
    // applies that same client (x,y) on its OWN window — assumes all PW clients are
    // sized identically (typical multi-client setup).
    BroadcastClick,
    BroadcastDoubleClick,
}
