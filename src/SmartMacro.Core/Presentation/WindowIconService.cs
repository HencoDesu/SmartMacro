using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;

namespace SmartMacro.Presentation;

/// <summary>
/// Applies an icon file to a game window's title-bar / taskbar slot. Backs
/// <c>SetIconNode</c>, whose <c>IconPath</c> the executor has already interpolated
/// (e.g. <c>"Assets/ClassIcons/{tag}.png"</c> → <c>"Assets/ClassIcons/Лучник.png"</c>).
///
/// Two things it owns beyond the raw <see cref="IGameWindow.SetIconFromFile"/> call:
///   * path resolution — relative paths resolve against the app directory, so macro
///     files stay portable;
///   * the post-apply retry pair — PW's own post-load init occasionally resets our
///     WM_SETICON, so the icon is re-sent at +2s and +5s. Cheap and idempotent (the
///     HICON is cached in Native.WindowIconCache).
///
/// Singleton in DI; no state beyond the legacy alias table.
/// </summary>
public sealed partial class WindowIconService
{
    /// <summary>
    /// Fallback for PW's shipped icon set: identification tags are Russian class names
    /// while <c>Assets/ClassIcons</c> ships English file stems. When the interpolated path
    /// doesn't exist we retry once through this table so the <c>pw-boot</c> /
    /// <c>pw-identify</c> examples work against the assets already in the repo.
    /// TODO(W0.4): delete along with renaming the icon files to their tag names — the
    /// engine itself has no business knowing PW class vocabulary.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyTagIconStems =
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

    // Retry cadence for post-apply re-application, racing PW's post-load init.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly ILogger<WindowIconService> _logger;

    public WindowIconService(ILogger<WindowIconService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Best-effort apply of <paramref name="iconPath"/> to <paramref name="window"/>, plus
    /// two background retries. Every failure is logged and reported, never thrown — a
    /// cosmetic icon must not abort a macro run.
    /// </summary>
    /// <returns><c>true</c> when the initial apply succeeded.</returns>
    public bool TryApply(IGameWindow window, string iconPath)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return false;
        }

        var resolved = ResolvePath(iconPath);
        if (resolved is null)
        {
            LogIconMissing(iconPath);
            return false;
        }

        var applied = ApplyOnce(window, resolved);

        // Fire-and-forget retries. Even if the initial apply failed, retry — PW might
        // have been mid-init and dropped the SendMessage; a later re-send could stick.
        _ = Task.Run(async () =>
        {
            foreach (var delay in RetryDelays)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    if (!window.IsAlive)
                    {
                        return;
                    }
                    ApplyOnce(window, resolved);
                }
                catch (Exception ex)
                {
                    LogIconException(ex, resolved);
                    return;
                }
            }
        });

        return applied;
    }

    // Relative → app directory. On a miss, try the legacy Russian-tag → English-stem
    // alias in the same folder before giving up.
    private static string? ResolvePath(string iconPath)
    {
        var absolute = Path.IsPathRooted(iconPath)
            ? iconPath
            : Path.Combine(AppContext.BaseDirectory, iconPath);
        if (File.Exists(absolute))
        {
            return absolute;
        }

        var stem = Path.GetFileNameWithoutExtension(absolute);
        if (!LegacyTagIconStems.TryGetValue(stem, out var alias))
        {
            return null;
        }

        var aliased = Path.Combine(
            Path.GetDirectoryName(absolute) ?? AppContext.BaseDirectory,
            alias + Path.GetExtension(absolute));
        return File.Exists(aliased) ? aliased : null;
    }

    private bool ApplyOnce(IGameWindow window, string path)
    {
        try
        {
            if (window.SetIconFromFile(path))
            {
                LogIconApplied(path);
                return true;
            }
            LogIconLoadFailed(path);
            return false;
        }
        catch (Exception ex)
        {
            LogIconException(ex, path);
            return false;
        }
    }

    [LoggerMessage(LogLevel.Debug, "Icon file not found for '{IconPath}' (nor under its legacy alias); window icon stays default")]
    partial void LogIconMissing(string iconPath);

    [LoggerMessage(LogLevel.Information, "Window icon applied from {Path}")]
    partial void LogIconApplied(string path);

    [LoggerMessage(LogLevel.Warning, "Window icon load failed for {Path} (file may be corrupt or not a valid image)")]
    partial void LogIconLoadFailed(string path);

    [LoggerMessage(LogLevel.Error, "Window icon application threw for {Path}")]
    partial void LogIconException(Exception ex, string path);
}
