using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// Пунктирная линия-подсказка между двумя коробками: «эта нода пишет переменную, эта её читает».
///
/// Намеренно НЕ <see cref="CanvasEdgeViewModel"/>. Ребро — это поток управления: оно выходит из
/// именованного порта исхода, прокладывается по жёлобам, чтобы никогда не пересечь коробку, и
/// несёт стрелку со смыслом «обход пойдёт туда». Связь по переменной не означает ничего из
/// перечисленного, и придать ей ту же форму значило бы заставить canvas утверждать, что есть
/// ветвление, которого нет. Рисуется прямой, пунктиром и цветом переменной — ровно как в записи
/// легенды на макете («— — переменная»).
/// </summary>
/// <param name="From">Сторона пишущего, в координатах canvas.</param>
/// <param name="To">Сторона читающего, в координатах canvas.</param>
public sealed record CanvasLinkViewModel(CanvasPoint From, CanvasPoint To)
{
    /// <summary>
    /// Соединяет две коробки от края до края: из правого бока левой в левый бок правой, чтобы
    /// линия не начиналась под своей же коробкой.
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
