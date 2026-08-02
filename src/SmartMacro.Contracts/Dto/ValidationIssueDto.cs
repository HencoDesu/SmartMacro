using SmartMacro.Macros.Validation;

namespace SmartMacro.Contracts.Dto;

/// <summary>
/// Wire form of a <see cref="ValidationIssue"/>. <see cref="Severity"/> is a plain string
/// rather than the enum so that a daemon which learns a new severity doesn't break an
/// older UI on deserialization — the UI renders unknown severities verbatim.
/// </summary>
/// <param name="Severity">Severity name, e.g. <c>"Warning"</c> / <c>"Error"</c>.</param>
/// <param name="NodeId">The offending node, or <c>null</c> for graph-level issues.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record ValidationIssueDto(string Severity, string? NodeId, string Message);

/// <summary>
/// Mapping between <see cref="ValidationIssue"/> and <see cref="ValidationIssueDto"/>.
/// Lives in Contracts (unlike the window/run mappers, which have to live in Core) because
/// both sides of this conversion are Contracts types.
/// </summary>
public static class ValidationIssueDtoMappers
{
    /// <summary>Projects a validation issue onto its wire form.</summary>
    public static ValidationIssueDto ToDto(this ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new ValidationIssueDto(issue.Severity.ToString(), issue.NodeId, issue.Message);
    }

    /// <summary>Projects a list of validation issues onto their wire form.</summary>
    public static IReadOnlyList<ValidationIssueDto> ToDto(this IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return [.. issues.Select(issue => issue.ToDto())];
    }

    /// <summary>
    /// Parses a wire issue back into the domain type. An unrecognized
    /// <see cref="ValidationIssueDto.Severity"/> is read as
    /// <see cref="ValidationSeverity.Error"/> — an issue we can't classify is the one we
    /// least want to silently downgrade to a warning.
    /// </summary>
    public static ValidationIssue ToIssue(this ValidationIssueDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var severity = Enum.TryParse<ValidationSeverity>(dto.Severity, ignoreCase: true, out var parsed)
            ? parsed
            : ValidationSeverity.Error;
        return new ValidationIssue(severity, dto.NodeId, dto.Message);
    }
}
