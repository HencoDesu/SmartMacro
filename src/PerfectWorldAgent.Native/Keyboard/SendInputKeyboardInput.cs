using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native.Internal;
using PerfectWorldAgent.Native.Window;

namespace PerfectWorldAgent.Native.Keyboard;

// System-level input injection via SendInput. Goes through the full input pipeline (DirectInput,
// Raw Input), so it's the most compatible option for games that ignore window messages — but
// the target window MUST be foreground. Uses Win32NativeWindowSystem to materialize per-hwnd
// handles for the focus switch and to bring the original foreground back if requested.
//
// Timings come from SendInputOptions.
[SupportedOSPlatform("windows")]
public sealed class SendInputKeyboardInput : IKeyboardInput
{
    private readonly TimeSpan _focusSettleDelay;
    private readonly TimeSpan _keyHoldDuration;
    private readonly bool _restoreOriginalFocus;

    public SendInputKeyboardInput(IOptions<SendInputOptions> options)
    {
        var values = options.Value;
        _focusSettleDelay = TimeSpan.FromMilliseconds(values.FocusSettleDelayMs);
        _keyHoldDuration = TimeSpan.FromMilliseconds(values.KeyHoldDurationMs);
        _restoreOriginalFocus = values.RestoreOriginalFocus;
    }

    public async Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default)
    {
        var original = Win32NativeWindowSystem.GetForeground();
        var target = Win32NativeWindowSystem.Open(hwnd);
        target.BringToFront();
        await Task.Delay(_focusSettleDelay, cancellationToken).ConfigureAwait(false);

        try
        {
            SendKey(key, isKeyUp: false);
            await Task.Delay(_keyHoldDuration, cancellationToken).ConfigureAwait(false);
            SendKey(key, isKeyUp: true);
        }
        finally
        {
            if (_restoreOriginalFocus && original.Handle != IntPtr.Zero && original.Handle != hwnd)
            {
                original.BringToFront();
            }
        }
    }

    private static void SendKey(VirtualKey key, bool isKeyUp)
    {
        var scanCode = (ushort)User32Native.MapVirtualKey((uint)key, User32Native.MAPVK_VK_TO_VSC);
        var inputs = new[]
        {
            new INPUT
            {
                type = User32Native.INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)key,
                        wScan = scanCode,
                        dwFlags = isKeyUp ? User32Native.KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero,
                    },
                },
            },
        };
        var result = User32Native.SendInput((uint)inputs.Length, inputs, INPUT.Size);
        if (result != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput keyboard injection failed (result={result})");
        }
    }
}
