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
    // the Shift+1 chord (selects party member 1 = master) and the F2 keypress (fires
    // /assist macro against the now-selected master) in the assist sequence.
    // Just queueing both back-to-back means PW's pump processes them in immediate
    // succession; PW may not have fully applied the party-member selection (UI target
    // frame, internal target state) before /assist runs against whatever was previously
    // targeted. Result observed: F2 fires while target is still the agent itself, so
    // /assist self-targets and the character ends up looking at the master. The delay
    // gives PW wall-clock time to actually apply the chord's effect before the next
    // input runs.
    //
    // Default 200ms — physical mouse-macro tools work with ~30ms for foreground PW,
    // background pump is slower so we add headroom. Bump higher (300-500ms) in
    // appsettings.json if the assist still misses on heavily-loaded background windows.
    public int InterStepDelayMs { get; init; } = 200;
}
