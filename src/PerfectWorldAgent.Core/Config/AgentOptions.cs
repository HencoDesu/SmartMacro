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

    // How often each CharacterAgent's RunLoopAsync ticks (auto-identification poll,
    // future per-mode work, window-death detection).
    public int AgentPollIntervalSeconds { get; init; } = 2;

    public string OllamaEndpoint { get; init; } = "http://localhost:11434";

    public string OllamaModel { get; init; } = "qwen2.5vl:7b";
}
