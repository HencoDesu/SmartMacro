using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// A dashed hint line between two boxes: «эта нода пишет переменную, эта её читает».
///
/// Deliberately NOT a <see cref="CanvasEdgeViewModel"/>. An edge is control flow — it leaves
/// a named outcome port, it routes through the gutters so it never crosses a box, and it
/// carries an arrow that means "the walker goes there". A variable link means none of those
/// things, and giving it the same shape would make the canvas claim a branch that does not
/// exist. It is drawn straight, dashed and in the variable colour, exactly like the mockup's
/// legend entry («— — переменная»).
/// </summary>
/// <param name="From">Writer side, in canvas space.</param>
/// <param name="To">Reader side, in canvas space.</param>
public sealed record CanvasLinkViewModel(CanvasPoint From, CanvasPoint To)
{
    /// <summary>
    /// Joins two boxes edge to edge: out of the right side of the left-hand one and into the
    /// left side of the right-hand one, so the line does not start underneath its own box.
    /// </summary>
    public static CanvasLinkViewModel Between(NodeRowViewModel from, NodeRowViewModel to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var fromMidY = from.Y + (from.LayoutHeight / 2);
        var toMidY = to.Y + (to.LayoutHeight / 2);
        var fromIsLeft = from.X <= to.X;

        return new CanvasLinkViewModel(
            new CanvasPoint(fromIsLeft ? from.X + CanvasMetrics.NodeWidth : from.X, fromMidY),
            new CanvasPoint(fromIsLeft ? to.X : to.X + CanvasMetrics.NodeWidth, toMidY));
    }
}
