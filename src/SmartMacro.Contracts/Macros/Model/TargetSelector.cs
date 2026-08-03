namespace SmartMacro.Macros.Model;

/// <summary>
/// Selects target windows by tags with AND semantics: a window matches when it carries
/// every tag in <see cref="RequireTags"/> and none of the tags in <see cref="ExcludeTags"/>.
/// Both lists empty = match every registered window. Tags are case-sensitive free-form
/// strings, compared ordinally against the <c>WindowRegistry</c> snapshot.
/// </summary>
public sealed record TargetSelector
{
    /// <summary>Tags a window must ALL carry to match. Empty = no requirement.</summary>
    public List<string> RequireTags { get; init; } = [];

    /// <summary>Tags that disqualify a window when ANY is present. Empty = no exclusion.</summary>
    public List<string> ExcludeTags { get; init; } = [];

    /// <summary>
    /// THE matching rule, and the only implementation of it (wave D4).
    ///
    /// It lives here — on the selector, in Contracts — rather than in the executor because
    /// two processes now need the same answer. The daemon asks it through
    /// <c>SelectorEvaluator</c> when a node fans out; the panel asks it directly to draw the
    /// «8 окон · кроме Склад» badge. A badge that disagreed with what the engine actually
    /// targets would be worse than no badge, so there is deliberately no second copy of
    /// these two loops anywhere.
    ///
    /// Taking the TAGS rather than a window is what makes it usable from both sides: Core
    /// has <c>ManagedWindowInfo</c> (an <see cref="IReadOnlySet{T}"/> of tags), the panel has
    /// <c>WindowDto</c> (an ordered list), and neither has to know about the other.
    /// </summary>
    /// <param name="tags">
    /// The window's tags. Ordinal comparison either way: LINQ's <c>Contains</c> dispatches to
    /// a <see cref="HashSet{T}"/>'s own lookup when the caller passes one, and both a default
    /// <c>HashSet&lt;string&gt;</c> and a <c>List&lt;string&gt;</c> compare ordinally — so the
    /// two callers cannot disagree on case.
    /// </param>
    /// <returns><c>true</c> when a window carrying <paramref name="tags"/> is a target.</returns>
    public bool Matches(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in RequireTags)
        {
            if (!tags.Contains(tag))
            {
                return false;
            }
        }
        foreach (var tag in ExcludeTags)
        {
            if (tags.Contains(tag))
            {
                return false;
            }
        }
        return true;
    }
}
