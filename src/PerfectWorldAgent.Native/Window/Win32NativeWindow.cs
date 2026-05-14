using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PerfectWorldAgent.Native.Internal;

namespace PerfectWorldAgent.Native.Window;

[SupportedOSPlatform("windows")]
public sealed class Win32NativeWindow : INativeWindow
{
    public Win32NativeWindow(IntPtr handle)
    {
        Handle = handle;
    }

    public IntPtr Handle { get; }

    public bool IsAlive => Handle != IntPtr.Zero && User32Native.IsWindow(Handle);

    public (int Width, int Height) GetClientSize()
    {
        if (!User32Native.GetClientRect(Handle, out var rect))
        {
            throw new InvalidOperationException(
                $"GetClientRect failed for hwnd 0x{Handle:X} (Win32 error {Marshal.GetLastWin32Error()})");
        }
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public (int X, int Y) ClientToScreen(int clientX, int clientY)
    {
        var point = new POINT { X = clientX, Y = clientY };
        User32Native.ClientToScreen(Handle, ref point);
        return (point.X, point.Y);
    }

    public (int X, int Y) ScreenToClient(int screenX, int screenY)
    {
        var point = new POINT { X = screenX, Y = screenY };
        User32Native.ScreenToClient(Handle, ref point);
        return (point.X, point.Y);
    }

    public bool BringToFront()
    {
        if (User32Native.SetForegroundWindow(Handle))
        {
            return true;
        }

        // Modern Windows blocks SetForegroundWindow from non-foreground processes.
        // AttachThreadInput is the canonical workaround.
        var targetThread = User32Native.GetWindowThreadProcessId(Handle, out _);
        var currentThread = Kernel32Native.GetCurrentThreadId();
        if (targetThread == 0 || targetThread == currentThread)
        {
            return false;
        }

        if (!User32Native.AttachThreadInput(currentThread, targetThread, true))
        {
            return false;
        }

        try
        {
            return User32Native.SetForegroundWindow(Handle);
        }
        finally
        {
            User32Native.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    public void SendActivationSignal(uint lParam)
    {
        // wParam=1 (TRUE) — "this window is being activated". Sending wParam=0 (FALSE)
        // tells the window it's being DEactivated (use SendDeactivationSignal for that).
        // lParam is documented as "thread id of the thread that owns the window being
        // activated/deactivated"; the magic 0x91D8 default is carried over from a
        // known-working third-party helper — PW's engine appears to ignore the value
        // but accepts the message itself as a wake-up trigger.
        User32Native.SendMessage(Handle, User32Native.WM_ACTIVATEAPP, (IntPtr)1, (IntPtr)lParam);
    }

    public void SendDeactivationSignal()
    {
        // wParam=0 (FALSE) — "this window is being deactivated". lParam is per the docs
        // the thread id of the window taking over focus; passing 0 since PW ignores it.
        // Paired with SendActivationSignal on background windows after input/capture so
        // they don't all stay rendering at full speed.
        User32Native.SendMessage(Handle, User32Native.WM_ACTIVATEAPP, IntPtr.Zero, IntPtr.Zero);
    }

    // Captures via PrintWindow with PW_CLIENTONLY | PW_RENDERFULLCONTENT — the second flag
    // is critical for DirectX/DirectComposition windows (the game client is one); without
    // it many such windows return a solid-black image.
    public byte[] CapturePng()
    {
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Window handle is null.");
        }

        var (width, height) = GetClientSize();
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"Window 0x{Handle:X} has zero-sized client area ({width}x{height}) — minimised or destroyed?");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!User32Native.PrintWindow(Handle, hdc, User32Native.PW_CLIENTONLY | User32Native.PW_RENDERFULLCONTENT))
            {
                throw new InvalidOperationException(
                    $"PrintWindow failed for hwnd 0x{Handle:X} (Win32 error {Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
