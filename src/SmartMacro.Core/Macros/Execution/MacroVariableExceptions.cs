namespace SmartMacro.Macros.Execution;

/// <summary>
/// Base for run-variable read failures. The executor catches this family and turns it
/// into a run abort with a log entry — an explicit failure beats a silent miss.
/// </summary>
public abstract class MacroVariableException : Exception
{
    private protected MacroVariableException(string message) : base(message) { }
}

/// <summary>Thrown when a node reads a variable that was never written in this run.</summary>
public sealed class MacroVariableNotFoundException : MacroVariableException
{
    public MacroVariableNotFoundException(string name)
        : base($"Variable '{name}' is not defined in this macro run.")
    {
        Name = name;
    }

    /// <summary>The variable name that was requested.</summary>
    public string Name { get; }
}

/// <summary>Thrown when a variable exists but holds the wrong kind of value (e.g. PointVar names a string).</summary>
public sealed class MacroVariableTypeMismatchException : MacroVariableException
{
    public MacroVariableTypeMismatchException(string name, string expected, VariableValue actual)
        : base($"Variable '{name}' is not a {expected} (actual value: {actual.DisplayString}).")
    {
        Name = name;
    }

    /// <summary>The variable name that was requested.</summary>
    public string Name { get; }
}
