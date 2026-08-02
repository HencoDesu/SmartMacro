namespace SmartMacro.Config;

/// <summary>
/// Infra/runtime settings bound from the "Agent" section of appsettings.json.
///
/// Deliberately tiny: process selection lives in <c>ProcessProfiles</c>, identity in
/// <c>WindowRegistry</c> tags, and every coordinate, region, template, key binding and
/// timeout that used to sit here now lives inside macro nodes where the user can edit it
/// without touching config. All that's left is polling cadence.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>
    /// How often ProcessMonitor polls the OS process list to diff against the previous
    /// snapshot.
    /// </summary>
    public int ProcessPollIntervalSeconds { get; init; } = 1;

    /// <summary>How often each agent checks whether its window is still alive.</summary>
    public int AgentPollIntervalSeconds { get; init; } = 2;
}
