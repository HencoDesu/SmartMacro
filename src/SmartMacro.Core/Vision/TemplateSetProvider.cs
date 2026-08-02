using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SmartMacro.Vision;

/// <summary>
/// Resolves the template names macro nodes carry into PNG bytes.
///
/// Two shapes, matching the two node families:
///   * SINGLE template by name (<c>FindElementNode</c>/<c>WaitForElementNode</c>) —
///     <see cref="TryGetTemplate"/>.
///   * A SET of templates keyed by tag (<c>RecognizeTagNode</c>) — <see cref="GetSet"/>,
///     where each key is a file stem and therefore the tag applied on a match.
///
/// Physical layout (W0.2b): the existing asset folders are reused as-is rather than
/// churning them into the <c>templates/{set}/{tag}.png</c> layout the plan sketches —
///   * set <c>"classes"</c>          → <c>Assets/GameClassNames/*.png</c>
///   * any other set <c>"{name}"</c> → <c>Assets/templates/{name}/*.png</c>
///   * single templates by stem      → <c>Assets/GameUiElements/{stem}.png</c>
/// TODO(W0.4): collapse GameUiElements/GameClassNames into one <c>templates/</c> tree and
/// drop the "classes" alias — it only exists so PW's shipped assets keep working untouched.
///
/// Everything is read from disk on first use and cached for process lifetime (templates
/// are small and never change while running); a missing folder or unreadable file is
/// logged and yields an empty result rather than throwing, so one bad PNG can't take the
/// whole macro library down.
/// </summary>
public sealed partial class TemplateSetProvider
{
    /// <summary>Set name that maps onto the legacy <c>Assets/GameClassNames</c> folder.</summary>
    public const string ClassesSetName = "classes";

    private readonly string _assetsRoot;
    private readonly ILogger<TemplateSetProvider> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> _sets = new(StringComparer.Ordinal);
    private readonly Lazy<IReadOnlyDictionary<string, byte[]>> _singleTemplates;

    public TemplateSetProvider(ILogger<TemplateSetProvider> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "Assets"), logger)
    {
    }

    /// <param name="assetsRoot">Folder holding <c>GameUiElements</c> / <c>GameClassNames</c> / <c>templates</c>.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public TemplateSetProvider(string assetsRoot, ILogger<TemplateSetProvider> logger)
    {
        _assetsRoot = assetsRoot;
        _logger = logger;
        _singleTemplates = new Lazy<IReadOnlyDictionary<string, byte[]>>(
            () => LoadFolder("single templates", Path.Combine(_assetsRoot, "GameUiElements")));
    }

    /// <summary>
    /// The named template set as tag → PNG bytes. Unknown or empty sets return an empty
    /// dictionary (logged once) — <c>RecognizeTagNode</c> then simply never matches.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> GetSet(string setName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        return _sets.GetOrAdd(setName, LoadSet);
    }

    /// <summary>
    /// A single template by name. Names are file stems, matched CASE-SENSITIVELY: lookup
    /// goes through the folder listing rather than <c>File.Exists</c>, so a template
    /// resolves the same way here as inside a set — Windows' case-insensitive filesystem
    /// would otherwise make the two disagree.
    /// </summary>
    /// <returns>The PNG bytes, or <c>null</c> when no such template exists.</returns>
    public byte[]? TryGetTemplate(string templateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        if (_singleTemplates.Value.TryGetValue(templateName, out var bytes))
        {
            return bytes;
        }
        LogTemplateMissing(templateName, Path.Combine(_assetsRoot, "GameUiElements"));
        return null;
    }

    private IReadOnlyDictionary<string, byte[]> LoadSet(string setName)
    {
        var directory = string.Equals(setName, ClassesSetName, StringComparison.Ordinal)
            ? Path.Combine(_assetsRoot, "GameClassNames")
            : Path.Combine(_assetsRoot, "templates", setName);
        return LoadFolder(setName, directory);
    }

    // Every *.png in one folder, keyed by file stem. The stem is the identity: a tag for
    // a set, a template name for a single lookup.
    private IReadOnlyDictionary<string, byte[]> LoadFolder(string label, string directory)
    {
        var templates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            LogSetDirMissing(label, directory);
            return templates;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            try
            {
                templates[stem] = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                LogTemplateReadFailed(ex, stem, path);
            }
        }

        LogSetLoaded(label, templates.Count, directory);
        return templates;
    }

    [LoggerMessage(LogLevel.Information, "Template set '{SetName}' loaded: {Count} template(s) from {Path}")]
    partial void LogSetLoaded(string setName, int count, string path);

    [LoggerMessage(LogLevel.Warning, "Template set '{SetName}' has no folder at {Path} — RecognizeTag nodes using it will never match")]
    partial void LogSetDirMissing(string setName, string path);

    [LoggerMessage(LogLevel.Warning, "Template '{TemplateName}' not found in {Path} — Find/Wait nodes using it will never match")]
    partial void LogTemplateMissing(string templateName, string path);

    [LoggerMessage(LogLevel.Error, "Failed to read template '{TemplateName}' from {Path}")]
    partial void LogTemplateReadFailed(Exception ex, string templateName, string path);
}
