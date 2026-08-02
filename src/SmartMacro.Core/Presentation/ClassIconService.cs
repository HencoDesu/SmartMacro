using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;

namespace SmartMacro.Presentation;

// Applies per-tag taskbar icons to game windows. CharacterAgent delegates here on
// identification — keeps the agent focused on routing input and lifecycle, off the
// concerns of where icon files live, how Russian tag names map to English filenames,
// and what to do when a file is missing or corrupt.
//
// Singleton in DI. No state beyond the icon-name dictionary; safe to share.
public sealed partial class ClassIconService
{
    // Tags produced by identification use Russian display names (template filename
    // stems); the user's icon files use English class names. Map between them here so
    // the user can drop their existing icons in Assets/ClassIcons/ without renaming.
    // Adjust if a mapping turns out wrong (e.g. Оборотень might be "tank" or "guardian"
    // depending on which PW build the icons came from). Unmapped tags silently skip.
    // TODO(W0.2): replaced by SetIconNode with a {tag}-templated icon path.
    private static readonly IReadOnlyDictionary<string, string> TagIconNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Маг"] = "mage",
            ["Воин"] = "warrior",
            ["Стрелок"] = "gunner",
            ["Друид"] = "druid",
            ["Оборотень"] = "tank",       // Barbarian / shape-shifter
            ["Странник"] = "rover",
            ["Жрец"] = "priest",
            ["Лучник"] = "archer",
            ["Паладин"] = "paladin",
            ["Шаман"] = "shaman",
            ["Убийца"] = "assassin",
            ["Бард"] = "bard",
            ["Мистик"] = "mystic",
            ["Страж"] = "guardian",
            ["ДухКрови"] = "bloodspirit",
            ["Жнец"] = "reaper",
            ["Призрак"] = "ghost",
            // Канлонг — no icon in the user's current set; will silently skip.
        };

    private readonly ILogger<ClassIconService> _logger;

    public ClassIconService(ILogger<ClassIconService> logger)
    {
        _logger = logger;
    }

    // Retry cadence for post-boot re-application. Racing PW's own post-load init
    // that occasionally resets our WM_SETICON. Cheap re-sends: SendMessage(WM_SETICON)
    // is a few Win32 calls each, cached HICON reused. Idempotent — if PW never resets
    // ours, these are no-ops.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>
    /// Best-effort apply of the tag's icon to a game window. Silent skip for empty or
    /// unmapped tags. All failures logged; reported as <c>false</c>.
    /// After the initial apply, schedules two background retries at +2s and +5s to
    /// survive PW's post-boot init step that sometimes overwrites the taskbar icon
    /// when the agent auto-drove the client through server-select → in-world.
    /// </summary>
    /// <returns><c>true</c> when the initial apply succeeded; <c>false</c> on any skip or failure.</returns>
    public bool TryApply(IGameWindow window, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        if (!TagIconNames.TryGetValue(tag, out var fileStem))
        {
            LogTagIconUnmapped(tag);
            return false;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "ClassIcons", $"{fileStem}.png");
        if (!File.Exists(path))
        {
            LogTagIconMissing(tag, path);
            return false;
        }

        var applied = ApplyOnce(window, tag, path);

        // Fire-and-forget retries. Even if initial apply failed, retry — PW might have
        // still been mid-init and rejected/dropped the SendMessage; a later re-send
        // could stick.
        _ = Task.Run(async () =>
        {
            foreach (var delay in RetryDelays)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    if (!window.IsAlive) return;
                    ApplyOnce(window, tag, path);
                }
                catch (Exception ex)
                {
                    LogTagIconException(ex, tag);
                    return;
                }
            }
        });

        return applied;
    }

    private bool ApplyOnce(IGameWindow window, string tag, string path)
    {
        try
        {
            if (window.SetIconFromFile(path))
            {
                LogTagIconApplied(tag);
                return true;
            }
            LogTagIconLoadFailed(tag, path);
            return false;
        }
        catch (Exception ex)
        {
            LogTagIconException(ex, tag);
            return false;
        }
    }

    [LoggerMessage(LogLevel.Debug, "Tag icon file not found for '{Tag}' at {Path}; taskbar icon stays default")]
    partial void LogTagIconMissing(string tag, string path);

    [LoggerMessage(LogLevel.Information, "Tag icon applied for '{Tag}'")]
    partial void LogTagIconApplied(string tag);

    [LoggerMessage(LogLevel.Warning, "Tag icon LoadImage failed for '{Tag}' at {Path} (file may be corrupt or not a valid image)")]
    partial void LogTagIconLoadFailed(string tag, string path);

    [LoggerMessage(LogLevel.Error, "Tag icon application threw for '{Tag}'")]
    partial void LogTagIconException(Exception ex, string tag);

    [LoggerMessage(LogLevel.Debug, "No icon filename mapping for tag '{Tag}'; taskbar icon stays default")]
    partial void LogTagIconUnmapped(string tag);
}
