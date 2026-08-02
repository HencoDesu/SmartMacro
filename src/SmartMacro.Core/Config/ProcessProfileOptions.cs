namespace SmartMacro.Config;

/// <summary>
/// Options wrapper around the "ProcessProfiles" appsettings array. The section is a raw
/// JSON array, so the composition root binds it via
/// <c>configuration.GetSection(ProcessProfileOptions.SectionName).Bind(options.Profiles)</c>.
/// </summary>
public sealed class ProcessProfileOptions
{
    /// <summary>Configuration section name holding the profile array.</summary>
    public const string SectionName = "ProcessProfiles";

    /// <summary>All configured process profiles. Empty = no processes are watched.</summary>
    public List<ProcessProfile> Profiles { get; init; } = [];

    /// <summary>
    /// Finds the profile for a process name (case-insensitive — Windows process names
    /// are not case-sensitive). First match wins when duplicates are configured.
    /// </summary>
    /// <returns>The matching profile, or <c>null</c> when the process has no configured entry.</returns>
    public ProcessProfile? FindByProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        foreach (var profile in Profiles)
        {
            if (string.Equals(profile.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }
        return null;
    }

    /// <summary>
    /// The union of process names ProcessMonitor should poll: distinct (case-insensitive),
    /// blank entries skipped, original order preserved.
    /// </summary>
    public IReadOnlyList<string> GetWatchedProcessNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(Profiles.Count);
        foreach (var profile in Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.ProcessName))
            {
                continue;
            }
            if (seen.Add(profile.ProcessName))
            {
                names.Add(profile.ProcessName);
            }
        }
        return names;
    }
}
