using SmartMacro.ProcessMonitoring;

namespace SmartMacro.GameWindows;

// Создаёт IGameWindow, привязанный к конкретному процессу игрового клиента. Прячет всю сборку
// стратегий ввода (какие реализации клавиатуры и мыши, какие декораторы) за одним вызовом
// «дай мне процесс — получи окно», и благодаря этому обработчику ProcessAppeared в
// оркестраторе не нужно знать про типы из Native.
public interface IGameWindowFactory
{
    /// <summary>
    /// Создаёт <see cref="IGameWindow"/> поверх дескриптора главного окна из
    /// <paramref name="info"/>, собирая настроенную стратегию ввода (по умолчанию PostMessage +
    /// побудка через WM_ACTIVATEAPP).
    /// </summary>
    IGameWindow Create(ProcessInfo info);
}
