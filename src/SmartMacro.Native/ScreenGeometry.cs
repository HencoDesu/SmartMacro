namespace SmartMacro.Native;

/// <summary>
/// Pixel-space point. Used wherever we'd otherwise pass an (x, y) pair: click coords,
/// cursor positions, button locations. Lives in the Native namespace alongside the
/// other primitive types (VirtualKey, MouseButton).
/// </summary>
/// <remarks>
/// Record-struct so it round-trips cleanly through Microsoft.Extensions.Configuration
/// binding (positional ctor params have setters via init under the hood). Default
/// values on the parameters let the binder construct via the default ctor if the
/// JSON section is empty.
/// </remarks>
public readonly record struct ScreenPoint(int X = 0, int Y = 0)
{
    public override string ToString() => $"({X},{Y})";
}

/// <summary>
/// Pixel-space rectangle. Used for vision crop regions, button bounding boxes, anything
/// that needs (x, y, w, h). Same record-struct trick as <see cref="ScreenPoint"/> for
/// config binding.
/// </summary>
public readonly record struct ScreenRect(int X = 0, int Y = 0, int Width = 0, int Height = 0)
{
    public ScreenPoint TopLeft => new(X, Y);
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}
