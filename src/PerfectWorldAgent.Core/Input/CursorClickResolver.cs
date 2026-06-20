using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Window;

namespace PerfectWorldAgent.Input;

// Translates "user pressed BroadcastClick hotkey" into a concrete (x, y) in client
// coordinates suitable for broadcasting to every agent — or null if guards say "don't
// broadcast". The orchestrator owns the agent set and the broadcast itself; this class
// owns the cursor / foreground / coordinate-translation logic.
//
// Two guards prevent firing when the user isn't actually pointing at a game client:
//   1. Foreground window must be one of the tracked agents — otherwise the user is in
//      another app (browser, IDE, panel) and the hotkey was likely accidental.
//   2. Cursor must be inside the foreground's client area — otherwise they're on the
//      title bar / over a different monitor / partially-occluded window; the translated
//      coords would be negative or out-of-bounds and the broadcast would click garbage
//      positions in every agent's window.
public sealed partial class CursorClickResolver
{
    private readonly ILogger<CursorClickResolver> _logger;

    public CursorClickResolver(ILogger<CursorClickResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves the broadcast-click target. Returns the client-space point to broadcast,
    /// or <c>null</c> with a logged reason if either guard fails (foreground not a
    /// tracked agent, or cursor outside client area).
    /// </summary>
    /// <param name="trackedAgentHandles">Snapshot of currently-tracked agent hwnds used for the foreground-is-agent check.</param>
    /// <param name="doubleClick">Pass-through to log labels; doesn't affect the resolution logic itself.</param>
    public ScreenPoint? TryResolve(IReadOnlyCollection<IntPtr> trackedAgentHandles, bool doubleClick)
    {
        var kind = doubleClick ? "DoubleClick" : "Click";
        var (screenX, screenY) = Win32NativeWindowSystem.GetCursorPos();
        var screen = new ScreenPoint(screenX, screenY);
        var foreground = Win32NativeWindowSystem.GetForeground();

        if (foreground.Handle == IntPtr.Zero)
        {
            LogNoForeground(screen, doubleClick);
            return null;
        }

        if (!trackedAgentHandles.Contains(foreground.Handle))
        {
            LogForegroundNotAgent(kind, foreground.Handle.ToInt64(), screen);
            return null;
        }

        var (clientX, clientY) = foreground.ScreenToClient(screenX, screenY);
        var client = new ScreenPoint(clientX, clientY);
        var (width, height) = foreground.GetClientSize();
        if (clientX < 0 || clientY < 0 || clientX >= width || clientY >= height)
        {
            LogCursorOutsideClient(kind, screen, client, width, height);
            return null;
        }

        LogResolved(kind, client);
        return client;
    }

    [LoggerMessage(LogLevel.Information, "Broadcast{Kind} hotkey pressed — client={Point} on all agents")]
    partial void LogResolved(string kind, ScreenPoint point);

    [LoggerMessage(LogLevel.Warning, "Broadcast click hotkey pressed but no foreground window — screen={Screen} doubleClick={DoubleClick}; skipping")]
    partial void LogNoForeground(ScreenPoint screen, bool doubleClick);

    [LoggerMessage(LogLevel.Information, "Broadcast{Kind} suppressed — foreground hwnd=0x{Hwnd:X} is not a tracked agent (cursor screen={Screen})")]
    partial void LogForegroundNotAgent(string kind, long hwnd, ScreenPoint screen);

    [LoggerMessage(LogLevel.Information, "Broadcast{Kind} suppressed — cursor screen={Screen} maps to client={Client} which is outside [0,{W})x[0,{H})")]
    partial void LogCursorOutsideClient(string kind, ScreenPoint screen, ScreenPoint client, int w, int h);
}
