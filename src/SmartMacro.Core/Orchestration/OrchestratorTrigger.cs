namespace SmartMacro.Orchestration;

// Hotkey triggers — each one is a broadcast action. The orchestrator no longer holds a
// mode state machine in v1; pressing a hotkey just fans out an inbox message to all
// live agents.
public enum OrchestratorTrigger
{
    // Broadcasts UseImmunityMessage to every live agent. Each agent looks up its own
    // Character.ImmunityKey and presses it. Bound in-game to a per-character
    // invulnerability skill ("иммунка"); pressing the Stream Deck button hits the panic
    // button on all agents simultaneously.
    BroadcastImmunity,

    // Broadcasts TakeAssistMessage. Every non-master agent sends Shift+1 (selects party
    // member 1 = master) then presses its own Character.AssistKey (in-game macro =
    // /assist current target). Net effect: all 8 followers target whatever master
    // targets.
    BroadcastAssist,

    // The orchestrator reads the current cursor position, translates it into the
    // foreground window's client coordinates, and broadcasts a ClickAtMessage to every
    // live agent. Each agent then applies that same client (x,y) on its OWN window —
    // assumes all PW clients are sized identically (typical multi-client setup).
    BroadcastClick,
    BroadcastDoubleClick,

    // Broadcasts EnterIdentifyMessage. Every unidentified agent opens the in-game stats
    // window (press C), captures a screenshot, matches the class against the roster, and
    // promotes itself. Already-identified agents ignore the message. User-triggered:
    // press once after all PW clients are in-world.
    BroadcastIdentify,
}
