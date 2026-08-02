using System.Diagnostics;
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
// Targeted use: keyboard input for broadcasts where reliability matters more than
// latency (immunity, assist). Mouse input stays on PostMessage since clicks already
// reach PW reliably and SendMessage adds blocking overhead for no observed gain.
[SupportedOSPlatform("windows")]
public sealed class SendMessageKeyboardInput : IKeyboardInput
{
    private readonly TimeSpan _keyHoldDuration;
    private readonly TimeSpan _interEventDelay;

    public SendMessageKeyboardInput(TimeSpan? keyHoldDuration = null, TimeSpan? interEventDelay = null)
    {
        _keyHoldDuration = keyHoldDuration ?? TimeSpan.FromMilliseconds(50);
        _interEventDelay = interEventDelay ?? TimeSpan.FromMilliseconds(30);
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

    public async Task SendChordAsync(IntPtr hwnd, VirtualKey modifier, VirtualKey key, CancellationToken cancellationToken = default)
    {
        var modWParam = (IntPtr)(ushort)modifier;
        var keyWParam = (IntPtr)(ushort)key;
        var modDown = LParamHelpers.BuildKeyLParam(modifier, isKeyUp: false);
        var modUp = LParamHelpers.BuildKeyLParam(modifier, isKeyUp: true);
        var keyDown = LParamHelpers.BuildKeyLParam(key, isKeyUp: false);
        var keyUp = LParamHelpers.BuildKeyLParam(key, isKeyUp: true);

        // Uniform spacing between every event — mirrors the proven hardware-macro pattern
        // (Razer/Bloody/etc. that work reliably for PW). Sending modifier-down and key-down
        // back-to-back risks PW processing the key before its internal "modifier held"
        // state has updated (especially if PW polls modifier state via GetKeyboardState
        // rather than tracking WM_KEYDOWN events directly). The inter-event delay gives
        // PW a tick to register each event before the next arrives.
        //
        // Sequence: mod-down → gap → key-down → hold → key-up → gap → mod-up
        DebugLog($"chord[{modifier}+{key}] hwnd=0x{hwnd.ToInt64():X} → WM_KEYDOWN({modifier})");
        User32Native.SendMessage(hwnd, User32Native.WM_KEYDOWN, modWParam, modDown);
        await Task.Delay(_interEventDelay, cancellationToken).ConfigureAwait(false);
        DebugLog($"chord[{modifier}+{key}] → WM_KEYDOWN({key})");
        User32Native.SendMessage(hwnd, User32Native.WM_KEYDOWN, keyWParam, keyDown);
        await Task.Delay(_keyHoldDuration, cancellationToken).ConfigureAwait(false);
        DebugLog($"chord[{modifier}+{key}] → WM_KEYUP({key})");
        User32Native.SendMessage(hwnd, User32Native.WM_KEYUP, keyWParam, keyUp);
        await Task.Delay(_interEventDelay, cancellationToken).ConfigureAwait(false);
        DebugLog($"chord[{modifier}+{key}] → WM_KEYUP({modifier})");
        User32Native.SendMessage(hwnd, User32Native.WM_KEYUP, modWParam, modUp);
        DebugLog($"chord[{modifier}+{key}] done");
    }

    // Lightweight chord-trace — uses Debug.WriteLine (also captured by Serilog when
    // configured) and only fires when DEBUG-class log routing is enabled. Avoids dragging
    // ILogger into this Native-tier class which has no logger.
    [Conditional("DEBUG")]
    private static void DebugLog(string msg)
    {
        Debug.WriteLine($"[SendMessageKeyboardInput] {msg}");
    }
}
