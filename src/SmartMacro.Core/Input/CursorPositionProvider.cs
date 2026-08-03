using Microsoft.Extensions.Logging;
using SmartMacro.Native;
using SmartMacro.Native.Window;
using SmartMacro.Windows;

namespace SmartMacro.Input;

/// <summary>
/// Поставляет переменную прогона <c>cursor</c>, которой засевается каждый прогон макроса.
///
/// В отличие от обвешанного проверками пути рассылки кликов, который он собой заменил, этот
/// ВСЕГДА возвращает точку: переменная — это данные, и отказ её определить обрывал бы любой
/// прогон, который её читает. Решение о том, означает ли «курсор вне игры» — «не кликать»,
/// теперь принадлежит автору макроса (через теговые селекторы), а не слою триггеров.
///
/// Точка выражена в КЛИЕНТСКИХ координатах окна переднего плана, если это одно из наших окон:
/// именно в этом пространстве координат работает <c>ClickNode</c>, а все клиенты в сборке с
/// несколькими окнами делят одну раскладку — навёл на умение в том окне, на которое смотришь, и
/// кликнется то же самое умение во всех. Когда на переднем плане что-то иное (браузер, IDE),
/// пересчитывать не относительно чего, поэтому возвращаются сырые экранные координаты, и это
/// пишется в лог.
/// </summary>
public sealed partial class CursorPositionProvider
{
    private readonly WindowRegistry _windows;
    private readonly ILogger<CursorPositionProvider> _logger;

    public CursorPositionProvider(WindowRegistry windows, ILogger<CursorPositionProvider> logger)
    {
        _windows = windows;
        _logger = logger;
    }

    /// <summary>Текущая позиция курсора — по возможности в клиентских координатах управляемого окна.</summary>
    public ScreenPoint Current()
    {
        var (screenX, screenY) = Win32NativeWindowSystem.GetCursorPos();
        var screen = new ScreenPoint(screenX, screenY);

        try
        {
            var foreground = Win32NativeWindowSystem.GetForeground();
            if (foreground.Handle == IntPtr.Zero || _windows.TryGetWindow(foreground.Handle) is null)
            {
                LogScreenSpace(screen);
                return screen;
            }

            var (clientX, clientY) = foreground.ScreenToClient(screenX, screenY);
            var client = new ScreenPoint(clientX, clientY);
            LogClientSpace(screen, client, foreground.Handle.ToInt64());
            return client;
        }
        catch (Exception ex)
        {
            // Окно переднего плана может умереть между двумя вызовами; экранные координаты —
            // вполне годный запасной вариант.
            LogTranslationFailed(ex, screen);
            return screen;
        }
    }

    [LoggerMessage(LogLevel.Debug,
        "Переменная cursor = {Client} (клиентские координаты окна переднего плана hwnd=0x{Hwnd:X}, экранные {Screen})")]
    partial void LogClientSpace(ScreenPoint screen, ScreenPoint client, long hwnd);

    [LoggerMessage(LogLevel.Debug, "Переменная cursor = {Screen} (экранные координаты — окно переднего плана не наше)")]
    partial void LogScreenSpace(ScreenPoint screen);

    [LoggerMessage(LogLevel.Debug, "Пересчёт курсора в клиентские координаты не удался; берём экранные {Screen}")]
    partial void LogTranslationFailed(Exception ex, ScreenPoint screen);
}
