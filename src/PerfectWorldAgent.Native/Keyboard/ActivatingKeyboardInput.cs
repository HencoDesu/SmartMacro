using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native.Window;

namespace PerfectWorldAgent.Native.Keyboard;

// Decorator that prefixes every key send with a WM_ACTIVATEAPP signal to wake a frozen
// background window before the inner sender does its job. Perfect World freezes inactive
// clients; this snaps the engine out of the frozen state long enough to process the
// subsequent key.
//
// Timing and the activation lParam come from ActivatingInputOptions — see that class
// for rationale on each field.
[SupportedOSPlatform("windows")]
public sealed class ActivatingKeyboardInput : IKeyboardInput
{
    private readonly IKeyboardInput _inner;
    private readonly uint _activationLParam;
    private readonly TimeSpan _settleDelay;

    public ActivatingKeyboardInput(IKeyboardInput inner, IOptions<ActivatingInputOptions> options)
    {
        _inner = inner;
        var values = options.Value;
        _activationLParam = values.ActivationLParam;
        _settleDelay = TimeSpan.FromMilliseconds(values.SettleDelayMs);
    }

    public async Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default)
    {
        var window = Win32NativeWindowSystem.Open(hwnd);
        window.SendActivationSignal(_activationLParam);
        await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);
        await _inner.SendKeyAsync(hwnd, key, cancellationToken).ConfigureAwait(false);

        // Put the window back to sleep — unless it's the one the user is actually
        // interacting with. Without this, every broadcast leaves all 11 PW clients
        // rendering at full speed → noticeable game lag.
        if (Win32NativeWindowSystem.GetForeground().Handle != hwnd)
        {
            window.SendDeactivationSignal();
        }
    }
}
