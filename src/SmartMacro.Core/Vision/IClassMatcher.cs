using SmartMacro.Native;

namespace SmartMacro.Vision;

// Picks the best-matching template out of a SET, inside a caller-chosen region of a
// window capture. Backs RecognizeTagNode: the winning template's key becomes the tag.
//
// Templates are keyed by free-form tag strings (filename stems of the set on disk). The
// canonical PW use is the in-game stats window: open it (default hotkey C), capture, and
// match the "Класс: <name>" value area against pre-rendered class-name PNGs.
public interface IClassMatcher
{
    /// <summary>
    /// Matches <paramref name="region"/> of the screenshot against the given templates.
    /// </summary>
    /// <param name="screenshot">Full client capture of the window.</param>
    /// <param name="templates">Tag → template-PNG-bytes map, e.g. one template set from <c>TemplateSetProvider</c>.</param>
    /// <param name="region">Client-space crop the set is matched against. Empty (Width or Height ≤ 0) = whole capture.</param>
    /// <returns>The matched tag above the configured threshold, or <c>null</c> on no match.</returns>
    TagMatch? Match(byte[] screenshot, IReadOnlyDictionary<string, byte[]> templates, ScreenRect region);

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
