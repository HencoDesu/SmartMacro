using System.Globalization;

namespace SmartMacro.Models;

public readonly record struct Coordinates(int X, int Z, int Y)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X} {Z} {Y}");

    public static Coordinates Parse(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new FormatException($"Expected 3 space-separated integers, got: '{text}'");
        }

        return new Coordinates(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    // Euclidean distance on the horizontal plane (X/Z). Y is elevation; height
    // differences don't count for "how far apart are we" — a character on a tower
    // overhead is still "close" for follow/stuck logic.
    public int HorizontalDistanceTo(Coordinates other)
    {
        var dx = X - other.X;
        var dz = Z - other.Z;
        return (int)Math.Sqrt(dx * dx + dz * dz);
    }
}
