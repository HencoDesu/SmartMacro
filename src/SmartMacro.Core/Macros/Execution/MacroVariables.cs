using System.Text.RegularExpressions;
using SmartMacro.Native;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Run-scoped variables: name → <see cref="VariableValue"/> (string | number | point).
/// Written by the trigger (always seeds <see cref="CursorVariableName"/>) and by
/// conditional nodes (<c>ResultVar</c>/<c>FoundPointVar</c>); read via <c>PointVar</c>
/// and <c>{name}</c> interpolation in string parameters. Sub-runs get a <see cref="Clone"/>,
/// never the same instance — writes don't leak across runs.
///
/// Not thread-safe by design: only the single walker of one run writes; parallel fan-outs
/// only read, and sub-runs own their clones.
/// </summary>
public sealed partial class MacroVariables
{
    /// <summary>Variable every trigger writes: the cursor position at fire time.</summary>
    public const string CursorVariableName = "cursor";

    private readonly Dictionary<string, VariableValue> _values;

    /// <summary>Creates an empty variable set.</summary>
    public MacroVariables()
    {
        _values = new Dictionary<string, VariableValue>(StringComparer.Ordinal);
    }

    private MacroVariables(Dictionary<string, VariableValue> values)
    {
        _values = values;
    }

    /// <summary>Number of defined variables.</summary>
    public int Count => _values.Count;

    /// <summary>
    /// Creates the variable set for a fresh trigger-initiated run: <c>cursor</c> is set to
    /// the cursor position at fire time. Every launch path (hotkey, process-appeared,
    /// UI Run) goes through this.
    /// </summary>
    public static MacroVariables ForTrigger(ScreenPoint cursorPosition)
    {
        var variables = new MacroVariables();
        variables.Set(CursorVariableName, cursorPosition);
        return variables;
    }

    /// <summary>Sets (or overwrites) a variable. Names are case-sensitive.</summary>
    public void Set(string name, VariableValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[name] = value;
    }

    /// <summary>Non-throwing read.</summary>
    public bool TryGet(string name, out VariableValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _values.TryGetValue(name, out value!);
    }

    /// <summary>Reads a variable; missing name throws <see cref="MacroVariableNotFoundException"/>.</summary>
    public VariableValue Get(string name)
    {
        return TryGet(name, out var value) ? value : throw new MacroVariableNotFoundException(name);
    }

    /// <summary>
    /// Reads a variable that must hold a point (ClickNode.PointVar). Missing name throws
    /// <see cref="MacroVariableNotFoundException"/>; a non-point value throws
    /// <see cref="MacroVariableTypeMismatchException"/>.
    /// </summary>
    public ScreenPoint GetPoint(string name)
    {
        var value = Get(name);
        return value is PointValue point
            ? point.Value
            : throw new MacroVariableTypeMismatchException(name, "point", value);
    }

    /// <summary>
    /// Replaces every <c>{name}</c> placeholder in <paramref name="template"/> with the
    /// variable's display string. A placeholder referencing an undefined variable throws
    /// <see cref="MacroVariableNotFoundException"/> — the executor aborts the run.
    /// </summary>
    public string Interpolate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return PlaceholderRegex().Replace(template, match => Get(match.Groups[1].Value).DisplayString);
    }

    /// <summary>Independent copy for a sub-run: reads inherit, writes never flow back.</summary>
    public MacroVariables Clone()
    {
        return new MacroVariables(new Dictionary<string, VariableValue>(_values, StringComparer.Ordinal));
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderRegex();
}
