namespace SmartMacro.Macros.Execution;

/// <summary>
/// Основание для сбоев чтения переменных прогона. Исполнитель ловит всё это семейство и
/// превращает в обрыв прогона с записью в лог: явный сбой лучше молчаливого промаха.
/// </summary>
public abstract class MacroVariableException : Exception
{
    private protected MacroVariableException(string message) : base(message)
    {
    }
}

/// <summary>Бросается, когда нода читает переменную, которую в этом прогоне никто не записывал.</summary>
public sealed class MacroVariableNotFoundException : MacroVariableException
{
    public MacroVariableNotFoundException(string name)
        : base($"Variable '{name}' is not defined in this macro run.")
    {
        Name = name;
    }

    /// <summary>Имя запрошенной переменной.</summary>
    public string Name { get; }
}

/// <summary>Бросается, когда переменная есть, но держит значение не того рода (например, PointVar называет строку).</summary>
public sealed class MacroVariableTypeMismatchException : MacroVariableException
{
    public MacroVariableTypeMismatchException(string name, string expected, VariableValue actual)
        : base($"Variable '{name}' is not a {expected} (actual value: {actual.DisplayString}).")
    {
        Name = name;
    }

    /// <summary>Имя запрошенной переменной.</summary>
    public string Name { get; }
}
