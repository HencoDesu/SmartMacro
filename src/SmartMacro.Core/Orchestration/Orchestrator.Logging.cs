using Microsoft.Extensions.Logging;

namespace SmartMacro.Orchestration;

// Кодогенерируемые объявления LoggerMessage для Orchestrator. Вынесены в отдельный файл
// частичного класса, чтобы основной Orchestrator.cs читался как логика «триггер → прогон», а не
// как заготовки логгера. На поведение не влияет.
public sealed partial class Orchestrator
{
    [LoggerMessage(LogLevel.Information, "Цикл раздачи оркестратора запущен")]
    partial void LogDispatchLoopStarted();

    [LoggerMessage(LogLevel.Information, "Цикл раздачи оркестратора остановлен")]
    partial void LogDispatchLoopStopped();

    [LoggerMessage(LogLevel.Debug, "Необработанное сообщение снизу: {Type}")]
    partial void LogUnhandledUpstreamMessageType(string type);

    [LoggerMessage(LogLevel.Information,
        "Появился процесс (от монитора): pid={Pid} имя='{ProcessName}' hwnd=0x{Hwnd:X}")]
    partial void LogProcessAppearedNotification(int pid, string processName, long hwnd);

    [LoggerMessage(LogLevel.Error, "Не удалось создать агента для pid={Pid}")]
    partial void LogAgentCreationFailed(Exception ex, int pid);

    [LoggerMessage(LogLevel.Information, "Триггер хоткея → макрос '{MacroName}'")]
    partial void LogHotkeyTriggered(string macroName);

    [LoggerMessage(LogLevel.Information,
        "Появился процесс '{ProcessName}' → запускаем макрос '{MacroName}' на hwnd=0x{Hwnd:X}")]
    partial void LogProcessMacroStarting(string macroName, string processName, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Макроса '{MacroName}' нет в библиотеке — триггер отброшен")]
    partial void LogMacroNotFound(string macroName);

    [LoggerMessage(LogLevel.Warning, "Макрос '{MacroName}' оборван: {Reason}")]
    partial void LogMacroAborted(string macroName, string reason);

    [LoggerMessage(LogLevel.Error, "Макрос '{MacroName}' упал непредвиденно")]
    partial void LogMacroFailed(Exception ex, string macroName);
}
