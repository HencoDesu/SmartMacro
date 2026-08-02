using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Keyboard;

// Synchronous keyboard input via SendMessage. Each WM_KEYDOWN/UP blocks until the
// target window's WndProc returns — guaranteeing PW has actually handled the keypress
// before we proceed. Trade-off vs PostMessage:
//
//   * GOOD: no queue-ordering subtleties, no "did PW dequeue this yet?" races. If PW
//     has a "process keys only when active" check inside its WndProc, the activation
//     state set by our (synchronous) WM_ACTIVATEAPP propagates before the key arrives.
//   * BAD: blocks our task thread for the duration of PW's handler. Under heavy load
//     (PW mid-render) a key send could take dozens of milliseconds. PostMessage is
//     fire-and-forget.
//
// This is the strategy GameWindowFactory bakes in for every keypress: post'd keys were
// observed getting dropped by frozen background clients (1-2 of 9 windows at random).
// Mouse input stays on PostMessage since clicks already reach PW reliably and SendMessage
// adds blocking overhead for no observed gain.
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
