using Microsoft.Extensions.Logging;

namespace SmartMacro.Agents;

// Кодогенерируемые объявления LoggerMessage для CharacterAgent. Вынесены в отдельный файл
// частичного класса ради единообразия с остальной кодовой базой — даже несмотря на то, что в
// W0.2b агент усох до оболочки над временем жизни окна.
public sealed partial class CharacterAgent
{
    [LoggerMessage(LogLevel.Information, "Agent hwnd=0x{Hwnd:X} watching its window (poll={Poll})")]
    partial void LogStarted(long hwnd, TimeSpan poll);

    [LoggerMessage(LogLevel.Information, "Agent hwnd=0x{Hwnd:X} run loop stopped")]
    partial void LogStopped(long hwnd);

    [LoggerMessage(LogLevel.Information, "Agent window no longer alive; exiting run loop")]
    partial void LogWindowGone();

    [LoggerMessage(LogLevel.Error, "Agent run loop failed unexpectedly")]
    partial void LogRunFailed(Exception ex);
}
