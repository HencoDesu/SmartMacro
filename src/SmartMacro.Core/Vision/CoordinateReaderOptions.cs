namespace SmartMacro.Vision;

// Настройки TesseractCoordinateReader. TessdataPath указывает на каталог с eng.traineddata
// (он скачивается в tessdata/ рядом с .exe на этапе сборки).
public sealed class CoordinateReaderOptions
{
    public string TessdataPath { get; init; } = "tessdata";
}
