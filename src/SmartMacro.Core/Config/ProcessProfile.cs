namespace SmartMacro.Config;

/// <summary>
/// Per-process activation profile. Bound from an element of the "ProcessProfiles" array
/// in appsettings.json. Replaces the single global Agent:GameProcessName + Input:Activating
/// activation tuning — the WM_ACTIVATEAPP wake-up dance is a Perfect World quirk, so it
/// lives with the process it belongs to. ProcessMonitor watches the union of all profile
/// process names; GameWindowFactory picks the matching profile per window.
/// </summary>
public sealed class ProcessProfile
{
    /// <summary>OS-level process name (no extension), e.g. "elementclient_64".</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// Magic lParam paired with WM_ACTIVATEAPP to wake a frozen client before input.
    /// <c>null</c> = plain input: no wake-up signal is sent and no deactivation follows.
    /// </summary>
    public uint? ActivationLParam { get; init; }

    /// <summary>Wait after the wake-up signal before sending input (engine unfreeze time).</summary>
    public int SettleDelayMs { get; init; }

    /// <summary>Wait before the deactivation signal so the target's message pump drains queued input.</summary>
    public int DeactivationDelayMs { get; init; }

    /// <summary>
    /// Fallback profile for windows whose process has no configured entry: plain input,
    /// no delays, no wake-up dance.
    /// </summary>
    public static ProcessProfile Inert { get; } = new();
}
