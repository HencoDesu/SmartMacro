using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// A point in canvas space. Deliberately NOT <c>Avalonia.Point</c>: the whole canvas
/// geometry is computed in view-models that are exercised headlessly, and the moment one
/// of them takes an Avalonia type the tests need a rendering platform.
/// </summary>
public readonly record struct CanvasPoint(double X, double Y);

/// <summary>
/// The fixed metrics of the canvas, taken from the mockup (<c>docs/design/opt-1d.html</c>).
///
/// They are constants rather than options because the layout, the routing and the node
/// template all have to agree on them: a box drawn 6px taller than the router believes
/// puts every outgoing edge 6px off its port, and nothing in the code would say so.
/// </summary>
public static class CanvasMetrics
{
    /// <summary>Width of a collapsed node box.</summary>
    public const double NodeWidth = 210;

    /// <summary>Width of a node expanded into an editor of itself (1e).</summary>
    public const double ExpandedNodeWidth = 300;

    /// <summary>Height of an action box: header, id, summary, one outcome row.</summary>
    public const double ActionNodeHeight = 70;

    /// <summary>Height of a conditional box — two outcome rows instead of one.</summary>
    public const double ConditionalNodeHeight = 92;

    /// <summary>Left-to-right distance between two node columns (75px of gutter).</summary>
    public const double ColumnPitch = 285;

    /// <summary>Top-to-bottom distance between two rows (40px of gutter under a conditional).</summary>
    public const double RowPitch = 132;

    /// <summary>Columns before the layout wraps to the next row.</summary>
    public const int ColumnsPerRow = 3;

    /// <summary>One outcome row of a node box.</summary>
    public const double OutcomeRowHeight = 15;

    /// <summary>Padding under the last outcome row.</summary>
    public const double OutcomeBlockPadding = 4;

    /// <summary>Where an edge arriving from the LEFT enters a box, measured from its top.</summary>
    public const double SideEntryInset = 30;

    /// <summary>How far past a box's right edge a routed path steps before turning.</summary>
    public const double ExitStub = 26;

    /// <summary>How far above the target row a routed path runs before dropping in.</summary>
    public const double GutterOffset = 12;

    /// <summary>Horizontal spread between several edges entering the same box from above.</summary>
    public const double FanPitch = 26;
}

/// <summary>
/// One drawn edge: source node + outcome → target node, as a path in canvas space.
///
/// An outcome with no target produces NO edge at all. That is the rule the mockup calls
/// out explicitly — "в конец" must not materialise a terminal node — and it lives here
/// rather than in the renderer so nothing downstream can invent one.
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

    /// <summary>Node the edge leaves.</summary>
    public NodeRowViewModel Source { get; }

    /// <summary>Which of <see cref="NodeRowViewModel.Edges"/> this is.</summary>
    public int OutcomeIndex { get; }

    /// <summary>Node the edge arrives at.</summary>
    public NodeRowViewModel Target { get; }

    /// <summary>
    /// Path in canvas space, from the source port to the arrow tip. Two points for a
    /// direct hop (drawn as one cubic), five for a routed one (drawn as a rounded
    /// polyline through the gutter).
    /// </summary>
    public IReadOnlyList<CanvasPoint> Waypoints { get; }

    /// <summary><c>true</c> = a straight right-to-left hop between neighbours on one row.</summary>
    public bool IsDirect { get; }

    /// <summary>
    /// Lit while the run is standing on this edge's source. D3b sets it through
    /// <c>MacroEditorViewModel.ExecutingNodeId</c>; until then it is always <c>false</c>.
    /// </summary>
    public bool IsActive => Source.IsExecuting;
}

/// <summary>
/// Turns "node A's outcome N points at node B" into a path.
///
/// Two shapes, matching the mockup: a short S-curve when the target sits to the right on
/// the same row, and otherwise a route out to the right, along the gutter ABOVE the
/// target's row and down into the top of the box. Edges never cross a node box — that is
/// the entire reason for the second shape, and the reason rows wrap at a fixed pitch.
/// </summary>
public static class CanvasEdgeRouter
{
    /// <summary>Canvas-space point where a given outcome leaves its box (the port dot).</summary>
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
    /// Routes one edge.
    /// </summary>
    /// <param name="fanIndex">Position of this edge among all edges entering <paramref name="target"/> from above.</param>
    /// <param name="fanCount">How many edges enter <paramref name="target"/> from above.</param>
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

    /// <summary><c>true</c> when the target is the next box along the same row.</summary>
    public static bool IsSideEntry(NodeRowViewModel source, NodeRowViewModel target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        // Same row (within a pixel of slack, since a dragged node lands on fractional
        // coordinates) and strictly to the right, with room for the curve.
        return Math.Abs(source.Y - target.Y) < 1
               && target.X >= source.X + CanvasMetrics.NodeWidth;
    }

    /// <summary>
    /// Builds every edge of a graph, in node order then outcome order.
    ///
    /// Unwired outcomes and outcomes naming a node that is not in the list are skipped:
    /// the first is a legitimate end state, the second is a broken file the validator will
    /// complain about — neither is something to draw a line to.
    /// </summary>
    public static List<CanvasEdgeViewModel> BuildAll(IReadOnlyList<NodeRowViewModel> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var byId = new Dictionary<string, NodeRowViewModel>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byId[node.NodeId] = node;
        }

        // Pass one: collect the pairs, so the fan-in of a node is known before its
        // arriving edges are placed.
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
