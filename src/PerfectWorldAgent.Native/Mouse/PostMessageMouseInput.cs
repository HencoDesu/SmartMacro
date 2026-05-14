using System.Runtime.Versioning;
using PerfectWorldAgent.Native.Internal;

namespace PerfectWorldAgent.Native.Mouse;

[SupportedOSPlatform("windows")]
public sealed class PostMessageMouseInput : IMouseInput
{
    private readonly TimeSpan _interClickDelay;

    public PostMessageMouseInput(TimeSpan? interClickDelay = null)
    {
        _interClickDelay = interClickDelay ?? TimeSpan.FromMilliseconds(50);
    }

    public async Task ClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var coord = LParamHelpers.MakeCoordLParam(x, y);
        Post(hwnd, User32Native.WM_LBUTTONDOWN, (IntPtr)User32Native.MK_LBUTTON, coord);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        Post(hwnd, User32Native.WM_LBUTTONUP, IntPtr.Zero, coord);
    }

    public async Task DoubleClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var coord = LParamHelpers.MakeCoordLParam(x, y);
        var wDown = (IntPtr)User32Native.MK_LBUTTON;
        var wUp = IntPtr.Zero;

        Post(hwnd, User32Native.WM_LBUTTONDOWN, wDown, coord);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        Post(hwnd, User32Native.WM_LBUTTONUP, wUp, coord);

        await Task.Delay(_interClickDelay, cancellationToken).ConfigureAwait(false);

        Post(hwnd, User32Native.WM_LBUTTONDBLCLK, wDown, coord);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        Post(hwnd, User32Native.WM_LBUTTONUP, wUp, coord);
    }

    private static void Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!User32Native.PostMessage(hwnd, msg, wParam, lParam))
        {
            throw new InvalidOperationException($"PostMessage 0x{msg:X4} failed for hwnd 0x{hwnd:X}");
        }
    }
}
