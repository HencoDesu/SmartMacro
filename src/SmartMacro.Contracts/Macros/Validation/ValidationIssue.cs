// ReSharper disable once CheckNamespace — имена SmartMacro.Macros.* достались валидатору от жизни в Core.

namespace SmartMacro.Macros.Validation;

/// <summary>Степень серьёзности <see cref="ValidationIssue"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>Подозрительно, но работоспособно (недостижимая нода, цикл без пауз, дубликат имени). Показать в редакторе, сохранять разрешить.</summary>
    Warning,

    /// <summary>Граф сломан: прогон прервётся (или вообще не стартует как надо).</summary>
    Error,
}

/// <summary>
/// Одна находка <see cref="MacroGraphValidator.Validate"/>.
///
/// Нода названа дважды и намеренно: <paramref name="NodeId"/> — то, по чему редактор её
/// НАХОДИТ (клик по замечанию подсвечивает коробку на канве), <paramref name="NodeName"/> — то,
/// что он ПОКАЗЫВАЕТ. Одного guid'а мало (читать его человеку незачем), одного имени мало
/// (дубликаты имён — законное предупреждение, а не повод потерять адресата).
/// </summary>
/// <param name="Severity">Блокирует ли это граф или просто выглядит подозрительно.</param>
/// <param name="NodeId">Провинившаяся нода или <c>null</c> для проблем уровня графа (нет стартовой ноды).</param>
/// <param name="NodeName">Её подпись на момент проверки или <c>null</c> вместе с <paramref name="NodeId"/>.</param>
/// <param name="Message">Человекочитаемое описание.</param>
public sealed record ValidationIssue(ValidationSeverity Severity, Guid? NodeId, string? NodeName, string Message);
