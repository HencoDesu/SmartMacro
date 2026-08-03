using SmartMacro.Macros.Model;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Вычисляет <see cref="TargetSelector"/> по снимкам окон: семантика И — все RequireTag на
/// месте, ни одного ExcludeTag. Функции чистые; исполнитель подаёт сюда свежий
/// <c>WindowRegistry.Snapshot()</c> в момент выполнения ноды, так что селектор всегда видит
/// текущее состояние тегов.
///
/// <b>Само правило живёт в Contracts</b> (<see cref="TargetSelector.Matches"/>) с волны D4:
/// панель вычисляет те же селекторы по своему снимку окон, чтобы нарисовать бейдж целей, а две
/// реализации ответа на вопрос «по каким окнам это попадёт» — это на одну реализацию больше,
/// чем нужно. Здесь остаётся типизированная обёртка, которую зовёт исполнитель: этот класс —
/// то место, где известен <see cref="ManagedWindowInfo"/>, а Contracts знать о нём не должен.
/// </summary>
public static class SelectorEvaluator
{
    /// <summary>Подходит ли одно окно под селектор.</summary>
    public static bool Matches(ManagedWindowInfo window, TargetSelector selector)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(selector);
        return selector.Matches(window.Tags);
    }

    /// <summary>Все окна из <paramref name="windows"/>, подходящие под селектор, в порядке поступления.</summary>
    public static IReadOnlyList<ManagedWindowInfo> Select(IEnumerable<ManagedWindowInfo> windows,
        TargetSelector selector)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(selector);
        return windows.Where(window => Matches(window, selector)).ToArray();
    }
}
