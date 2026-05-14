using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Vision;

// Reads the player's in-game coordinates from the HUD by OCR'ing the top-right "X, Z · Y"
// indicator on a screenshot. Returns null when:
//   * the screenshot doesn't contain a recognizable coord region (wrong UI scale,
//     character-select screen, etc.)
//   * OCR confidence is too low or the parsed text doesn't yield three integers
//
// All inputs/outputs are raw image bytes / value types so the interface stays free of
// OpenCV/Tesseract types.
public interface ICoordinateReader
{
    Coordinates? Read(byte[] screenshot);

    // Diagnostics — return the raw cropped region and the binarised view that Tesseract
    // sees. Used by the sample runner to visualise/tune the preprocessing pipeline.
    byte[] DebugCrop(byte[] screenshot);
    byte[] DebugBinarize(byte[] screenshot);
}
