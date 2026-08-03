using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// Точка в пространстве canvas. Намеренно НЕ <c>Avalonia.Point</c>: вся геометрия canvas
/// считается во view-моделях, которые гоняются headless, и в ту минуту, когда хоть одна из них
/// возьмёт тип Avalonia, тестам понадобится платформа отрисовки.
/// </summary>
public readonly record struct CanvasPoint(double X, double Y);

/// <summary>
/// Постоянные метрики canvas, взятые из макета (<c>docs/design/opt-1d.html</c>).
///
/// Это константы, а не настройки, потому что раскладка, маршрутизация и шаблон ноды обязаны
/// сходиться на одних и тех же числах: коробка, нарисованная на 6px выше, чем считает
/// маршрутизатор, уводит каждое исходящее ребро на 6px мимо порта — и ничто в коде об этом не
/// скажет.
/// </summary>
public static class CanvasMetrics
{
    /// <summary>Ширина свёрнутой коробки ноды.</summary>
    public const double NodeWidth = 210;

    /// <summary>Ширина ноды, развёрнутой в редактор самой себя (1e).</summary>
    public const double ExpandedNodeWidth = 300;

    /// <summary>Высота коробки действия: заголовок, id, сводка, одна строка исхода.</summary>
    public const double ActionNodeHeight = 70;

    /// <summary>Высота коробки условия — две строки исходов вместо одной.</summary>
    public const double ConditionalNodeHeight = 92;

    /// <summary>Расстояние слева направо между двумя колонками нод (75px жёлоба).</summary>
    public const double ColumnPitch = 285;

    /// <summary>Расстояние сверху вниз между двумя рядами (40px жёлоба под условием).</summary>
    public const double RowPitch = 132;

    /// <summary>Сколько колонок помещается в ряд, прежде чем раскладка перенесётся на следующий.</summary>
    public const int ColumnsPerRow = 3;

    /// <summary>Одна строка исхода в коробке ноды.</summary>
    public const double OutcomeRowHeight = 15;

    /// <summary>Отступ под последней строкой исхода.</summary>
    public const double OutcomeBlockPadding = 4;

    /// <summary>Куда в коробку входит ребро, пришедшее СЛЕВА, считая от её верха.</summary>
    public const double SideEntryInset = 30;

    /// <summary>Насколько дальше правого края коробки маршрут отходит, прежде чем повернуть.</summary>
    public const double ExitStub = 26;

    /// <summary>Насколько выше целевого ряда идёт маршрут, прежде чем нырнуть вниз.</summary>
    public const double GutterOffset = 12;

    /// <summary>Горизонтальный разброс между несколькими рёбрами, входящими в одну коробку сверху.</summary>
    public const double FanPitch = 26;
}

/// <summary>
/// Одно нарисованное ребро: нода-источник плюс исход → нода-цель, в виде пути в пространстве
/// canvas.
///
/// Исход без цели НЕ порождает ребра вообще. Это правило макет проговаривает отдельно — «в
/// конец» не должно материализовать терминальную ноду, — и живёт оно здесь, а не в
/// отрисовщике, чтобы никто ниже по течению не смог такую ноду выдумать.
/// </summary>
public sealed class CanvasEdgeViewModel
{
    internal CanvasEdgeViewModel(
        NodeRowViewModel source,
        int outcomeIndex,
        NodeRowViewModel target,
        IReadOnlyList<CanvasPoint> waypoints,
        bool isDirect)
    {
        Source = source;
        OutcomeIndex = outcomeIndex;
        Target = target;
        Waypoints = waypoints;
        IsDirect = isDirect;
    }

    /// <summary>Нода, из которой ребро выходит.</summary>
    public NodeRowViewModel Source { get; }

    /// <summary>Который это из <see cref="NodeRowViewModel.Edges"/>.</summary>
    public int OutcomeIndex { get; }

    /// <summary>Нода, в которую ребро приходит.</summary>
    public NodeRowViewModel Target { get; }

    /// <summary>
    /// Путь в пространстве canvas, от порта источника до кончика стрелки. Две точки на прямой
    /// прыжок (рисуется одной кубической кривой) и пять на маршрутизированный (рисуется
    /// скруглённой ломаной через жёлоб).
    /// </summary>
    public IReadOnlyList<CanvasPoint> Waypoints { get; }

    /// <summary><c>true</c> = прямой прыжок из правого бока в левый между соседями по одному ряду.</summary>
    public bool IsDirect { get; }

    /// <summary>
    /// Горит, пока прогон стоит на источнике этого ребра. Значение приходит из
    /// <c>MacroEditorViewModel.ExecutingNodeId</c> — то есть из русла событий прогона, которое
    /// построила D3b.
    /// </summary>
    public bool IsActive => Source.IsExecuting;
}

/// <summary>
/// Превращает «исход N ноды A указывает на ноду B» в путь.
///
/// Форм две, обе из макета: короткая S-образная кривая, когда цель стоит справа в том же ряду,
/// и во всех остальных случаях — маршрут вправо, вдоль жёлоба НАД рядом цели и вниз, в верх
/// коробки. Рёбра никогда не пересекают коробку ноды — ровно ради этого вторая форма и
/// существует, и ровно поэтому ряды переносятся с постоянным шагом.
/// </summary>
public static class CanvasEdgeRouter
{
    /// <summary>Точка в пространстве canvas, где данный исход покидает свою коробку (точка порта).</summary>
    public static CanvasPoint OutcomePort(NodeRowViewModel node, int outcomeIndex)
    {
        ArgumentNullException.ThrowIfNull(node);
        var rows = Math.Max(node.Edges.Count, 1);
        var index = Math.Clamp(outcomeIndex, 0, rows - 1);
        var bottom = node.Y + node.LayoutHeight;
        var y = bottom
                - CanvasMetrics.OutcomeBlockPadding
                - ((rows - index - 0.5) * CanvasMetrics.OutcomeRowHeight);
        return new CanvasPoint(node.X + CanvasMetrics.NodeWidth, y);
    }

    /// <summary>
    /// Прокладывает одно ребро.
    /// </summary>
    /// <param name="source">Нода, из которой ребро выходит.</param>
    /// <param name="outcomeIndex">Какой из исходов <paramref name="source"/> порождает это ребро.</param>
    /// <param name="target">Нода, в которую ребро приходит.</param>
    /// <param name="fanIndex">Место этого ребра среди всех рёбер, входящих в <paramref name="target"/> сверху.</param>
    /// <param name="fanCount">Сколько рёбер входит в <paramref name="target"/> сверху.</param>
    public static CanvasEdgeViewModel Route(
        NodeRowViewModel source,
        int outcomeIndex,
        NodeRowViewModel target,
        int fanIndex = 0,
        int fanCount = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var start = OutcomePort(source, outcomeIndex);

        if (IsSideEntry(source, target))
        {
            var entry = new CanvasPoint(target.X, target.Y + CanvasMetrics.SideEntryInset);
            return new CanvasEdgeViewModel(source, outcomeIndex, target, [start, entry], isDirect: true);
        }

        var stubX = source.X + CanvasMetrics.NodeWidth + CanvasMetrics.ExitStub;
        var gutterY = target.Y - CanvasMetrics.GutterOffset;
        var spread = fanCount > 1
            ? (fanIndex - ((fanCount - 1) / 2.0)) * CanvasMetrics.FanPitch
            : 0;
        var dropX = target.X + (CanvasMetrics.NodeWidth / 2) + spread;

        CanvasPoint[] waypoints =
        [
            start,
            new CanvasPoint(stubX, start.Y),
            new CanvasPoint(stubX, gutterY),
            new CanvasPoint(dropX, gutterY),
            new CanvasPoint(dropX, target.Y),
        ];
        return new CanvasEdgeViewModel(source, outcomeIndex, target, waypoints, isDirect: false);
    }

    /// <summary><c>true</c>, когда цель — следующая коробка по тому же ряду.</summary>
    public static bool IsSideEntry(NodeRowViewModel source, NodeRowViewModel target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        // Тот же ряд (с точностью до пикселя запаса — перетащенная нода приземляется на дробные
        // координаты) и строго правее, с местом на кривую.
        return Math.Abs(source.Y - target.Y) < 1
               && target.X >= source.X + CanvasMetrics.NodeWidth;
    }

    /// <summary>
    /// Строит все рёбра графа: в порядке нод, а внутри ноды — в порядке исходов.
    ///
    /// Неподключённые исходы и исходы, называющие ноду, которой в списке нет, пропускаются:
    /// первое — законное завершение, второе — битый файл, на который валидатор и так
    /// пожалуется; ни к тому, ни к другому линию рисовать незачем.
    /// </summary>
    public static List<CanvasEdgeViewModel> BuildAll(IReadOnlyList<NodeRowViewModel> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var byId = new Dictionary<string, NodeRowViewModel>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byId[node.NodeId] = node;
        }

        // Проход первый: собираем пары, чтобы веер, сходящийся в ноду, был известен до того, как
        // начнут расставлять входящие в неё рёбра.
        var pairs = new List<(NodeRowViewModel Source, int Outcome, NodeRowViewModel Target)>();
        foreach (var node in nodes)
        {
            for (var i = 0; i < node.Edges.Count; i++)
            {
                var targetId = node.Edges[i].TargetId;
                if (targetId.Length == 0 || !byId.TryGetValue(targetId, out var target))
                {
                    continue;
                }

                pairs.Add((node, i, target));
            }
        }

        var fanTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (source, _, target) in pairs)
        {
            if (IsSideEntry(source, target))
            {
                continue;
            }

            fanTotals[target.NodeId] = fanTotals.TryGetValue(target.NodeId, out var n) ? n + 1 : 1;
        }

        var fanSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new List<CanvasEdgeViewModel>(pairs.Count);
        foreach (var (source, outcome, target) in pairs)
        {
            if (IsSideEntry(source, target))
            {
                edges.Add(Route(source, outcome, target));
                continue;
            }

            var index = fanSeen.TryGetValue(target.NodeId, out var seen) ? seen : 0;
            fanSeen[target.NodeId] = index + 1;
            edges.Add(Route(source, outcome, target, index, fanTotals[target.NodeId]));
        }

        return edges;
    }
}
