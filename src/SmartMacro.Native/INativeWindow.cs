using System.Runtime.Versioning;

namespace SmartMacro.Native;

// Per-hwnd window handle wrapper. Methods operate on the encapsulated Handle — no hwnd
// parameter on the API. Instances are typically obtained via INativeWindowSystem.Open(hwnd).
[SupportedOSPlatform("windows")]
public interface INativeWindow
{
    IntPtr Handle { get; }

    /// <summary>
    /// Cheap aliveness check — returns <c>false</c> once the underlying window has been
    /// destroyed (e.g. game client crashed or was closed). Backed by <c>User32.IsWindow</c>
    /// on Windows; safe to call from any thread.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// Client-area width/height in pixels.
    /// </summary>
    (int Width, int Height) GetClientSize();

    /// <summary>
    /// Converts client-relative coordinates to screen-absolute coordinates.
    /// </summary>
    (int X, int Y) ClientToScreen(int clientX, int clientY);

    /// <summary>
    /// Converts screen-absolute coordinates to client-relative coordinates of this window.
    /// May return negative numbers / values outside <see cref="GetClientSize"/> when the
    /// point is not physically inside the window.
    /// </summary>
    (int X, int Y) ScreenToClient(int screenX, int screenY);

    /// <summary>
    /// Tries to bring this window to the foreground, including the AttachThreadInput
    /// workaround modern Windows requires for non-foreground processes.
    /// </summary>
    /// <returns><c>false</c> if all activation attempts failed.</returns>
    bool BringToFront();

    /// <summary>
    /// Sends the <c>WM_ACTIVATEAPP</c> wake-up signal — Perfect World's client freezes
    /// background windows; this snaps the engine awake long enough for a subsequent input
    /// call to be processed.
    /// </summary>
    /// <param name="lParam">Magic value carried over from a known-working helper. PW ignores it but it's exposed in case future client versions start validating.</param>
    void SendActivationSignal(uint lParam);

    /// <summary>
    /// Sends the matching deactivation signal — tells the window it's no longer the active
    /// app, putting PW's render loop back into its low-power background state. Must be
    /// paired with <see cref="SendActivationSignal"/> on background windows so they don't
    /// all stay at full render rate after a broadcast (11 windows × full render = noticeable
    /// game lag).
    /// </summary>
    void SendDeactivationSignal();

    /// <summary>
    /// Captures the client area into a PNG <c>byte[]</c> via <c>PrintWindow</c>. Will
    /// return a blank image if the caller's integrity level is below the target's — PW
    /// launches elevated, so the agent must too.
    /// </summary>
    byte[] CapturePng();

    /// <summary>
    /// Replaces the window's title-bar / taskbar icon. Loads the image file from disk
    /// (.ico via <c>LoadImage</c>, .png / .jpg / .bmp via GDI+), cached process-wide so
    /// each class icon is decoded once and reused across all 9 agents. Sends
    /// <c>WM_SETICON</c> for SMALL + BIG + SMALL2 covering title bar, Alt-Tab, taskbar
    /// list, taskbar button.
    /// </summary>
    /// <returns><c>false</c> if the file couldn't be loaded (missing, corrupt, unsupported).</returns>
    bool SetIconFromFile(string imagePath);
}
