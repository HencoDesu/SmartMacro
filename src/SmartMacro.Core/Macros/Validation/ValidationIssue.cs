namespace SmartMacro.Macros.Validation;

/// <summary>Severity of a <see cref="ValidationIssue"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>Suspicious but runnable (unreachable node, hot loop). Surface in the editor, allow saving.</summary>
    Warning,

    /// <summary>The graph is broken and a run would abort (or never start correctly).</summary>
    Error,
}

/// <summary>One finding of <see cref="MacroGraphValidator.Validate"/>.</summary>
/// <param name="Severity">Whether this blocks the graph or is merely suspicious.</param>
/// <param name="NodeId">The offending node, or <c>null</c> for graph-level issues (bad StartNodeId).</param>
/// <param name="Message">Human-readable description.</param>
public sealed record ValidationIssue(ValidationSeverity Severity, string? NodeId, string Message);
