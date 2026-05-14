namespace PerfectWorldAgent.Vision;

// Identifies which character is on screen by comparing the HUD name region against a set
// of reference templates (one per known character). Templates are typically harvested via
// Orchestrator.LabelAgentAsync — tight crops of the HUD name plate captured when the user
// labels an unidentified agent.
//
// All inputs/outputs are raw image bytes (PNG / JPEG-decoded by the implementation) so the
// interface stays free of OpenCV types.
public interface INameMatcher
{
    // Returns the name of the best-matching template if its similarity crosses the
    // implementation-defined threshold; otherwise null. `screenshot` is the full game
    // window capture — the matcher crops the HUD region itself.
    NameMatch? Match(byte[] screenshot, IReadOnlyDictionary<string, byte[]> templates);

    // Crops `screenshot` to just the HUD name region. Used by Orchestrator.LabelAgentAsync
    // to harvest a fresh template before persisting it into the roster.
    byte[] CropNameRegion(byte[] screenshot);

    // Diagnostics — return the binarised view that MatchTemplate actually sees. Used by
    // the sample runner to visualise/tune the binarisation pipeline.
    byte[] DebugBinarizeFullImage(byte[] imageBytes);
    byte[] DebugBinarizeNameRegion(byte[] screenshot);

    // How many pixels in the binarised search region pass the luminance threshold,
    // out of the total. Used by the labeling dialog to warn the user when the captured
    // template is sparse — typical when the nameplate is rendered in a non-white color
    // (e.g. PW colours low-level / store characters in blue, which doesn't binarise).
    // Sparse templates produce garbage matches against other characters' windows.
    (int Bright, int Total) GetNameplatePixelStats(byte[] screenshot);
}

// Result carrier — includes the score for diagnostics / threshold tuning.
public sealed record NameMatch(string Name, double Score);
