using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartMacro.App.Controls;

/// <summary>
/// The dot grid behind the graph.
///
/// It lives in the VIEWPORT rather than on the transformed surface, and reproduces the
/// pan/zoom itself from <see cref="Zoom"/>/<see cref="OffsetX"/>/<see cref="OffsetY"/>.
/// The alternative — a tiled brush on the surface — would need the surface to be as large
/// as anywhere the user might pan to, and would still stop at its edge. This way the grid
/// is infinite by construction and costs one <c>Render</c> per pan.
/// </summary>
public sealed class CanvasBackdrop : Control
{
    /// <summary>Grid pitch at 100%, from the mockup.</summary>
    private const double BaseSpacing = 22;

    /// <summary>Below this the dots merge into noise, so the pitch doubles instead.</summary>
    private const double MinSpacing = 13;

    private const double DotSize = 1;

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<CanvasBackdrop, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<double> OffsetXProperty =
        AvaloniaProperty.Register<CanvasBackdrop, double>(nameof(OffsetX));

    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<CanvasBackdrop, double>(nameof(OffsetY));

    public CanvasBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Canvas scale, so the grid breathes with the graph.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Canvas pan X in screen pixels.</summary>
    public double OffsetX
    {
        get => GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    /// <summary>Canvas pan Y in screen pixels.</summary>
    public double OffsetY
    {
        get => GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty
            || change.Property == OffsetXProperty
            || change.Property == OffsetYProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var brush = this.TryFindResource("NocturneCanvasGridBrush", out var value) && value is IBrush found
            ? found
            : new SolidColorBrush(Color.FromUInt32(0xFF242737));

        var spacing = BaseSpacing * Math.Max(Zoom, 0.01);
        while (spacing < MinSpacing)
        {
            spacing *= 2;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Start on the first grid line at or before the left/top edge, so the dots stay
        // anchored to canvas space while the viewport moves over them.
        var startX = OffsetX - (Math.Ceiling(OffsetX / spacing) * spacing);
        var startY = OffsetY - (Math.Ceiling(OffsetY / spacing) * spacing);

        for (var x = startX; x < width; x += spacing)
        {
            for (var y = startY; y < height; y += spacing)
            {
                context.FillRectangle(brush, new Rect(x, y, DotSize, DotSize));
            }
        }
    }
}
