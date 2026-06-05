using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Window;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Core;

[SupportedOSPlatform("windows")]
public sealed class GameWindow : IGameWindow
{
    private readonly IKeyboardInput _keyboard;
    private readonly IMouseInput _mouse;
    private readonly INativeWindow _nativeWindow;
    private readonly uint _activationLParam;
    private readonly int _settleDelayMs;
    private readonly int _deactivationDelayMs;

    public GameWindow(
        ProcessInfo info,
        IKeyboardInput keyboard,
        IMouseInput mouse,
        IOptions<ActivatingInputOptions> activatingOptions)
    {
        if (info.MainWindowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Process must have a non-zero MainWindowHandle.", nameof(info));
        }

        _keyboard = keyboard;
        _mouse = mouse;
        _nativeWindow = Win32NativeWindowSystem.Open(info.MainWindowHandle);
        _activationLParam = activatingOptions.Value.ActivationLParam;
        _settleDelayMs = activatingOptions.Value.SettleDelayMs;
        _deactivationDelayMs = activatingOptions.Value.DeactivationDelayMs;
    }

    public IntPtr Handle => _nativeWindow.Handle;

    public bool IsAlive => _nativeWindow.IsAlive;

    public (int Width, int Height) ClientSize
    {
        get
        {
            try { return _nativeWindow.GetClientSize(); }
            catch { return (0, 0); }
        }
    }

    // PW freezes inactive clients (input + rendering pause). Activate sends the wake-up
    // WM_ACTIVATEAPP signal so subsequent input is processed; settle delay lets the
    // engine actually come back online before we start posting input.
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _nativeWindow.SendActivationSignal(_activationLParam);
        if (_settleDelayMs > 0)
        {
            await Task.Delay(_settleDelayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    // Deactivation drain: PostMessage-based input lands in the target's queue but isn't
    // yet processed when we return from PressKeyAsync. If we deactivate immediately, PW
    // processes the deactivation first and (observed) discards pending posted input on
    // going inactive. The drain delay gives the message pump time to dequeue and process
    // pending input. After draining, we send WM_ACTIVATEAPP(FALSE) — unless this is the
    // window the user is currently interacting with (foreground), in which case we leave
    // it active so we don't yank focus away from them.
    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (_deactivationDelayMs > 0)
        {
            await Task.Delay(_deactivationDelayMs, cancellationToken).ConfigureAwait(false);
        }
        if (Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }
    }

    public Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default) =>
        _keyboard.SendKeyAsync(Handle, key, cancellationToken);

    public Task PressChordAsync(VirtualKey modifier, VirtualKey key, CancellationToken cancellationToken = default) =>
        _keyboard.SendChordAsync(Handle, modifier, key, cancellationToken);

    public Task ClickAsync(int x, int y, CancellationToken cancellationToken = default) =>
        _mouse.ClickAsync(Handle, x, y, cancellationToken);

    public Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default) =>
        _mouse.DoubleClickAsync(Handle, x, y, cancellationToken);

    // Self-contained — manages its own activation/deactivation because it's a sync API
    // called from one-shot UI paths (Label dialog, Dump captures) that don't need to
    // coordinate with a broader input session. Thread.Sleep instead of Task.Delay keeps
    // the API sync; settle is short enough (20 ms default) that the brief block on the
    // calling thread is imperceptible.
    public byte[] CaptureScreenshot()
    {
        _nativeWindow.SendActivationSignal(_activationLParam);
        Thread.Sleep(_settleDelayMs);
        var png = _nativeWindow.CapturePng();
        if (Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }
        return png;
    }

    // No activation, no deactivation — just whatever frame DWM has for this window.
    // Used by polling loops (identification, future boss/quest checks). The WM_ACTIVATEAPP
    // traffic from active CaptureScreenshot on 9 windows every 2s was disrupting the
    // user's manual window focus in dungeons; passive capture removes that interference.
    // Trade-off: a frozen background window may return a stale or partial frame — for
    // periodic identification that just means "try again next tick", not a correctness
    // issue. The user can always force a fresh capture via the Label dialog.
    public byte[] CaptureScreenshotPassive() => _nativeWindow.CapturePng();

    public bool SetIconFromFile(string imagePath) => _nativeWindow.SetIconFromFile(imagePath);
}
