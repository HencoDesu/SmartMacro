using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Window;

// Фабрика INativeWindow плюс системные запросы об окнах, не привязанные к конкретному hwnd
// (текущее окно переднего плана, размер экрана). Состояния не держит — все методы просто
// оборачивают вызовы user32, поэтому класс статический, а не синглтон-экземпляр.
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

    // Текущее положение курсора мыши в экранных координатах.
    public static (int X, int Y) GetCursorPos()
    {
        return User32Native.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
    }
}
