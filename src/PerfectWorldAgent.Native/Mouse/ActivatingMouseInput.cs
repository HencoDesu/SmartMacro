using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native.Window;

namespace PerfectWorldAgent.Native.Mouse;

// Decorator mirroring ActivatingKeyboardInput — wakes a frozen background window via
// WM_ACTIVATEAPP before delegating the click to the inner sender. Timings and lParam
// come from ActivatingInputOptions; see ActivatingKeyboardInput for the rationale.
[SupportedOSPlatform("windows")]
public sealed class ActivatingMouseInput : IMouseInput
{
    private readonly IMouseInput _inner;
    private readonly uint _activationLParam;
    private readonly TimeSpan _settleDelay;

    public ActivatingMouseInput(IMouseInput inner, IOptions<ActivatingInputOptions> options)
    {
        _inner = inner;
        var values = options.Value;
        _activationLParam = values.ActivationLParam;
        _settleDelay = TimeSpan.FromMilliseconds(values.SettleDelayMs);
    }

    public async Task ClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var window = Win32NativeWindowSystem.Open(hwnd);
        window.SendActivationSignal(_activationLParam);
        await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);
        await _inner.ClickAsync(hwnd, x, y, cancellationToken).ConfigureAwait(false);
        DeactivateIfBackground(window, hwnd);
    }

    public async Task DoubleClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default)
    {
        var window = Win32NativeWindowSystem.Open(hwnd);
        window.SendActivationSignal(_activationLParam);
        await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);
        await _inner.DoubleClickAsync(hwnd, x, y, cancellationToken).ConfigureAwait(false);
        DeactivateIfBackground(window, hwnd);
    }

    // Put the window back to sleep — unless it's the one the user is actually
    // interacting with. Without this, every broadcast leaves all 11 PW clients
    // rendering at full speed → noticeable game lag.
    private static void DeactivateIfBackground(INativeWindow window, IntPtr hwnd)
    {
        if (Win32NativeWindowSystem.GetForeground().Handle != hwnd)
        {
            window.SendDeactivationSignal();
        }
    }
}
