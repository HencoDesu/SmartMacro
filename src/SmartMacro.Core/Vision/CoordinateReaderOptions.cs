namespace SmartMacro.Vision;

// Configuration for TesseractCoordinateReader. TessdataPath points to the directory
// containing eng.traineddata (downloaded into tessdata/ alongside the .exe at build time).
public sealed class CoordinateReaderOptions
{
    public string TessdataPath { get; init; } = "tessdata";
}
