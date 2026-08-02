namespace SmartMacro.Vision;

// Identifies which tag template is displayed in the in-game stats window. The stats
// window is opened by the user / agent (default hotkey C), screenshot captured, then this
// matcher crops the fixed value-text region and template-matches against pre-rendered
// class-name PNGs from Assets/GameClassNames/ (filename stem = tag).
//
// Templates are keyed by free-form tag strings — the matched key becomes the window's
// tag in WindowRegistry. The user provides templates by harvesting tight crops of the
// "Класс: <name>" value from a sample stats-window screenshot per class.
public interface IClassMatcher
{
    /// <summary>
    /// Matches the screenshot's stats-window class-value region against the given templates.
    /// </summary>
    /// <param name="screenshot">Full client capture of a PW window with the stats panel open.</param>
    /// <param name="templates">Tag → template-PNG-bytes map. Typically the full set ClassTemplateLoader provides.</param>
    /// <returns>The matched tag above the configured threshold, or <c>null</c> on no match.</returns>
    TagMatch? Match(byte[] screenshot, IReadOnlyDictionary<string, byte[]> templates);

    /// <summary>
    /// Diagnostic — returns the binarised view of the class-value crop region. Used by
    /// the labeling / debug-dump flow so the user can visually verify that the region is
    /// correctly tuned in appsettings.json.
    /// </summary>
    byte[] DebugBinarizeClassRegion(byte[] screenshot);
}

/// <summary>
/// Match result — the winning template's tag plus the score for threshold tuning / diagnostics.
/// </summary>
public sealed record TagMatch(string Tag, double Score);
