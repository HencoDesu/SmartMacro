using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartMacro.App.ViewModels.Canvas;

namespace SmartMacro.App.Controls;

/// <summary>
/// Draws every edge of the graph in one pass.
///
/// One control rather than a <c>Path</c> per edge on purpose: dragging a node re-routes
/// every line it touches, and rebuilding a visual tree at pointer-move rate is exactly the
/// kind of thing that makes a canvas feel heavy. Here a drag is one <c>InvalidateVisual</c>.
///
/// Geometry comes from <see cref="CanvasEdgeViewModel"/> — the router is a view-model so it
/// can be tested without a rendering platform, and this class only turns waypoints into
/// strokes. It is never hit-testable: clicks belong to the boxes underneath.
/// </summary>
public sealed class EdgeLayer : Control
{
    /// <summary>Corner radius where a routed path turns.</summary>
    private const double CornerRadius = 10;

    /// <summary>Half-length of the arrow head at an edge's tip.</summary>
    private const double ArrowLength = 7;

    /// <summary>Half-width of the arrow head.</summary>
    private const double ArrowHalfWidth = 3.5;

    /// <summary>How much of the horizontal gap a direct hop's bezier handles take up.</summary>
    private const double DirectCurveTension = 0.45;

    public static readonly StyledProperty<IEnumerable?> EdgesProperty =
        AvaloniaProperty.Register<EdgeLayer, IEnumerable?>(nameof(Edges));

    /// <summary>Start of the link being dragged out of a port, in canvas space.</summary>
    public static readonly StyledProperty<Point?> PendingStartProperty =
        AvaloniaProperty.Register<EdgeLayer, Point?>(nameof(PendingStart));

    /// <summary>Current pointer position while a link is being dragged, in canvas space.</summary>
    public static readonly StyledProperty<Point?> PendingEndProperty =
        AvaloniaProperty.Register<EdgeLayer, Point?>(nameof(PendingEnd));

    private INotifyCollectionChanged? _observed;

    public EdgeLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    /// <summary>The routed edges to draw.</summary>
    public IEnumerable? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    public Point? PendingStart
    {
        get => GetValue(PendingStartProperty);
        set => SetValue(PendingStartProperty, value);
    }

    public Point? PendingEnd
    {
        get => GetValue(PendingEndProperty);
        set => SetValue(PendingEndProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EdgesProperty)
        {
            Rebind(change.NewValue as IEnumerable);
            InvalidateVisual();
        }
        else if (change.Property == PendingStartProperty || change.Property == PendingEndProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var idle = Brush("NocturneEdgeBrush", 0xFF464B61);
        var active = Brush("NocturneEdgeActiveBrush", 0xFF9184D9);

        if (Edges is { } edges)
        {
            foreach (var item in edges)
            {
                if (item is CanvasEdgeViewModel edge)
                {
                    DrawEdge(context, edge, edge.IsActive ? active : idle, edge.IsActive ? 1.6 : 1.4);
                }
            }
        }

        if (PendingStart is { } start && PendingEnd is { } end)
        {
            // The link being dragged: dashed accent, so it reads as a proposal rather than
            // as a wire that already exists.
            var pen = new Pen(active, 1.6, new DashStyle([4, 3], 0));
            context.DrawLine(pen, start, end);
            context.DrawEllipse(active, null, end, 3, 3);
        }
    }

    private void DrawEdge(DrawingContext context, CanvasEdgeViewModel edge, IBrush brush, double thickness)
    {
        var points = edge.Waypoints;
        if (points.Count < 2)
        {
            return;
        }

        var geometry = edge.IsDirect
            ? DirectGeometry(points[0], points[^1])
            : RoutedGeometry(points);
        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);

        var tip = ToPoint(points[^1]);
        var previous = ToPoint(points[^2]);
        DrawArrow(context, brush, previous, tip);
    }

    // A short S between two boxes on the same row, matching the mockup's cubic.
    private static StreamGeometry DirectGeometry(CanvasPoint from, CanvasPoint to)
    {
        var geometry = new StreamGeometry();
        using var sink = geometry.Open();
        var dx = Math.Max(to.X - from.X, 1) * DirectCurveTension;
        sink.BeginFigure(ToPoint(from), isFilled: false);
        sink.CubicBezierTo(
            new Point(from.X + dx, from.Y),
            new Point(to.X - dx, to.Y),
            ToPoint(to));
        sink.EndFigure(false);
        return geometry;
    }

    // Right out of the box, along the gutter above the target row, down into its top edge.
    // Corners are rounded with a quadratic so the path reads as one move, not four.
    private static StreamGeometry RoutedGeometry(IReadOnlyList<CanvasPoint> points)
    {
        var geometry = new StreamGeometry();
        using var sink = geometry.Open();
        sink.BeginFigure(ToPoint(points[0]), isFilled: false);

        for (var i = 1; i < points.Count - 1; i++)
        {
            var previous = ToPoint(points[i - 1]);
            var corner = ToPoint(points[i]);
            var next = ToPoint(points[i + 1]);

            var inRadius = Math.Min(CornerRadius, Distance(previous, corner) / 2);
            var outRadius = Math.Min(CornerRadius, Distance(corner, next) / 2);
            if (inRadius <= 0.5 || outRadius <= 0.5)
            {
                sink.LineTo(corner);
                continue;
            }

            sink.LineTo(Towards(corner, previous, inRadius));
            sink.QuadraticBezierTo(corner, Towards(corner, next, outRadius));
        }

        sink.LineTo(ToPoint(points[^1]));
        sink.EndFigure(false);
        return geometry;
    }

    private static void DrawArrow(DrawingContext context, IBrush brush, Point from, Point tip)
    {
        var dx = tip.X - from.X;
        var dy = tip.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 0.01)
        {
            return;
        }
        var ux = dx / length;
        var uy = dy / length;
        var baseX = tip.X - (ux * ArrowLength);
        var baseY = tip.Y - (uy * ArrowLength);

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(tip, isFilled: true);
            sink.LineTo(new Point(baseX - (uy * ArrowHalfWidth), baseY + (ux * ArrowHalfWidth)));
            sink.LineTo(new Point(baseX + (uy * ArrowHalfWidth), baseY - (ux * ArrowHalfWidth)));
            sink.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private void Rebind(IEnumerable? source)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }
        if (source is INotifyCollectionChanged notifier)
        {
            _observed = notifier;
            _observed.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private IBrush Brush(string key, uint fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            // Only reachable in the designer, where the token dictionary is not merged yet.
            : new SolidColorBrush(Color.FromUInt32(fallback));

    private static Point ToPoint(CanvasPoint point) => new(point.X, point.Y);

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    // A point `distance` along the segment from `origin` towards `target`.
    private static Point Towards(Point origin, Point target, double distance)
    {
        var dx = target.X - origin.X;
        var dy = target.Y - origin.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return length < 0.01
            ? origin
            : new Point(origin.X + (dx / length * distance), origin.Y + (dy / length * distance));
    }
}
