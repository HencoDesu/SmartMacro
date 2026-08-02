namespace SmartMacro.GameWindows;

// Tuning for IGameWindow.FindElementAsync/WaitForElementAsync. Bound from "Vision:Window" in
// appsettings.json. Pipeline: grayscale → MatchTemplate CCoeffNormed → cmp to MatchThreshold.
//
// There is no LuminanceThreshold here (stage 4B removed the unread property): this path
// deliberately does NOT binarise. Game-UI elements sit on semi-transparent backgrounds where
// bleed-through from the world below makes a fixed luminance cut unstable. ClassMatcher is
// the exception that still binarises — solid text on a solid stats panel.
public sealed class WindowVisionOptions
{
    // Cv2.MatchTemplate CCoeffNormed score cutoff. Above this = element present. Boot
    // templates are usually crisp UI buttons / HUD elements so 0.7 is a safe default.
    public double MatchThreshold { get; init; } = 0.7;

    // How often WaitForElementAsync re-captures + re-matches. 500ms is the sweet spot:
    // enough headroom for PrintWindow + OpenCV (~50ms total) without wasting CPU.
    public int PollIntervalMs { get; init; } = 500;
}
