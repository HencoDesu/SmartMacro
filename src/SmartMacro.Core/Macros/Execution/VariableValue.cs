using System.Globalization;
using SmartMacro.Native;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Value of one run variable: a string, a number, or a screen point. Closed union —
/// v1 deliberately has no expressions or other types (that's ScriptNode territory).
/// Implicit conversions exist from all three underlying types so call sites read
/// naturally: <c>variables.Set("cursor", point)</c>.
/// </summary>
public abstract record VariableValue
{
    private protected VariableValue() { }

    /// <summary>String form used by <c>{var}</c> interpolation (invariant culture for numbers).</summary>
    public abstract string DisplayString { get; }

    public static implicit operator VariableValue(string value) => new StringValue(value);
    public static implicit operator VariableValue(double value) => new NumberValue(value);
    public static implicit operator VariableValue(ScreenPoint value) => new PointValue(value);
}

/// <summary>A string variable.</summary>
public sealed record StringValue(string Value) : VariableValue
{
    /// <inheritdoc />
    public override string DisplayString => Value;
}

/// <summary>A numeric variable.</summary>
public sealed record NumberValue(double Value) : VariableValue
{
    /// <inheritdoc />
    public override string DisplayString => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A screen-point variable (e.g. <c>cursor</c>, <c>FoundPointVar</c> results).</summary>
public sealed record PointValue(ScreenPoint Value) : VariableValue
{
    /// <inheritdoc />
    public override string DisplayString => Value.ToString();
}
