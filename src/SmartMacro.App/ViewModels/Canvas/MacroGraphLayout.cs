using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// «Авто-раскладка» — ряды с переносом; именно её задаёт макет, и именно из-за неё его рёбра
/// никогда не пересекают коробку.
///
/// Порядок — обход в глубину от стартовой ноды, по исходам каждой ноды в том порядке, в каком
/// они объявлены (Found раньше NotFound и так далее). В глубину, а не в ширину, и это
/// намеренно: макрос в подавляющем большинстве случаев — цепочка с редким ветвлением, а обход
/// в глубину сохраняет цепочку в порядке чтения — pw-boot раскладывается ровно так, как его
/// рисует макет. Ноды, до которых от старта не добраться, дописываются следом в порядке списка:
/// это предупреждение, которое валидатор и так выдаёт, а спрятав их за пределами раскладки, мы
/// сделали бы это предупреждение ненаходимым.
/// </summary>
public static class MacroGraphLayout
{
    /// <summary>
    /// Заново расставляет каждую ноду по сетке. Это то, что вызывает кнопка на панели
    /// инструментов, и она всегда затирает: явная просьба прибраться — не тот момент, когда
    /// нужно беречь расставленные вручную координаты.
    /// </summary>
    public static void Apply(IReadOnlyList<NodeRowViewModel> nodes, string? startNodeId)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var ordered = Order(nodes, startNodeId);
        for (var i = 0; i < ordered.Count; i++)
        {
            var column = i % CanvasMetrics.ColumnsPerRow;
            var row = i / CanvasMetrics.ColumnsPerRow;
            ordered[i].SetPosition(column * CanvasMetrics.ColumnPitch, row * CanvasMetrics.RowPitch);
        }
    }

    /// <summary>
    /// Даёт позицию каждой ноде, не трогая те, у которых она уже есть.
    ///
    /// Вызывается при загрузке, потому что графы, написанные до этой волны, не несут координат
    /// вовсе и иначе открывались бы одной кучей в начале координат. Два случая:
    ///   * не расставлено ничего — раскладываем весь граф, и именно так должно выглядеть первое
    ///     открытие старого макроса;
    ///   * часть расставлена (разложенный руками граф плюс нода, которую кто-то добавил правкой
    ///     JSON) — эти не трогаем, а бесхозные сбрасываем в свежие ряды снизу, где они видны, а
    ///     не свалены поверх существующих коробок.
    /// </summary>
    /// <returns><c>true</c>, если хоть что-нибудь сдвинулось.</returns>
    public static bool EnsurePositions(IReadOnlyList<NodeRowViewModel> nodes, string? startNodeId)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return false;
        }

        var unplaced = nodes.Where(node => !node.HasPosition).ToList();
        if (unplaced.Count == 0)
        {
            return false;
        }

        if (unplaced.Count == nodes.Count)
        {
            Apply(nodes, startNodeId);
            return true;
        }

        var baseY = nodes
            .Where(node => node.HasPosition)
            .Max(node => node.Y + node.LayoutHeight);
        // Начинаем с ряда, следующего за самой нижней существующей коробкой, притянув его к шагу
        // сетки, чтобы новые коробки встали на ту же сетку, что и всё остальное.
        var firstRow = Math.Ceiling((baseY + CanvasMetrics.GutterOffset) / CanvasMetrics.RowPitch);
        for (var i = 0; i < unplaced.Count; i++)
        {
            var column = i % CanvasMetrics.ColumnsPerRow;
            var row = firstRow + (i / CanvasMetrics.ColumnsPerRow);
            unplaced[i].SetPosition(column * CanvasMetrics.ColumnPitch, row * CanvasMetrics.RowPitch);
        }

        return true;
    }

    /// <summary>
    /// Ищет свободное место для совсем новой ноды: первый слот сетки, который не накрыт ни одной
    /// существующей коробкой, просматривая ряды сверху вниз. Добавление ноды не имеет права
    /// уронить её под другую.
    /// </summary>
    public static (double X, double Y) NextFreeSlot(IReadOnlyList<NodeRowViewModel> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var taken = nodes
            .Where(node => node.HasPosition)
            .Select(node => (Column: (int)Math.Round(node.X / CanvasMetrics.ColumnPitch),
                Row: (int)Math.Round(node.Y / CanvasMetrics.RowPitch)))
            .ToHashSet();

        for (var row = 0;; row++)
        {
            for (var column = 0; column < CanvasMetrics.ColumnsPerRow; column++)
            {
                if (taken.Add((column, row)))
                {
                    return (column * CanvasMetrics.ColumnPitch, row * CanvasMetrics.RowPitch);
                }
            }
        }
    }

    /// <summary>В глубину от стартовой ноды, а следом всё, до чего она не дотянулась.</summary>
    internal static List<NodeRowViewModel> Order(IReadOnlyList<NodeRowViewModel> nodes, string? startNodeId)
    {
        var byId = new Dictionary<string, NodeRowViewModel>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byId.TryAdd(node.NodeId, node);
        }

        var ordered = new List<NodeRowViewModel>(nodes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(startNodeId) && byId.TryGetValue(startNodeId, out var start))
        {
            Walk(start);
        }

        foreach (var node in nodes)
        {
            if (seen.Add(node.NodeId))
            {
                ordered.Add(node);
            }
        }

        return ordered;

        void Walk(NodeRowViewModel node)
        {
            if (!seen.Add(node.NodeId))
            {
                return;
            }

            ordered.Add(node);
            foreach (var edge in node.Edges)
            {
                if (edge.TargetId.Length > 0 && byId.TryGetValue(edge.TargetId, out var next))
                {
                    Walk(next);
                }
            }
        }
    }
}
