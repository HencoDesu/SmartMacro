using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartMacro.App.Controls;

/// <summary>
/// Точечная сетка за графом.
///
/// Живёт она в ОБЛАСТИ ПРОСМОТРА, а не на трансформируемой поверхности, и воспроизводит
/// панораму и масштаб сама, из <see cref="Zoom"/>/<see cref="OffsetX"/>/<see cref="OffsetY"/>.
/// Альтернатива — замощающая кисть на самой поверхности — потребовала бы, чтобы поверхность
/// была размером со всё, куда пользователь может увести панораму, и всё равно обрывалась бы на
/// её краю. А так сетка бесконечна по построению и стоит одного <c>Render</c> на панораму.
/// </summary>
public sealed class CanvasBackdrop : Control
{
    /// <summary>Шаг сетки на 100%, из макета.</summary>
    private const double BaseSpacing = 22;

    /// <summary>Ниже этого точки сливаются в шум, поэтому шаг вместо того удваивается.</summary>
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

    /// <summary>Масштаб canvas — чтобы сетка дышала вместе с графом.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Панорама canvas по X, в экранных пикселях.</summary>
    public double OffsetX
    {
        get => GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    /// <summary>Панорама canvas по Y, в экранных пикселях.</summary>
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

        // Начинаем с первой линии сетки на левом/верхнем крае или до него — так точки остаются
        // привязанными к пространству canvas, пока область просмотра ездит поверх них.
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
