namespace PerfectWorldAgent.Native;

// Tuning for the ActivatingKeyboardInput / ActivatingMouseInput decorators that wake a
// frozen background PW client via WM_ACTIVATEAPP before delegating input to the inner
// sender.
//
// SettleDelayMs — how long to wait between the wake-up signal and the actual input send.
// Too short and the engine hasn't unfrozen yet; too long and aggregate latency stacks up
// across 9 agents.
//
// ActivationLParam — the magic lParam paired with WM_ACTIVATEAPP. The engine reacts to
// the message itself rather than validating the lParam, but it's exposed in case a future
// client version starts checking. Default 0x91D8 is carried over from a known-working
// third-party helper.
public sealed class ActivatingInputOptions
{
    public int SettleDelayMs { get; init; } = 20;
    public uint ActivationLParam { get; init; } = 0x91D8;
}
