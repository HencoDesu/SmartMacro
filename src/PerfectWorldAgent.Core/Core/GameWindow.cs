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

    public Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default) =>
        _keyboard.SendKeyAsync(Handle, key, cancellationToken);

    public Task ClickAsync(int x, int y, CancellationToken cancellationToken = default) =>
        _mouse.ClickAsync(Handle, x, y, cancellationToken);

    public Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default) =>
        _mouse.DoubleClickAsync(Handle, x, y, cancellationToken);

    // PW freezes background windows — both input AND rendering pause. PrintWindow on a
    // frozen client returns a stale or empty DWM thumbnail. Same wake-up trick as
    // ActivatingKeyboardInput/MouseInput: send WM_ACTIVATEAPP, let DWM compose a fresh
    // frame, then capture. After the capture, send the matching deactivation signal so
    // the client goes back to its low-power background state — otherwise polling every
    // 2 s on 11 windows leaves them all rendering at full speed and the game lags.
    // Skip deactivation for the foreground window (the one the user is interacting with).
    // Thread.Sleep instead of Task.Delay keeps the API sync — settle is short enough
    // (20 ms default) that the brief block on the calling thread is imperceptible.
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
}
