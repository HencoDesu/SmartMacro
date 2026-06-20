using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Window;
using PerfectWorldAgent.ProcessMonitoring;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PerfectWorldAgent.GameWindows;

[SupportedOSPlatform("windows")]
public sealed partial class GameWindow : IGameWindow
{
    private readonly IKeyboardInput _keyboard;
    private readonly IMouseInput _mouse;
    private readonly INativeWindow _nativeWindow;
    private readonly uint _activationLParam;
    private readonly int _settleDelayMs;
    private readonly int _deactivationDelayMs;
    private readonly double _matchThreshold;
    private readonly double _luminanceThreshold;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<GameWindow> _logger;

    public GameWindow(
        ProcessInfo info,
        IKeyboardInput keyboard,
        IMouseInput mouse,
        IOptions<ActivatingInputOptions> activatingOptions,
        IOptions<WindowVisionOptions> visionOptions,
        ILogger<GameWindow> logger)
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
        _matchThreshold = visionOptions.Value.MatchThreshold;
        _luminanceThreshold = visionOptions.Value.LuminanceThreshold;
        _pollInterval = TimeSpan.FromMilliseconds(visionOptions.Value.PollIntervalMs);
        _logger = logger;
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

    public Task ClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _mouse.ClickAsync(Handle, point.X, point.Y, cancellationToken);

    public Task DoubleClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _mouse.DoubleClickAsync(Handle, point.X, point.Y, cancellationToken);

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

    // Poll-based template matcher. Loop captures passively, crops to position (or
    // fullscreen if position is empty), binarises both source and template at
    // LuminanceThreshold, runs MatchTemplate (CCoeffNormed), returns true on the first
    // tick whose max score crosses MatchThreshold. Returns false on timeout.
    public async Task<bool> WaitForElementAt(byte[] elementTemplate, ScreenRect position, TimeSpan waitDuration, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + (long)waitDuration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] capture;
            try
            {
                // ACTIVE capture per tick — passive PrintWindow on a frozen background
                // PW client returns stale / black frames, breaking boot polling when
                // the user launches multiple clients back-to-back and each one loses
                // foreground to the next. WM_ACTIVATEAPP unfreezes PW briefly so we
                // get a fresh frame; we re-freeze afterwards (unless foreground) so
                // the user's actual focus isn't disturbed. ~25-50ms overhead per tick.
                capture = await CaptureFreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCaptureFailed(ex);
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (TryMatchOnce(capture, elementTemplate, position, out var score))
            {
                LogMatchHit(position, score, _matchThreshold);
                return true;
            }
            LogMatchMiss(position, score, _matchThreshold);

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    // Async sibling of CaptureScreenshot — wakes the window via WM_ACTIVATEAPP, waits
    // the settle delay, captures, then re-freezes (unless this window IS foreground,
    // in which case it stays active naturally). Used by WaitForElementAt's poll-loop.
    private async Task<byte[]> CaptureFreshAsync(CancellationToken cancellationToken)
    {
        _nativeWindow.SendActivationSignal(_activationLParam);
        if (_settleDelayMs > 0)
        {
            await Task.Delay(_settleDelayMs, cancellationToken).ConfigureAwait(false);
        }
        var png = _nativeWindow.CapturePng();
        if (Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }
        return png;
    }

    private bool TryMatchOnce(byte[] sourcePng, byte[] templatePng, ScreenRect position, out double score)
    {
        score = 0.0;
        using var sourceFull = Cv2.ImDecode(sourcePng, ImreadModes.Color);
        if (sourceFull.Empty()) return false;

        // Empty region = search the full frame.
        var rect = position.Width <= 0 || position.Height <= 0
            ? new Rect(0, 0, sourceFull.Width, sourceFull.Height)
            : ClampToImage(new Rect(position.X, position.Y, position.Width, position.Height), sourceFull.Size());

        using var crop = new Mat(sourceFull, rect);
        using var sourceBin = Binarize(crop);

        using var templateBgr = Cv2.ImDecode(templatePng, ImreadModes.Color);
        if (templateBgr.Empty()) return false;
        using var templateBin = Binarize(templateBgr);

        if (templateBin.Width > sourceBin.Width || templateBin.Height > sourceBin.Height)
        {
            return false;
        }

        using var result = new Mat();
        Cv2.MatchTemplate(sourceBin, templateBin, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out _);
        score = maxVal;
        return score >= _matchThreshold;
    }

    private Mat Binarize(Mat bgr)
    {
        var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var binary = new Mat();
        Cv2.Threshold(gray, binary, _luminanceThreshold, 255, ThresholdTypes.Binary);
        gray.Dispose();
        return binary;
    }

    private static Rect ClampToImage(Rect rect, Size imageSize)
    {
        var x = Math.Max(0, Math.Min(rect.X, imageSize.Width - 1));
        var y = Math.Max(0, Math.Min(rect.Y, imageSize.Height - 1));
        var w = Math.Min(rect.Width, imageSize.Width - x);
        var h = Math.Min(rect.Height, imageSize.Height - y);
        return new Rect(x, y, w, h);
    }

    [LoggerMessage(LogLevel.Debug, "WaitForElementAt: hit at {Region} score={Score:F3} >= {Threshold:F3}")]
    partial void LogMatchHit(ScreenRect region, double score, double threshold);

    [LoggerMessage(LogLevel.Debug, "WaitForElementAt: miss at {Region} score={Score:F3} < {Threshold:F3}")]
    partial void LogMatchMiss(ScreenRect region, double score, double threshold);

    [LoggerMessage(LogLevel.Debug, "WaitForElementAt: capture failed (transient — minimised window, GPU stall)")]
    partial void LogCaptureFailed(Exception ex);
}
