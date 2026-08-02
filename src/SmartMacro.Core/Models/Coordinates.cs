using System.Globalization;

namespace SmartMacro.Models;

// In-world position as read off the HUD by TesseractCoordinateReader. Parked alongside it:
// the only live consumer is tools/VisionSampleRunner.
//
// Parse() and HorizontalDistanceTo() were deleted in stage 4B — they served the
// FOLLOW/HOLD and stuck-detection designs that were dropped long before the node-graph
// rewrite, and nothing had called them since.
public readonly record struct Coordinates(int X, int Z, int Y)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X} {Z} {Y}");
}
