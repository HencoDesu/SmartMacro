using Microsoft.Extensions.Logging;

namespace SmartMacro.Agents;

// Кодогенерируемые объявления LoggerMessage для CharacterAgent. Вынесены в отдельный файл
// частичного класса ради единообразия с остальной кодовой базой — даже несмотря на то, что в
// W0.2b агент усох до оболочки над временем жизни окна.
public sealed partial class CharacterAgent
{
    [LoggerMessage(LogLevel.Information, "Агент hwnd=0x{Hwnd:X} следит за своим окном (опрос {Poll})")]
    partial void LogStarted(long hwnd, TimeSpan poll);

    [LoggerMessage(LogLevel.Information, "Цикл агента hwnd=0x{Hwnd:X} остановлен")]
    partial void LogStopped(long hwnd);

    [LoggerMessage(LogLevel.Information, "Окно агента больше не живо; выходим из цикла")]
    partial void LogWindowGone();

    [LoggerMessage(LogLevel.Error, "Цикл агента упал непредвиденно")]
    partial void LogRunFailed(Exception ex);
}
