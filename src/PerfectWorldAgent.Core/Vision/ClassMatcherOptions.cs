namespace PerfectWorldAgent.Vision;

// Tuning for ClassMatcher — the crop region of the stats window where "Класс: <name>"
// is rendered, and the binarisation / template-match thresholds.
//
// The user opens the in-game stats window (default hotkey C) once at a fixed position;
// these coords address the value-text area only (the part containing the class name,
// e.g. "Жрец"). Template-matching pre-rendered PNGs of each class name against this
// region returns the matched CharacterClass.
//
// Both region and thresholds are configurable in appsettings.json so the user can tune
// to their own resolution / UI scale without recompiling.
public sealed class ClassMatcherOptions
{
    // Crop rect for the class-value text inside the stats window. Tune via appsettings.json
    // after a first run — defaults are placeholders matching the screenshot the user shared
    // at 2K res. Use the BroadcastDoubleClick trick (cursor → hotkey → log shows client
    // coords) to find the exact top-left and bottom-right of the value text.
    public int RegionX { get; init; } = 1985;
    public int RegionY { get; init; } = 678;
    public int RegionWidth { get; init; } = 130;
    public int RegionHeight { get; init; } = 26;

    // Luminance cutoff for binarisation — class text in the stats window is rendered in
    // light colour on a dark panel; 200 keeps the text core and drops background gradient.
    // Same threshold logic used historically by NameMatcher (which used 240 because the
    // nameplate uses near-white). Stats panel text is dimmer, so lower default.
    public double LuminanceThreshold { get; init; } = 200.0;

    // Cv2.MatchTemplate CCoeffNormed score cutoff. 0.6 was OK for nameplates; the class
    // value text is shorter and has less unique structure, so we may need to raise this
    // if false positives appear in practice.
    public double MatchThreshold { get; init; } = 0.6;
}
