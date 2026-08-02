using SmartMacro.Macros.Model;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Evaluates a <see cref="TargetSelector"/> against window snapshots: AND semantics —
/// every RequireTag present, no ExcludeTag present. Pure functions; the executor feeds
/// it a fresh <c>WindowRegistry.Snapshot()</c> at the moment a node executes, so a
/// selector always sees the current tag state.
/// </summary>
public static class SelectorEvaluator
{
    /// <summary>Whether one window matches the selector.</summary>
    public static bool Matches(ManagedWindowInfo window, TargetSelector selector)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(selector);

        foreach (var tag in selector.RequireTags)
        {
            if (!window.Tags.Contains(tag))
            {
                return false;
            }
        }
        foreach (var tag in selector.ExcludeTags)
        {
            if (window.Tags.Contains(tag))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>All windows of <paramref name="windows"/> matching the selector, in input order.</summary>
    public static IReadOnlyList<ManagedWindowInfo> Select(IEnumerable<ManagedWindowInfo> windows, TargetSelector selector)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(selector);
        return windows.Where(window => Matches(window, selector)).ToArray();
    }
}
