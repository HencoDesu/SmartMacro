using System.Runtime.Versioning;

namespace PerfectWorldAgent.Native;

// Per-hwnd window handle wrapper. Methods operate on the encapsulated Handle — no hwnd
// parameter on the API. Instances are typically obtained via INativeWindowSystem.Open(hwnd).
[SupportedOSPlatform("windows")]
public interface INativeWindow
{
    IntPtr Handle { get; }

    // Cheap aliveness check — returns false once the underlying window has been destroyed
    // (e.g. game client crashed or was closed). Backed by User32.IsWindow on Windows; safe
    // to call from any thread.
    bool IsAlive { get; }

    // Client-area width/height in pixels.
    (int Width, int Height) GetClientSize();

    // Convert client-relative coordinates to screen-absolute coordinates.
    (int X, int Y) ClientToScreen(int clientX, int clientY);

    // Convert screen-absolute coordinates to client-relative coordinates of this window.
    // May return negative numbers / values outside ClientSize when the point is not
    // physically inside the window.
    (int X, int Y) ScreenToClient(int screenX, int screenY);

    // Try to bring this window to the foreground, including the AttachThreadInput workaround
    // modern Windows requires for non-foreground processes. Returns false if all attempts
    // failed.
    bool BringToFront();

    // Sends the WM_ACTIVATEAPP wake-up signal — Perfect World's client freezes background
    // windows, this snaps the engine awake long enough for a subsequent input call to be
    // processed. lParam is a magic value carried over from a known-working helper.
    void SendActivationSignal(uint lParam);

    // Send the matching deactivation signal — tells the window it's no longer the active
    // app, putting PW's render loop back into its low-power background state. Must be
    // paired with SendActivationSignal on background windows so they don't all stay at
    // full render rate after a broadcast (11 windows × full render = noticeable game lag).
    void SendDeactivationSignal();

    // Captures the client area into a PNG byte[] via PrintWindow. Will return a blank image
    // if the caller's integrity level is below the target's — PW launches elevated, so the
    // agent must too.
    byte[] CapturePng();

    // Replaces the window's title-bar / taskbar icon. Loads the image file from disk —
    // .ico via LoadImage, .png / .jpg / .bmp via GDI+ — cached process-wide so each
    // class icon is decoded once and reused across all 9 agents. Sends WM_SETICON for
    // SMALL + BIG + SMALL2 covering title bar, Alt-Tab, taskbar list, taskbar button.
    // Returns false if the file couldn't be loaded (missing, corrupt, unsupported).
    bool SetIconFromFile(string imagePath);
}
