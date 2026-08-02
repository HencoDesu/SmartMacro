using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// «Авто-раскладка» — rows with wrapping, which is the layout the mockup specifies and the
/// reason its edges never cross a box.
///
/// The order is a depth-first walk from the start node, following each node's outcomes in
/// their declared order (Found before NotFound, and so on). Depth-first rather than
/// breadth-first on purpose: a macro is overwhelmingly a chain with the odd branch, and DFS
/// keeps a chain in reading order — pw-boot lays out exactly as the mockup draws it. Nodes
/// the start cannot reach are appended afterwards in list order; they are a warning the
/// validator already reports, and hiding them off-layout would make that warning unfindable.
/// </summary>
public static class MacroGraphLayout
{
    /// <summary>
    /// Re-places every node on the grid. This is what the toolbar button calls, and it
    /// always overwrites — an explicit request to tidy up is not the moment to preserve
    /// hand-placed coordinates.
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
    /// Gives every node a position without disturbing the ones that already have one.
    ///
    /// Called on load, because graphs authored before this wave carry no coordinates at
    /// all and would otherwise open as a single pile at the origin. Two cases:
    ///   * nothing is placed — lay the whole graph out, which is what a first open of an
    ///     old macro should look like;
    ///   * some are placed (a hand-arranged graph plus a node someone added by editing the
    ///     JSON) — leave those alone and drop the strays into fresh rows underneath, where
    ///     they are visible rather than stacked on top of existing boxes.
    /// </summary>
    /// <returns><c>true</c> when anything moved.</returns>
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
        // Start on the row after the lowest existing box, snapped to the pitch so the new
        // boxes sit on the same grid as everything else.
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
    /// Finds a free spot for a brand-new node: the first grid slot no existing box covers,
    /// scanning rows top to bottom. Adding a node must never drop it under another one.
    /// </summary>
    public static (double X, double Y) NextFreeSlot(IReadOnlyList<NodeRowViewModel> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var taken = nodes
            .Where(node => node.HasPosition)
            .Select(node => (Column: (int)Math.Round(node.X / CanvasMetrics.ColumnPitch),
                             Row: (int)Math.Round(node.Y / CanvasMetrics.RowPitch)))
            .ToHashSet();

        for (var row = 0; ; row++)
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

    /// <summary>Depth-first from the start node, then whatever it could not reach.</summary>
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
