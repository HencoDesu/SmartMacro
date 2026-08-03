using System.Globalization;
using SmartMacro.Native;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Значение одной переменной прогона: строка, число или точка на экране. Закрытое
/// объединение — в первой версии намеренно нет ни выражений, ни других типов (это вотчина
/// ScriptNode). Неявные преобразования есть от всех трёх лежащих в основе типов, чтобы места
/// вызова читались естественно: <c>variables.Set("cursor", point)</c>.
/// </summary>
public abstract record VariableValue
{
    private protected VariableValue()
    {
    }

    /// <summary>Строковая форма, которую подставляет интерполяция <c>{var}</c> (числа — в инвариантной культуре).</summary>
    public abstract string DisplayString { get; }

    public static implicit operator VariableValue(string value) => new StringValue(value);
    public static implicit operator VariableValue(double value) => new NumberValue(value);
    public static implicit operator VariableValue(ScreenPoint value) => new PointValue(value);
}

/// <summary>Строковая переменная.</summary>
public sealed record StringValue(string Value) : VariableValue
{
    /// <inheritdoc />
    public override string DisplayString => Value;
}

/// <summary>Числовая переменная.</summary>
public sealed record NumberValue(double Value) : VariableValue
{
    /// <inheritdoc />
    public override string DisplayString => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Переменная-точка на экране (например, <c>cursor</c> или результат <c>FoundPointVar</c>).</summary>
public sealed record PointValue(ScreenPoint Value) : VariableValue
{
    /// <inheritdoc />
    public override string DisplayString => Value.ToString();
}
