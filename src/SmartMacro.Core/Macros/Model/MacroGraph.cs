namespace SmartMacro.Macros.Model;

/// <summary>
/// A macro as a directed graph of nodes: actions (single outgoing edge) and conditionals
/// (one edge per outcome). Execution starts at <see cref="StartNodeId"/> and follows edges
/// until a <c>null</c> edge ends the run. Named <c>MacroGraph</c> (not <c>Macro</c>) to
/// avoid clashing with the legacy <c>SmartMacro.Macro.Macro</c> during the transition —
/// once the old pipeline dies the name can be revisited.
/// </summary>
public sealed class MacroGraph
{
    /// <summary>
    /// Macro name. Unique across the library; becomes the file name on disk, so it must be
    /// a valid NTFS name (enforced by the store, not the model).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Ways this macro starts on its own. Empty is legal — such a macro only runs via
    /// <see cref="RunMacroNode"/> from another macro or a manual UI Run.
    /// </summary>
    public List<MacroTrigger> Triggers { get; init; } = [];

    /// <summary>Id of the node execution starts from. Must reference an entry of <see cref="Nodes"/>.</summary>
    public required string StartNodeId { get; init; }

    /// <summary>All nodes of the graph. Ids must be unique (checked by the validator and the executor).</summary>
    public required List<MacroNode> Nodes { get; init; }
}
