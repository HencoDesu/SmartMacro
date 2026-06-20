using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.Config;

// Infra/runtime settings bound from the "Agent" section of appsettings.json.
// Separate from the per-character roster (handled by ICharacterRoster + roster.json) so
// runtime tuning and roster editing have different change cadences.
public sealed class AgentOptions
{
    // The OS-level process name to watch via ProcessMonitor.
    public string GameProcessName { get; init; } = "elementclient";

    // How often ProcessMonitor polls Process.GetProcessesByName to diff against the
    // previous snapshot.
    public int ProcessPollIntervalSeconds { get; init; } = 1;

    // How often each CharacterAgent's RunLoopAsync ticks for window-death detection +
    // inbox-drain wake-up. Identification is no longer polling-based (see StatsHotkey).
    public int AgentPollIntervalSeconds { get; init; } = 2;

    // In-game hotkey that toggles the stats window. Used by IdentifyAsync to open the
    // stats panel, capture the class-text region, then close again. PW default is C.
    public VirtualKey StatsHotkey { get; init; } = VirtualKey.C;

    // Wall-clock delay between sending the stats-open hotkey and capturing the
    // screenshot. PW renders the stats panel asynchronously; ~500ms covers a frozen
    // background client. Tune up if the binarised class region comes back empty.
    public int StatsOpenDelayMs { get; init; } = 500;
}
