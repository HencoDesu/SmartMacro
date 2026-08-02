using Microsoft.Extensions.Logging;

namespace SmartMacro.Vision;

// Filesystem-backed loader for the stats-window tag-template set. Scans every *.png
// under Assets/GameClassNames/ and keys it by filename stem — the stem IS the tag that
// gets applied to the window on a match ("Лучник.png" → tag "Лучник"). Sibling to
// Assets/GameUiElements/ (boot UI templates) and Assets/ClassIcons/ (taskbar icons).
//
// Adding a recognisable tag = dropping a PNG in the folder; no enum, no code change.
// Bytes are read as-is at startup — decoding happens later in ClassMatcher, so a broken
// file surfaces as a match-time error, not a load-time crash.
public sealed partial class ClassTemplateLoader
{
    private readonly ILogger<ClassTemplateLoader> _logger;
    private readonly IReadOnlyDictionary<string, byte[]> _templates;

    public ClassTemplateLoader(ILogger<ClassTemplateLoader> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "Assets", "GameClassNames"), logger)
    {
    }

    public ClassTemplateLoader(string templatesDir, ILogger<ClassTemplateLoader> logger)
    {
        _logger = logger;
        _templates = Load(templatesDir);
    }

    /// <summary>
    /// All successfully-loaded templates keyed by tag (case-sensitive filename stem).
    /// Hand to <see cref="IClassMatcher.Match"/>.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> Templates => _templates;

    private Dictionary<string, byte[]> Load(string templatesDir)
    {
        var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (!Directory.Exists(templatesDir))
        {
            LogDirMissing(templatesDir);
            return dict;
        }

        foreach (var path in Directory.EnumerateFiles(templatesDir, "*.png"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            try
            {
                dict[stem] = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                LogLoadFailed(ex, stem, path);
            }
        }

        LogLoaded(dict.Count);
        return dict;
    }

    [LoggerMessage(LogLevel.Information, "Tag templates loaded: {Count} from disk")]
    partial void LogLoaded(int count);

    [LoggerMessage(LogLevel.Warning, "Tag template directory not found at {Path} — identification will fail until PNGs are provided in Assets/GameClassNames/{{Tag}}.png")]
    partial void LogDirMissing(string path);

    [LoggerMessage(LogLevel.Error, "Failed to load tag template '{Tag}' from {Path}")]
    partial void LogLoadFailed(Exception ex, string tag, string path);
}
