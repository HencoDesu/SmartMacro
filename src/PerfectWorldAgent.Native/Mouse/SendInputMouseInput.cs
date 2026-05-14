using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native.Internal;
using PerfectWorldAgent.Native.Window;

namespace PerfectWorldAgent.Native.Mouse;

// System-level mouse injection via SendInput. Requires target window to be foreground —
// uses Win32NativeWindowSystem to materialize per-hwnd handles for the focus switch and to
// read screen size for the 0..65535 absolute coordinate normalisation.
//
// Timings come from SendInputOptions.
[SupportedOSPlatform("windows")]
public sealed class SendInputMouseInput : IMouseInput
{
    private readonly TimeSpan _focusSettleDelay;
    private readonly TimeSpan _interClickDelay;
    private readonly bool _restoreOriginalFocus;

    public SendInputMouseInput(IOptions<SendInputOptions> options)
    {
        var values = options.Value;
        _focusSettleDelay = TimeSpan.FromMilliseconds(values.FocusSettleDelayMs);
        _interClickDelay = TimeSpan.FromMilliseconds(values.InterClickDelayMs);
        _restoreOriginalFocus = values.RestoreOriginalFocus;
    }

    public async Task ClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var original = Win32NativeWindowSystem.GetForeground();
        var target = Win32NativeWindowSystem.Open(hwnd);
        target.BringToFront();
        await Task.Delay(_focusSettleDelay, cancellationToken).ConfigureAwait(false);

        try
        {
            var (screenX, screenY) = target.ClientToScreen(x, y);
            var (screenW, screenH) = Win32NativeWindowSystem.GetScreenSize();
            var absX = (screenX * 65535) / Math.Max(1, screenW - 1);
            var absY = (screenY * 65535) / Math.Max(1, screenH - 1);

            var moveDown = User32Native.MOUSEEVENTF_MOVE | User32Native.MOUSEEVENTF_ABSOLUTE | User32Native.MOUSEEVENTF_LEFTDOWN;
            var up = User32Native.MOUSEEVENTF_ABSOLUTE | User32Native.MOUSEEVENTF_LEFTUP;

            SendMouse(absX, absY, moveDown);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            SendMouse(absX, absY, up);
        }
        finally
        {
            if (_restoreOriginalFocus && original.Handle != IntPtr.Zero && original.Handle != hwnd)
            {
                original.BringToFront();
            }
        }
    }

    public async Task DoubleClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var original = Win32NativeWindowSystem.GetForeground();
        var target = Win32NativeWindowSystem.Open(hwnd);
        target.BringToFront();
        await Task.Delay(_focusSettleDelay, cancellationToken).ConfigureAwait(false);

        try
        {
            var (screenX, screenY) = target.ClientToScreen(x, y);
            var (screenW, screenH) = Win32NativeWindowSystem.GetScreenSize();
            var absX = (screenX * 65535) / Math.Max(1, screenW - 1);
            var absY = (screenY * 65535) / Math.Max(1, screenH - 1);

            var moveDown = User32Native.MOUSEEVENTF_MOVE | User32Native.MOUSEEVENTF_ABSOLUTE | User32Native.MOUSEEVENTF_LEFTDOWN;
            var up = User32Native.MOUSEEVENTF_ABSOLUTE | User32Native.MOUSEEVENTF_LEFTUP;

            SendMouse(absX, absY, moveDown);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            SendMouse(absX, absY, up);

            await Task.Delay(_interClickDelay, cancellationToken).ConfigureAwait(false);

            SendMouse(absX, absY, moveDown);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            SendMouse(absX, absY, up);
        }
        finally
        {
            if (_restoreOriginalFocus && original.Handle != IntPtr.Zero && original.Handle != hwnd)
            {
                original.BringToFront();
            }
        }
    }

    private static void SendMouse(int absX, int absY, uint flags)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = User32Native.INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = absX,
                        dy = absY,
                        mouseData = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero,
                    },
                },
            },
        };
        var result = User32Native.SendInput((uint)inputs.Length, inputs, INPUT.Size);
        if (result != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput mouse injection failed (result={result})");
        }
    }
}
