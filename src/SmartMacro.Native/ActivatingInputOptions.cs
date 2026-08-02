namespace SmartMacro.Native;

// Residual tuning for AgentInputDispatcher's multi-step input sequences.
//
// TODO(W0.2): SettleDelayMs / DeactivationDelayMs / ActivationLParam moved to
// ProcessProfiles (per-process activation is a PW quirk, not a global input concern).
// PartySlot1 + InterStepDelayMs remain here only because the assist path still needs
// them — they dissolve into macro-node parameters with the node-graph model.
public sealed class ActivatingInputOptions
{
    // Delay between consecutive input steps within ONE activation cycle — e.g. between
    // the party-slot-1 click (selects master) and the assist key (fires /assist macro
    // against the now-selected master) in the assist sequence.
    // Just queueing both back-to-back means PW's pump processes them in immediate
    // succession; PW may not have fully applied the party-member selection (UI target
    // frame, internal target state) before /assist runs against whatever was previously
    // targeted. The delay gives PW wall-clock time to actually apply the click's effect
    // before the next input runs.
    //
    // Default 200ms — physical mouse-macro tools work with ~30ms for foreground PW,
    // background pump is slower so we add headroom. Bump higher (300-500ms) in
    // appsettings.json if the assist still misses on heavily-loaded background windows.
    public int InterStepDelayMs { get; init; } = 200;

    // Client-space coordinates of party-slot-1 (the master) in PW's UI. All 9 game
    // windows have identical client size and identical UI layout, so a single point
    // covers all of them — same model used by FireClickAsync broadcasts.
    //
    // We click here in FireAssistAsync to select the master before firing the assist
    // key. Replaces the old Shift+1 chord, which broke because PW reads modifier state
    // via GetKeyState — and SendMessage from another thread does NOT update that state,
    // so the '1' was perceived as un-modified (= switch self-target, not party-slot).
    //
    // Defaults are placeholders — tune in appsettings.json after the first run by
    // checking what coordinates land on the first party-list portrait at your PW UI
    // scale / resolution.
    public ScreenPoint PartySlot1 { get; init; } = new(285, 456);
}
