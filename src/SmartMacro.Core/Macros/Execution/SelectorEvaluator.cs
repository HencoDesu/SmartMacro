using SmartMacro.Macros.Model;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Evaluates a <see cref="TargetSelector"/> against window snapshots: AND semantics —
/// every RequireTag present, no ExcludeTag present. Pure functions; the executor feeds
/// it a fresh <c>WindowRegistry.Snapshot()</c> at the moment a node executes, so a
/// selector always sees the current tag state.
///
/// <b>The rule itself lives in Contracts</b> (<see cref="TargetSelector.Matches"/>) since
/// wave D4: the panel evaluates the same selectors against its own window snapshot to draw
/// the targets badge, and two implementations of "which windows does this hit" would be one
/// implementation too many. What is left here is the typed wrapper the executor calls —
/// this class is where <see cref="ManagedWindowInfo"/> is known, and Contracts must not
/// know it.
/// </summary>
public static class SelectorEvaluator
{
    /// <summary>Whether one window matches the selector.</summary>
    public static bool Matches(ManagedWindowInfo window, TargetSelector selector)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(selector);
        return selector.Matches(window.Tags);
    }

    /// <summary>All windows of <paramref name="windows"/> matching the selector, in input order.</summary>
    public static IReadOnlyList<ManagedWindowInfo> Select(IEnumerable<ManagedWindowInfo> windows, TargetSelector selector)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(selector);
        return windows.Where(window => Matches(window, selector)).ToArray();
    }
}
