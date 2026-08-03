using System.Globalization;

namespace SmartMacro.Models;

// Позиция в игровом мире, как её считывает с HUD TesseractCoordinateReader. Заморожено вместе
// с ним: единственный живой потребитель — tools/VisionSampleRunner.
//
// Parse() и HorizontalDistanceTo() удалены на стадии 4B — они обслуживали замыслы FOLLOW/HOLD
// и обнаружения застревания, от которых отказались задолго до переписывания на графы нод, и с
// тех пор их никто не вызывал.
public readonly record struct Coordinates(int X, int Z, int Y)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X} {Z} {Y}");
}
