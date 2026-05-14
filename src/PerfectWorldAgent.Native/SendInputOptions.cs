namespace PerfectWorldAgent.Native;

// Tuning for the SendInputKeyboardInput / SendInputMouseInput senders. SendInput pushes
// through the full input pipeline (DirectInput, Raw Input) and requires the target to be
// foreground — so these timings cover both the foreground-switch settle and the per-
// stroke / per-click cadence.
public sealed class SendInputOptions
{
    public int FocusSettleDelayMs { get; init; } = 30;
    public int KeyHoldDurationMs { get; init; } = 50;
    public int InterClickDelayMs { get; init; } = 50;

    // When true, the original foreground window is restored after the send. Disabled by
    // default — restoring focus is jarring across 9 rapid sends.
    public bool RestoreOriginalFocus { get; init; } = false;
}
