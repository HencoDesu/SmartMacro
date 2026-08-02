using SmartMacro.Models;

namespace SmartMacro.Vision;

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
    /// <summary>
    /// Reads the coordinates HUD region from a screenshot.
    /// </summary>
    /// <returns>The parsed coordinates, or <c>null</c> when the region is missing / OCR fails / the parse doesn't yield three integers.</returns>
    Coordinates? Read(byte[] screenshot);

    /// <summary>
    /// Diagnostic — returns the raw cropped coord region. Used by the sample runner to
    /// visualise/tune the preprocessing pipeline.
    /// </summary>
    byte[] DebugCrop(byte[] screenshot);

    /// <summary>
    /// Diagnostic — returns the binarised view that Tesseract actually sees.
    /// </summary>
    byte[] DebugBinarize(byte[] screenshot);
}
