using SmartMacro.Macros.Validation;

namespace SmartMacro.Contracts.Dto;

/// <summary>
/// Проводная форма <see cref="ValidationIssue"/>. <see cref="Severity"/> — обычная строка, а
/// не перечисление, чтобы демон, научившийся новой степени серьёзности, не ломал на
/// десериализации более старый интерфейс: незнакомые значения интерфейс просто показывает как
/// есть.
/// </summary>
/// <param name="Severity">Имя степени серьёзности, например <c>"Warning"</c> / <c>"Error"</c>.</param>
/// <param name="NodeId">Провинившаяся нода или <c>null</c> для проблем уровня всего графа.</param>
/// <param name="Message">Человекочитаемое описание.</param>
public sealed record ValidationIssueDto(string Severity, string? NodeId, string Message);

/// <summary>
/// Отображение между <see cref="ValidationIssue"/> и <see cref="ValidationIssueDto"/>. Живёт
/// в Contracts (в отличие от мапперов окон и прогонов, которым положено жить в Core), потому
/// что обе стороны этого преобразования — типы из Contracts.
/// </summary>
public static class ValidationIssueDtoMappers
{
    /// <summary>Проецирует проблему валидации в её проводную форму.</summary>
    public static ValidationIssueDto ToDto(this ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new ValidationIssueDto(issue.Severity.ToString(), issue.NodeId, issue.Message);
    }

    /// <summary>Проецирует список проблем валидации в их проводную форму.</summary>
    public static IReadOnlyList<ValidationIssueDto> ToDto(this IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return [.. issues.Select(issue => issue.ToDto())];
    }

    /// <summary>
    /// Разбирает проводную проблему обратно в доменный тип. Незнакомая
    /// <see cref="ValidationIssueDto.Severity"/> читается как
    /// <see cref="ValidationSeverity.Error"/>: проблему, которую мы не смогли
    /// классифицировать, меньше всего хочется молча понизить до предупреждения.
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
