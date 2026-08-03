using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartMacro.App.ViewModels.Canvas;

namespace SmartMacro.App.Controls;

/// <summary>
/// Рисует все рёбра графа за один проход.
///
/// Один control, а не <c>Path</c> на ребро, — и это намеренно: перетаскивание ноды
/// перекладывает маршрут каждой линии, которой она касается, а пересборка визуального дерева с
/// частотой движения указателя — ровно то, от чего canvas начинает казаться тяжёлым. Здесь
/// перетаскивание — это один <c>InvalidateVisual</c>.
///
/// Геометрия приходит из <see cref="CanvasEdgeViewModel"/> — маршрутизатор сделан view-model'ю,
/// чтобы его можно было тестировать без платформы отрисовки, а этот класс лишь превращает
/// путевые точки в штрихи. Hit-test по нему не проходит никогда: клики принадлежат коробкам
/// под ним.
/// </summary>
public sealed class EdgeLayer : Control
{
    /// <summary>Радиус скругления там, где проложенный маршрут поворачивает.</summary>
    private const double CornerRadius = 10;

    /// <summary>Половина длины наконечника стрелки на острие ребра.</summary>
    private const double ArrowLength = 7;

    /// <summary>Половина ширины наконечника стрелки.</summary>
    private const double ArrowHalfWidth = 3.5;

    /// <summary>Какую долю горизонтального зазора занимают рычаги безье у прямого перехода.</summary>
    private const double DirectCurveTension = 0.45;

    /// <summary>Штриховка связи по переменной, в толщинах штриха.</summary>
    private static readonly DashStyle VariableDash = new([4, 4], 0);

    public static readonly StyledProperty<IEnumerable?> EdgesProperty =
        AvaloniaProperty.Register<EdgeLayer, IEnumerable?>(nameof(Edges));

    /// <summary>Пунктирные подсказки «кто пишет → кто читает» (D5), рисуются при наведении на карточку переменной.</summary>
    public static readonly StyledProperty<IEnumerable?> LinksProperty =
        AvaloniaProperty.Register<EdgeLayer, IEnumerable?>(nameof(Links));

    /// <summary>Начало связи, которую тянут из порта, в пространстве canvas.</summary>
    public static readonly StyledProperty<Point?> PendingStartProperty =
        AvaloniaProperty.Register<EdgeLayer, Point?>(nameof(PendingStart));

    /// <summary>Текущее положение указателя, пока связь тянут, в пространстве canvas.</summary>
    public static readonly StyledProperty<Point?> PendingEndProperty =
        AvaloniaProperty.Register<EdgeLayer, Point?>(nameof(PendingEnd));

    private INotifyCollectionChanged? _observed;
    private INotifyCollectionChanged? _observedLinks;

    public EdgeLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    /// <summary>Проложенные рёбра, которые надо нарисовать.</summary>
    public IEnumerable? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    /// <summary><see cref="CanvasLinkViewModel"/> — пунктиром, по прямой, без стрелки.</summary>
    public IEnumerable? Links
    {
        get => GetValue(LinksProperty);
        set => SetValue(LinksProperty, value);
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
            _observed = Rebind(_observed, change.NewValue as IEnumerable);
            InvalidateVisual();
        }
        else if (change.Property == LinksProperty)
        {
            _observedLinks = Rebind(_observedLinks, change.NewValue as IEnumerable);
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

        // Связи по переменным — последними, чтобы пунктирная подсказка легла ПОВЕРХ потока
        // управления, который она комментирует, а не спряталась под ним. Прямая и без стрелки
        // намеренно — почему она не должна выглядеть ребром, написано в CanvasLinkViewModel.
        if (Links is { } links)
        {
            var pen = new Pen(Brush("NocturneVariableBrush", 0xFFDBB277), 1.2, VariableDash);
            foreach (var item in links)
            {
                if (item is CanvasLinkViewModel link)
                {
                    context.DrawLine(pen, ToPoint(link.From), ToPoint(link.To));
                }
            }
        }

        if (PendingStart is { } start && PendingEnd is { } end)
        {
            // Связь, которую тянут прямо сейчас: акцентный пунктир, чтобы читалась как
            // предложение, а не как уже существующий провод.
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
        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
            geometry);

        var tip = ToPoint(points[^1]);
        var previous = ToPoint(points[^2]);
        DrawArrow(context, brush, previous, tip);
    }

    // Короткая «эска» между двумя коробками одного ряда — та самая кубическая кривая из макета.
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

    // Вправо из коробки, по жёлобу над целевым рядом, вниз в его верхнюю кромку. Углы скруглены
    // квадратичной кривой, чтобы путь читался одним движением, а не четырьмя.
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

    private INotifyCollectionChanged? Rebind(INotifyCollectionChanged? current, IEnumerable? source)
    {
        if (current is not null)
        {
            current.CollectionChanged -= OnCollectionChanged;
        }

        if (source is not INotifyCollectionChanged notifier)
        {
            return null;
        }

        notifier.CollectionChanged += OnCollectionChanged;
        return notifier;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private IBrush Brush(string key, uint fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            // Достижимо только в дизайнере, где словарь токенов ещё не подмешан.
            : new SolidColorBrush(Color.FromUInt32(fallback));

    private static Point ToPoint(CanvasPoint point) => new(point.X, point.Y);

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    // Точка на отрезке из `origin` в сторону `target`, отстоящая от начала на `distance`.
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
