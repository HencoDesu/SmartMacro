namespace SmartMacro.Macros.Validation;

/// <summary>Степень серьёзности <see cref="ValidationIssue"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>Подозрительно, но работоспособно (недостижимая нода, цикл без пауз). Показать в редакторе, сохранять разрешить.</summary>
    Warning,

    /// <summary>Граф сломан: прогон прервётся (или вообще не стартует как надо).</summary>
    Error,
}

/// <summary>Одна находка <see cref="MacroGraphValidator.Validate"/>.</summary>
/// <param name="Severity">Блокирует ли это граф или просто выглядит подозрительно.</param>
/// <param name="NodeId">Провинившаяся нода или <c>null</c> для проблем уровня графа (плохой StartNodeId).</param>
/// <param name="Message">Человекочитаемое описание.</param>
public sealed record ValidationIssue(ValidationSeverity Severity, string? NodeId, string Message);
