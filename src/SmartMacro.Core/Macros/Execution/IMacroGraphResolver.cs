using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Разрешает имя макроса в его граф во время исполнения (<see cref="RunMacroNode"/> ищет
/// макросы лениво, чтобы библиотека могла меняться между прогонами). В W0.2b это реализовано
/// поверх хранилища макросов; тесты обходятся словарём.
/// </summary>
public interface IMacroGraphResolver
{
    /// <summary>Граф, зарегистрированный под именем <paramref name="name"/>, или <c>null</c>, если такого нет.</summary>
    MacroGraph? TryGet(string name);
}
