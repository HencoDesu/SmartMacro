using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Keyboard;

// Asynchronous message-queue input. Targets a specific hwnd without requiring focus.
// Most likely to work with cooperative apps; many DirectInput/Raw Input games may ignore it.
//
// ⚠️ NOT WIRED, and deliberately not the default: GameWindowFactory picks
// SendMessageKeyboardInput because post'd keys were observed being dropped by frozen
// background PW clients (1-2 of 9 windows at random — the queue never got pumped). This
// class stays as the alternative strategy for a cooperative, non-freezing target process;
// do not swap it in for PW without re-testing in game.
[SupportedOSPlatform("windows")]
public sealed class PostMessageKeyboardInput : IKeyboardInput
{
    private readonly TimeSpan _keyHoldDuration;

    public PostMessageKeyboardInput(TimeSpan? keyHoldDuration = null)
    {
        _keyHoldDuration = keyHoldDuration ?? TimeSpan.FromMilliseconds(50);
    }

    public async Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default)
    {
        var wParam = (IntPtr)(ushort)key;
        var downLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: false);
        var upLParam = LParamHelpers.BuildKeyLParam(key, isKeyUp: true);

        Post(hwnd, User32Native.WM_KEYDOWN, wParam, downLParam);
        await Task.Delay(_keyHoldDuration, cancellationToken).ConfigureAwait(false);
        Post(hwnd, User32Native.WM_KEYUP, wParam, upLParam);
    }

    private static void Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!User32Native.PostMessage(hwnd, msg, wParam, lParam))
        {
            throw new InvalidOperationException($"PostMessage 0x{msg:X4} failed for hwnd 0x{hwnd:X}");
        }
    }
}
