using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Window;

// Factory for INativeWindow + system-level window queries that aren't bound to a specific
// hwnd (current foreground, screen size). Stateless — all methods just wrap user32 calls,
// so the class is static rather than a singleton instance.
[SupportedOSPlatform("windows")]
public static class Win32NativeWindowSystem
{
    public static INativeWindow Open(IntPtr hwnd) => new Win32NativeWindow(hwnd);

    public static INativeWindow GetForeground() => new Win32NativeWindow(User32Native.GetForegroundWindow());

    public static (int Width, int Height) GetScreenSize()
    {
        var w = User32Native.GetSystemMetrics(User32Native.SM_CXSCREEN);
        var h = User32Native.GetSystemMetrics(User32Native.SM_CYSCREEN);
        return (w, h);
    }

    // Current mouse cursor position in screen coordinates.
    public static (int X, int Y) GetCursorPos()
    {
        return User32Native.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
    }
}
