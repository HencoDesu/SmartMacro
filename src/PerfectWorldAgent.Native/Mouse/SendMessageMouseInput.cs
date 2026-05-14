using System.Runtime.Versioning;
using PerfectWorldAgent.Native.Internal;

namespace PerfectWorldAgent.Native.Mouse;

[SupportedOSPlatform("windows")]
public sealed class SendMessageMouseInput : IMouseInput
{
    private readonly TimeSpan _interClickDelay;

    public SendMessageMouseInput(TimeSpan? interClickDelay = null)
    {
        _interClickDelay = interClickDelay ?? TimeSpan.FromMilliseconds(50);
    }

    public async Task ClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var coord = LParamHelpers.MakeCoordLParam(x, y);
        User32Native.SendMessage(hwnd, User32Native.WM_LBUTTONDOWN, (IntPtr)User32Native.MK_LBUTTON, coord);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        User32Native.SendMessage(hwnd, User32Native.WM_LBUTTONUP, IntPtr.Zero, coord);
    }

    public async Task DoubleClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var coord = LParamHelpers.MakeCoordLParam(x, y);
        var wDown = (IntPtr)User32Native.MK_LBUTTON;
        var wUp = IntPtr.Zero;

        User32Native.SendMessage(hwnd, User32Native.WM_LBUTTONDOWN, wDown, coord);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        User32Native.SendMessage(hwnd, User32Native.WM_LBUTTONUP, wUp, coord);

        await Task.Delay(_interClickDelay, cancellationToken).ConfigureAwait(false);

        User32Native.SendMessage(hwnd, User32Native.WM_LBUTTONDBLCLK, wDown, coord);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        User32Native.SendMessage(hwnd, User32Native.WM_LBUTTONUP, wUp, coord);
    }
}
