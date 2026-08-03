namespace SmartMacro.Config;

/// <summary>
/// Обёртка-опции вокруг массива "ProcessProfiles" из appsettings. Секция там — голый
/// JSON-массив, поэтому корень композиции привязывает её через
/// <c>configuration.GetSection(ProcessProfileOptions.SectionName).Bind(options.Profiles)</c>.
/// </summary>
public sealed class ProcessProfileOptions
{
    /// <summary>Имя секции конфигурации, в которой лежит массив профилей.</summary>
    public const string SectionName = "ProcessProfiles";

    /// <summary>Все настроенные профили процессов. Пусто = ни за одним процессом не следим.</summary>
    public List<ProcessProfile> Profiles { get; init; } = [];

    /// <summary>
    /// Находит профиль по имени процесса (без учёта регистра — в Windows имена процессов
    /// регистронезависимы). Если настроены дубли, выигрывает первое совпадение.
    /// </summary>
    /// <returns>Подходящий профиль или <c>null</c>, если такого процесса в конфиге нет.</returns>
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
    /// Объединение имён процессов, которые ProcessMonitor должен опрашивать: без повторов (без
    /// учёта регистра), пустые записи пропущены, исходный порядок сохранён.
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
