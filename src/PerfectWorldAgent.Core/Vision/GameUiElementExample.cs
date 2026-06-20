using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Config;

namespace PerfectWorldAgent.Vision;

// Filesystem-backed loader for UI-element template PNGs. Scans every *.png under
// Assets/GameUiElements/ and returns them keyed by filename-stem (case-sensitive),
// e.g. "ServerSelectButton.png" → key "ServerSelectButton".
//
// Decoupled from any specific phase / domain enum — callers (CharacterAgent for boot
// detection, future combat-state detection, dialog detection, etc.) look up by string
// name. Add a new template = drop a PNG in the folder + reference its name from
// caller's config. No code change in the loader.
public sealed partial class GameUiElementExample
{
    private readonly GameUiElementExamplesName _uiElementNames;
    private readonly ILogger<GameUiElementExample> _logger;
    private readonly IReadOnlyDictionary<string, byte[]> _templates;

    public GameUiElementExample(
        IOptions<GameUiElementExamplesName> uiElementNames,
        ILogger<GameUiElementExample> logger)
    {
        var templatesDir = Path.Combine(AppContext.BaseDirectory, "Assets", "GameUiElements");

        _uiElementNames = uiElementNames.Value;
        _logger = logger;
        _templates = Load(templatesDir);
    }

    public byte[] ServerSelectButton
        => _templates.GetValueOrDefault(_uiElementNames.ServerSelect)
           ?? throw new InvalidOperationException();

    public byte[] CharacterSelectButton
        => _templates.GetValueOrDefault(_uiElementNames.CharacterSelect)
           ?? throw new InvalidOperationException();

    public byte[] ChatSettingsButton
        => _templates.GetValueOrDefault(_uiElementNames.CharacterSelect)
           ?? throw new InvalidOperationException();

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

        LogLoaded(dict.Count, templatesDir);
        return dict;
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "UI-element templates loaded: {Count} from {Path}")]
    partial void LogLoaded(int count, string path);

    [LoggerMessage(LogLevel.Warning, "UI-element template directory not found at {Path} — WaitForElementAt callers that need templates will fail")]
    partial void LogDirMissing(string path);

    [LoggerMessage(LogLevel.Error, "Failed to load UI-element template '{Name}' from {Path}")]
    partial void LogLoadFailed(Exception ex, string name, string path);

    #endregion
}
