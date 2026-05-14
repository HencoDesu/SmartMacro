using System.Runtime.Versioning;
using PerfectWorldAgent.Native.Internal;

namespace PerfectWorldAgent.Native.Keyboard;

// Synchronous message dispatch. Like PostMessage but blocks until the target's WndProc returns.
// Useful when an app processes WM_KEYDOWN inside its WndProc rather than reading the queue
// asynchronously. Same fundamental limitation as PostMessage for DirectInput/Raw Input games.
[SupportedOSPlatform("windows")]
public sealed class SendMessageKeyboardInput : IKeyboardInput
{
    private readonly TimeSpan _keyHoldDuration;

    public SendMessageKeyboardInput(TimeSpan? keyHoldDuration = null)
    {
        _keyHoldDuration = keyHoldDuration ?? TimeSpan.FromMilliseconds(50);
    }

    public async Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default)
    {
        var wParam = (IntPtr)(ushort)key;
        var downLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: false);
        var upLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: true);

        User32Native.SendMessage(hwnd, User32Native.WM_KEYDOWN, wParam, downLParam);
        await Task.Delay(_keyHoldDuration, cancellationToken).ConfigureAwait(false);
        User32Native.SendMessage(hwnd, User32Native.WM_KEYUP, wParam, upLParam);
    }
}
