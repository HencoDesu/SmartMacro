namespace SmartMacro.GameWindows;

// Tuning for IGameWindow.WaitForElementAt. Bound from "Vision:Window" in appsettings.json.
// Same OpenCV pipeline as the old BootDetector (binarize at LuminanceThreshold → MatchTemplate
// CCoeffNormed → cmp to MatchThreshold) just inlined into GameWindow now.
public sealed class WindowVisionOptions
{
    // Cv2.MatchTemplate CCoeffNormed score cutoff. Above this = element present. Boot
    // templates are usually crisp UI buttons / HUD elements so 0.7 is a safe default.
    public double MatchThreshold { get; init; } = 0.7;

    // Luminance cutoff for binarisation. UI elements in PW are typically bright on dark
    // backgrounds; 200 keeps the bright cores and drops the background.
    public double LuminanceThreshold { get; init; } = 200.0;

    // How often WaitForElementAt re-captures + re-matches. 500ms is the sweet spot:
    // enough headroom for PrintWindow + OpenCV (~50ms total) without wasting CPU.
    public int PollIntervalMs { get; init; } = 500;
}
