using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using SmartMacro.Config;
using SmartMacro.Native;
using SmartMacro.Native.Window;
using SmartMacro.ProcessMonitoring;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SmartMacro.GameWindows;

[SupportedOSPlatform("windows")]
public sealed partial class GameWindow : IGameWindow
{
    private readonly IKeyboardInput _keyboard;
    private readonly IMouseInput _mouse;
    private readonly INativeWindow _nativeWindow;
    // Null = plain-input process (no profile / no ActivationLParam configured): the
    // whole WM_ACTIVATEAPP wake-up/deactivate dance is skipped.
    private readonly uint? _activationLParam;
    private readonly int _settleDelayMs;
    private readonly int _deactivationDelayMs;
    private readonly double _matchThreshold;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<GameWindow> _logger;

    public GameWindow(
        ProcessInfo info,
        ProcessProfile profile,
        IKeyboardInput keyboard,
        IMouseInput mouse,
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
        _activationLParam = profile.ActivationLParam;
        _settleDelayMs = profile.SettleDelayMs;
        _deactivationDelayMs = profile.DeactivationDelayMs;
        _matchThreshold = visionOptions.Value.MatchThreshold;
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
    // engine actually come back online before we start posting input. Plain-input
    // processes (no ActivationLParam in their profile) skip the signal entirely.
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activationLParam is { } lParam)
        {
            _nativeWindow.SendActivationSignal(lParam);
        }
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
        if (_activationLParam is null)
        {
            // Plain-input window — we never activated it, so there's nothing to drain
            // or put back to sleep.
            return;
        }
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
        if (_activationLParam is { } lParam)
        {
            _nativeWindow.SendActivationSignal(lParam);
            Thread.Sleep(_settleDelayMs);
        }
        var png = _nativeWindow.CapturePng();
        if (_activationLParam is not null && Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }
        return png;
    }

    public bool SetIconFromFile(string imagePath) => _nativeWindow.SetIconFromFile(imagePath);

    // One-shot sibling of WaitForElementAsync: a single fresh capture and a single match
    // pass, no polling. FindElementNode branches on the outcome immediately, so a poll
    // budget here would just be a hidden wait the graph author didn't ask for.
    public async Task<ScreenPoint?> FindElementAsync(byte[] elementTemplate, ScreenRect position, CancellationToken cancellationToken = default)
    {
        byte[] capture;
        try
        {
            capture = await CaptureFreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(ex);
            return null;
        }

        if (TryMatchOnce(capture, elementTemplate, position, out var score, out var center))
        {
            LogMatchHit(position, score, _matchThreshold);
            return center;
        }
        LogMatchMiss(position, score, _matchThreshold);
        return null;
    }

    // Poll-based template matcher. Loop captures actively, crops to position (or
    // fullscreen if position is empty), grayscales both source and template, runs
    // MatchTemplate (CCoeffNormed), returns the match CENTER on the first tick whose max
    // score crosses MatchThreshold. Returns null on timeout.
    public async Task<ScreenPoint?> WaitForElementAsync(byte[] elementTemplate, ScreenRect position, TimeSpan waitDuration, CancellationToken cancellationToken = default)
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

            if (TryMatchOnce(capture, elementTemplate, position, out var score, out var center))
            {
                LogMatchHit(position, score, _matchThreshold);
                return center;
            }
            LogMatchMiss(position, score, _matchThreshold);

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // Async sibling of CaptureScreenshot — wakes the window via WM_ACTIVATEAPP, waits
    // the settle delay, captures, then re-freezes (unless this window IS foreground,
    // in which case it stays active naturally). Used by the one-shot and poll-loop matchers.
    private async Task<byte[]> CaptureFreshAsync(CancellationToken cancellationToken)
    {
        if (_activationLParam is { } lParam)
        {
            _nativeWindow.SendActivationSignal(lParam);
            if (_settleDelayMs > 0)
            {
                await Task.Delay(_settleDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
        var png = _nativeWindow.CapturePng();
        if (_activationLParam is not null && Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }
        return png;
    }

    // Single match pass. `center` is the CLIENT-space center of the best match — the crop
    // origin is added back in so callers get a point they can hand straight to ClickAsync,
    // which is the whole point of FoundPointVar ("find the button anywhere, then click it").
    private bool TryMatchOnce(byte[] sourcePng, byte[] templatePng, ScreenRect position, out double score, out ScreenPoint center)
    {
        score = 0.0;
        center = default;
        using var sourceFull = Cv2.ImDecode(sourcePng, ImreadModes.Color);
        if (sourceFull.Empty()) return false;

        // Empty region = search the full frame.
        var rect = position.Width <= 0 || position.Height <= 0
            ? new Rect(0, 0, sourceFull.Width, sourceFull.Height)
            : ClampToImage(new Rect(position.X, position.Y, position.Width, position.Height), sourceFull.Size());

        using var crop = new Mat(sourceFull, rect);
        using var sourceGray = ToGrayscale(crop);

        using var templateBgr = Cv2.ImDecode(templatePng, ImreadModes.Color);
        if (templateBgr.Empty()) return false;
        using var templateGray = ToGrayscale(templateBgr);

        if (templateGray.Width > sourceGray.Width || templateGray.Height > sourceGray.Height)
        {
            LogTemplateLargerThanRegion(templateGray.Width, templateGray.Height, sourceGray.Width, sourceGray.Height);
            return false;
        }

        // Grayscale + CCoeffNormed instead of binarize + CCoeffNormed: many game-UI
        // elements (chat panel icons etc.) sit on semi-transparent darkened backgrounds
        // where bleed-through from the world below makes binarization unstable. CCoeff
        // subtracts the mean and normalises by stddev so brightness shifts cancel out.
        // ClassMatcher is the one place that still binarises, because its subject (class
        // text on the opaque stats panel) separates cleanly at a fixed luminance cut.
        using var result = new Mat();
        Cv2.MatchTemplate(sourceGray, templateGray, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);
        score = maxVal;
        // maxLoc is the template's top-left inside the CROP; shift by the crop origin to
        // get client space, then by half the template to land on the center.
        center = new ScreenPoint(
            rect.X + maxLoc.X + (templateGray.Width / 2),
            rect.Y + maxLoc.Y + (templateGray.Height / 2));
        return score >= _matchThreshold;
    }

    private static Mat ToGrayscale(Mat bgr)
    {
        var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Rect ClampToImage(Rect rect, Size imageSize)
    {
        var x = Math.Max(0, Math.Min(rect.X, imageSize.Width - 1));
        var y = Math.Max(0, Math.Min(rect.Y, imageSize.Height - 1));
        var w = Math.Min(rect.Width, imageSize.Width - x);
        var h = Math.Min(rect.Height, imageSize.Height - y);
        return new Rect(x, y, w, h);
    }

    [LoggerMessage(LogLevel.Warning, "Template match: template ({TplW}x{TplH}) is larger than search region ({SrcW}x{SrcH}) — shrink template or grow region")]
    partial void LogTemplateLargerThanRegion(int tplW, int tplH, int srcW, int srcH);

    [LoggerMessage(LogLevel.Debug, "Template match: hit at {Region} score={Score:F3} >= {Threshold:F3}")]
    partial void LogMatchHit(ScreenRect region, double score, double threshold);

    [LoggerMessage(LogLevel.Debug, "Template match: miss at {Region} score={Score:F3} < {Threshold:F3}")]
    partial void LogMatchMiss(ScreenRect region, double score, double threshold);

    [LoggerMessage(LogLevel.Debug, "Template match: capture failed (transient — minimised window, GPU stall)")]
    partial void LogCaptureFailed(Exception ex);
}
