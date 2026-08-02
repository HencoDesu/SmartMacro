using Microsoft.Extensions.Logging;
using SmartMacro.Native;
using SmartMacro.Native.Window;
using SmartMacro.Windows;

namespace SmartMacro.Input;

/// <summary>
/// Supplies the <c>cursor</c> run variable that every macro run is seeded with.
///
/// Unlike the guard-heavy broadcast-click path it replaces, this ALWAYS returns a point:
/// a variable is data, and refusing to define it would abort any run that reads it. The
/// decision of whether pointing outside the game means "don't click" now belongs to the
/// macro author (via tag selectors), not to the trigger layer.
///
/// The point is expressed in the CLIENT space of the foreground window when that window
/// is one we manage, because that's the coordinate space <c>ClickNode</c> uses and all
/// clients in a multi-boxing setup share one layout — pointing at a skill in the window
/// you're looking at then clicks the same skill in all of them. When the foreground is
/// something else (browser, IDE), there's nothing to translate against, so raw screen
/// coordinates are returned and logged.
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

    /// <summary>Current cursor position, in managed-window client space where possible.</summary>
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
            // Foreground window can die between the two calls; screen coords are a fine fallback.
            LogTranslationFailed(ex, screen);
            return screen;
        }
    }

    [LoggerMessage(LogLevel.Debug, "Cursor variable = {Client} (client space of foreground hwnd=0x{Hwnd:X}, screen {Screen})")]
    partial void LogClientSpace(ScreenPoint screen, ScreenPoint client, long hwnd);

    [LoggerMessage(LogLevel.Debug, "Cursor variable = {Screen} (screen space — foreground window is not one of ours)")]
    partial void LogScreenSpace(ScreenPoint screen);

    [LoggerMessage(LogLevel.Debug, "Cursor translation to client space failed; using screen coordinates {Screen}")]
    partial void LogTranslationFailed(Exception ex, ScreenPoint screen);
}
