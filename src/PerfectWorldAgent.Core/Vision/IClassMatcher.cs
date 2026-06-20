using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Vision;

// Identifies which character class is displayed in the in-game stats window. The stats
// window is opened by the user / agent (default hotkey C), screenshot captured, then this
// matcher crops the fixed value-text region and template-matches against pre-rendered
// class-name PNGs in Assets/ClassTemplates/.
//
// One template per CharacterClass (provided by the user, harvested from a stats-window
// screenshot of each class). Same conceptual model as the old INameMatcher but keyed by
// CharacterClass enum instead of per-character name.
public interface IClassMatcher
{
    /// <summary>
    /// Matches the screenshot's stats-window class-value region against the given templates.
    /// </summary>
    /// <param name="screenshot">Full client capture of a PW window with the stats panel open.</param>
    /// <param name="templates">Class → template-PNG-bytes map. Typically the full set ClassTemplateLoader provides.</param>
    /// <returns>The matched class above the configured threshold, or <c>null</c> on no match.</returns>
    ClassMatch? Match(byte[] screenshot, IReadOnlyDictionary<CharacterClass, byte[]> templates);

    /// <summary>
    /// Diagnostic — returns the binarised view of the class-value crop region. Used by
    /// the labeling / debug-dump flow so the user can visually verify that the region is
    /// correctly tuned in appsettings.json.
    /// </summary>
    byte[] DebugBinarizeClassRegion(byte[] screenshot);
}

/// <summary>
/// Match result — includes the score for threshold tuning / diagnostics.
/// </summary>
public sealed record ClassMatch(CharacterClass Class, double Score);
