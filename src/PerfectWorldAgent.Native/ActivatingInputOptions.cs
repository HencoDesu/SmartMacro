namespace PerfectWorldAgent.Native;

// Tuning for IGameWindow.ActivateAsync / DeactivateAsync — the WM_ACTIVATEAPP wake-up
// pair that brackets every input session against a frozen PW background client.
//
// SettleDelayMs — how long ActivateAsync waits between the wake-up signal and returning.
// Too short and the engine hasn't unfrozen yet; too long and aggregate latency stacks up
// across 9 agents when broadcasts fan out.
//
// DeactivationDelayMs — how long DeactivateAsync waits BEFORE posting the deactivation
// signal. Both the activation and deactivation now go through PostMessage (same lane as
// the input WM_KEYDOWN/UP), so the queue itself guarantees ordering — but PW still needs
// wall-clock time to actually pump through the queued messages. The delay gives PW a
// window to chew through ACTIVATE(TRUE) → KEYDOWN → KEYUP before we add ACTIVATE(FALSE)
// to the tail. Empirically: PW's pump is slow when throttled in background, so we need
// at least 100ms.
//
// ActivationLParam — the magic lParam paired with WM_ACTIVATEAPP. The engine reacts to
// the message itself rather than validating the lParam, but it's exposed in case a future
// client version starts checking. Default 0x91D8 is carried over from a known-working
// third-party helper.
public sealed class ActivatingInputOptions
{
    public int SettleDelayMs { get; init; } = 30;
    public int DeactivationDelayMs { get; init; } = 100;
    public uint ActivationLParam { get; init; } = 0x91D8;

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
    // windows have identical client size and identical UI layout, so a single (x, y)
    // pair covers all of them — same model used by FireClickAsync broadcasts.
    //
    // We click here in FireAssistAsync to select the master before firing the assist
    // key. Replaces the old Shift+1 chord, which broke because PW reads modifier state
    // via GetKeyState — and SendMessage from another thread does NOT update that state,
    // so the '1' was perceived as un-modified (= switch self-target, not party-slot).
    //
    // Defaults are placeholders — tune in appsettings.json after the first run by
    // checking what coordinates land on the first party-list portrait at your PW UI
    // scale / resolution.
    public int PartySlot1X { get; init; } = 285;
    public int PartySlot1Y { get; init; } = 456;
}
