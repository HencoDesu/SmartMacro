using Microsoft.Extensions.Logging;
using PerfectWorldAgent.GameWindows;
using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Presentation;

// Applies per-class taskbar icons to game windows. CharacterAgent delegates here on
// identification — keeps the agent focused on routing input and lifecycle, off the
// concerns of where icon files live, how Russian class names map to English filenames,
// and what to do when a file is missing or corrupt.
//
// Singleton in DI. Stateless beyond the icon-name dictionary; safe to share.
public sealed partial class ClassIconService
{
    // CharacterClass enum uses Russian display names; user's icon files use English
    // class names. Map between them here so the user can drop their existing icons in
    // Assets/ClassIcons/ without renaming. Adjust if a mapping turns out wrong (e.g.
    // Оборотень might be "tank" or "guardian" depending on which PW build the icons
    // came from).
    private static readonly IReadOnlyDictionary<CharacterClass, string> ClassIconNames =
        new Dictionary<CharacterClass, string>
        {
            [CharacterClass.Маг] = "mage",
            [CharacterClass.Воин] = "warrior",
            [CharacterClass.Стрелок] = "gunner",
            [CharacterClass.Друид] = "druid",
            [CharacterClass.Оборотень] = "tank",       // Barbarian / shape-shifter
            [CharacterClass.Странник] = "rover",
            [CharacterClass.Жрец] = "priest",
            [CharacterClass.Лучник] = "archer",
            [CharacterClass.Паладин] = "paladin",
            [CharacterClass.Шаман] = "shaman",
            [CharacterClass.Убийца] = "assassin",
            [CharacterClass.Бард] = "bard",
            [CharacterClass.Мистик] = "mystic",
            [CharacterClass.Страж] = "guardian",
            [CharacterClass.ДухКрови] = "bloodspirit",
            [CharacterClass.Жнец] = "reaper",
            [CharacterClass.Призрак] = "ghost",
            // Канлонг — no icon in the user's current set; will silently skip.
        };

    private readonly ILogger<ClassIconService> _logger;

    public ClassIconService(ILogger<ClassIconService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Best-effort apply of the class icon to a game window. Silent skip for
    /// <see cref="CharacterClass.Unknown"/> — there's nothing to apply for an unidentified
    /// or class-unknown agent. All failures (missing file, unmapped class, GDI+ throw)
    /// are logged at the appropriate level and reported as <c>false</c>.
    /// </summary>
    /// <returns><c>true</c> when the icon was applied; <c>false</c> on any skip or failure.</returns>
    public bool TryApply(IGameWindow window, CharacterClass cls)
    {
        if (cls == CharacterClass.Unknown)
        {
            return false;
        }

        if (!ClassIconNames.TryGetValue(cls, out var fileStem))
        {
            LogClassIconUnmapped(cls);
            return false;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "ClassIcons", $"{fileStem}.png");
        if (!File.Exists(path))
        {
            LogClassIconMissing(cls, path);
            return false;
        }

        try
        {
            if (window.SetIconFromFile(path))
            {
                LogClassIconApplied(cls);
                return true;
            }
            LogClassIconLoadFailed(cls, path);
            return false;
        }
        catch (Exception ex)
        {
            LogClassIconException(ex, cls);
            return false;
        }
    }

    [LoggerMessage(LogLevel.Debug, "Class icon file not found for {Cls} at {Path}; taskbar icon stays default")]
    partial void LogClassIconMissing(CharacterClass cls, string path);

    [LoggerMessage(LogLevel.Information, "Class icon applied for {Cls}")]
    partial void LogClassIconApplied(CharacterClass cls);

    [LoggerMessage(LogLevel.Warning, "Class icon LoadImage failed for {Cls} at {Path} (file may be corrupt or not a valid image)")]
    partial void LogClassIconLoadFailed(CharacterClass cls, string path);

    [LoggerMessage(LogLevel.Error, "Class icon application threw for {Cls}")]
    partial void LogClassIconException(Exception ex, CharacterClass cls);

    [LoggerMessage(LogLevel.Debug, "No icon filename mapping for {Cls}; taskbar icon stays default")]
    partial void LogClassIconUnmapped(CharacterClass cls);
}
