using System.Text.Json.Serialization;

namespace SmartMacro.Macros.Model;

/// <summary>
/// Canvas-editor placement of a node. Pure presentation data — the executor never reads
/// it, but it must round-trip through JSON so hand-arranged graphs keep their layout.
/// </summary>
public sealed record NodeEditorInfo(double X, double Y);

/// <summary>
/// One node of a <see cref="MacroGraph"/>. JSON-polymorphic via <c>$type</c>.
///
/// Two families:
///   * Action nodes — one outgoing edge (<c>Next</c>; <c>null</c> = end of run). Most carry
///     an optional <see cref="TargetSelector"/> (<c>Target</c>): non-null = fan the action
///     out to every matching window in parallel; null = act on the run's context window.
///   * Conditional nodes — operate on the context window only and carry one outgoing edge
///     per outcome (Found/NotFound, Found/Timeout, Matched/NotMatched); a <c>null</c>
///     outcome edge ends the run.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(KeyPressNode), typeDiscriminator: "keyPress")]
[JsonDerivedType(typeof(ClickNode), typeDiscriminator: "click")]
[JsonDerivedType(typeof(DelayNode), typeDiscriminator: "delay")]
[JsonDerivedType(typeof(AddTagNode), typeDiscriminator: "addTag")]
[JsonDerivedType(typeof(RemoveTagNode), typeDiscriminator: "removeTag")]
[JsonDerivedType(typeof(SetIconNode), typeDiscriminator: "setIcon")]
[JsonDerivedType(typeof(RunMacroNode), typeDiscriminator: "runMacro")]
[JsonDerivedType(typeof(FindElementNode), typeDiscriminator: "findElement")]
[JsonDerivedType(typeof(WaitForElementNode), typeDiscriminator: "waitForElement")]
[JsonDerivedType(typeof(RecognizeTagNode), typeDiscriminator: "recognizeTag")]
public abstract record MacroNode
{
    /// <summary>Unique (within the graph) node id. Edges reference nodes by this id.</summary>
    public required string Id { get; init; }

    /// <summary>Canvas-editor placement. <c>null</c> until the graph is opened in the visual editor.</summary>
    public NodeEditorInfo? Editor { get; init; }
}
