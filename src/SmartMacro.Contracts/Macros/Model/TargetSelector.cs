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
}
