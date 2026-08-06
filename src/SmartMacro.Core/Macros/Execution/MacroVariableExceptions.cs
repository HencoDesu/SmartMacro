using System.Globalization;
using SmartMacro.Resources;

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
        : base(string.Format(CultureInfo.CurrentCulture, Strings_Engine.Run_Variable_NotDefined, name))
    {
        Name = name;
    }

    /// <summary>Имя запрошенной переменной.</summary>
    public string Name { get; }
}

/// <summary>Бросается, когда переменная есть, но держит значение не того рода (например, PointVar называет строку).</summary>
public sealed class MacroVariableTypeMismatchException : MacroVariableException
{
    /// <param name="name">Имя запрошенной переменной.</param>
    /// <param name="expected">
    /// Ожидавшийся род значения — русское существительное в именительном падеже («точка»):
    /// оно встаёт прямо в текст сообщения, который читает пользователь панели.
    /// </param>
    /// <param name="actual">То, что в переменной лежит на самом деле.</param>
    public MacroVariableTypeMismatchException(string name, string expected, VariableValue actual)
        : base(string.Format(
            CultureInfo.CurrentCulture,
            Strings_Engine.Run_Variable_TypeMismatch,
            name,
            expected,
            actual.DisplayString))
    {
        Name = name;
    }

    /// <summary>Имя запрошенной переменной.</summary>
    public string Name { get; }
}
