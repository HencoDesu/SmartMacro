using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Vision;

// Filesystem-backed loader for the per-class stats-window text templates. One PNG per
// CharacterClass under Assets/ClassTemplates/{enum}.png. Same convention as
// Assets/ClassIcons/, but the user provides these by harvesting tight crops of the
// "Класс: <name>" value from a sample stats-window screenshot per class.
//
// On startup, the loader scans the dir and caches whatever templates exist. Missing
// templates are tolerated — the corresponding classes simply can't be matched. The
// log warns once with a list of missing classes so the user knows which screenshots
// they still need to provide.
public sealed partial class ClassTemplateLoader
{
    private readonly ILogger<ClassTemplateLoader> _logger;
    private readonly IReadOnlyDictionary<CharacterClass, byte[]> _templates;

    public ClassTemplateLoader(ILogger<ClassTemplateLoader> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "Assets", "ClassTemplates"), logger)
    {
    }

    public ClassTemplateLoader(string templatesDir, ILogger<ClassTemplateLoader> logger)
    {
        _logger = logger;
        _templates = Load(templatesDir);
    }

    /// <summary>
    /// All successfully-loaded templates keyed by class. Hand to <see cref="IClassMatcher.Match"/>.
    /// </summary>
    public IReadOnlyDictionary<CharacterClass, byte[]> Templates => _templates;

    private IReadOnlyDictionary<CharacterClass, byte[]> Load(string templatesDir)
    {
        var dict = new Dictionary<CharacterClass, byte[]>();
        var missing = new List<CharacterClass>();

        if (!Directory.Exists(templatesDir))
        {
            LogDirMissing(templatesDir);
            return dict;
        }

        foreach (CharacterClass cls in Enum.GetValues<CharacterClass>())
        {
            if (cls == CharacterClass.Unknown)
            {
                continue;
            }

            var path = Path.Combine(templatesDir, $"{cls}.png");
            if (!File.Exists(path))
            {
                missing.Add(cls);
                continue;
            }

            try
            {
                dict[cls] = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                LogLoadFailed(ex, cls, path);
            }
        }

        LogLoaded(dict.Count);
        if (missing.Count > 0)
        {
            LogMissing(missing.Count, string.Join(", ", missing));
        }
        return dict;
    }

    [LoggerMessage(LogLevel.Information, "Class templates loaded: {Count} from disk")]
    partial void LogLoaded(int count);

    [LoggerMessage(LogLevel.Warning, "Class template directory not found at {Path} — identification will fail until templates are provided")]
    partial void LogDirMissing(string path);

    [LoggerMessage(LogLevel.Warning, "Missing class template(s) for {Count} class(es): {List}")]
    partial void LogMissing(int count, string list);

    [LoggerMessage(LogLevel.Error, "Failed to load class template for {Cls} from {Path}")]
    partial void LogLoadFailed(Exception ex, CharacterClass cls, string path);
}
