using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;
using SmartMacro.Input;
using SmartMacro.Native;
using SmartMacro.Presentation;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// The real implementation of <see cref="IMacroPrimitives"/>: turns the walker's
/// hwnd-addressed operations into input, vision, and window cosmetics.
///
/// Every operation starts by resolving the hwnd through <see cref="WindowRegistry"/> —
/// the registry owns both the tag state selectors match against and the
/// <see cref="IGameWindow"/> facade that can actually drive the window. An hwnd with no
/// registered facade (window died mid-run, or was registered tag-only) is a logged no-op
/// rather than an exception: fan-outs routinely race window teardown and one dead window
/// must not abort a run that legitimately targeted eight others.
///
/// Activation lifecycle lives in <see cref="AgentInputDispatcher"/>, so each key/click is
/// its own wake → send → drain → sleep cycle. Node-level granularity means a Delay node
/// between two key nodes really does let the window go back to sleep in between; that
/// matches how the graph reads and is what the legacy per-message path did too.
/// </summary>
public sealed partial class MacroPrimitives : IMacroPrimitives
{
    private readonly WindowRegistry _windows;
    private readonly AgentInputDispatcher _input;
    private readonly TemplateSetProvider _templates;
    private readonly IClassMatcher _matcher;
    private readonly WindowIconService _icons;
    private readonly ILogger<MacroPrimitives> _logger;

    public MacroPrimitives(
        WindowRegistry windows,
        AgentInputDispatcher input,
        TemplateSetProvider templates,
        IClassMatcher matcher,
        WindowIconService icons,
        ILogger<MacroPrimitives> logger)
    {
        _windows = windows;
        _input = input;
        _templates = templates;
        _matcher = matcher;
        _icons = icons;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PressKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(PressKeyAsync)) is not { } window)
        {
            return Task.CompletedTask;
        }
        return _input.FireKeyAsync(window, key, $"Key({key})", Describe(hwnd));
    }

    /// <inheritdoc />
    public Task ClickAsync(IntPtr hwnd, ScreenPoint point, bool doubleClick, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(ClickAsync)) is not { } window)
        {
            return Task.CompletedTask;
        }
        return _input.FireClickAsync(window, point, doubleClick, Describe(hwnd));
    }

    /// <inheritdoc />
    public async Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, string template, ScreenRect? region, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(FindElementAsync)) is not { } window)
        {
            return null;
        }
        if (_templates.TryGetTemplate(template) is not { } bytes)
        {
            LogTemplateUnavailable(template);
            return null;
        }
        return await window.FindElementAsync(bytes, region ?? default, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, string template, ScreenRect? region, int timeoutMs, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(WaitForElementAsync)) is not { } window)
        {
            return null;
        }
        if (_templates.TryGetTemplate(template) is not { } bytes)
        {
            LogTemplateUnavailable(template);
            return null;
        }
        var budget = TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs));
        return await window.WaitForElementAsync(bytes, region ?? default, budget, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string?> RecognizeAsync(IntPtr hwnd, string templateSet, ScreenRect region, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(RecognizeAsync)) is not { } window)
        {
            return Task.FromResult<string?>(null);
        }

        var templates = _templates.GetSet(templateSet);
        if (templates.Count == 0)
        {
            LogEmptyTemplateSet(templateSet);
            return Task.FromResult<string?>(null);
        }

        ct.ThrowIfCancellationRequested();

        // ACTIVE capture. By the time this node runs, the key press that opened the
        // in-game panel has already completed its own activate/deactivate cycle, so a
        // passive PrintWindow would likely return the frozen pre-panel frame.
        byte[] screenshot;
        try
        {
            screenshot = window.CaptureScreenshot();
        }
        catch (Exception ex)
        {
            LogCaptureFailed(ex, hwnd.ToInt64());
            return Task.FromResult<string?>(null);
        }

        try
        {
            var match = _matcher.Match(screenshot, templates, region);
            if (match is null)
            {
                LogRecognizeNoMatch(templateSet, hwnd.ToInt64());
                return Task.FromResult<string?>(null);
            }
            LogRecognized(match.Tag, match.Score, templateSet);
            return Task.FromResult<string?>(match.Tag);
        }
        catch (Exception ex)
        {
            LogRecognizeFailed(ex, templateSet);
            return Task.FromResult<string?>(null);
        }
    }

    /// <inheritdoc />
    public Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(SetIconAsync)) is { } window)
        {
            _icons.TryApply(window, iconPath);
        }
        return Task.CompletedTask;
    }

    private IGameWindow? Resolve(IntPtr hwnd, string operation)
    {
        var window = _windows.TryGetWindow(hwnd);
        if (window is null)
        {
            LogUnknownWindow(operation, hwnd.ToInt64());
        }
        return window;
    }

    // Log label for input operations: the window's tags read far better in a log than a
    // bare handle when nine clients are running.
    private string Describe(IntPtr hwnd)
    {
        var tags = _windows.GetTags(hwnd);
        return tags.Count > 0 ? string.Join("/", tags) : $"hwnd=0x{hwnd.ToInt64():X}";
    }

    [LoggerMessage(LogLevel.Warning, "{Operation}: hwnd=0x{Hwnd:X} has no drivable window in the registry — skipping")]
    partial void LogUnknownWindow(string operation, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Template '{Template}' is unavailable — node treated as 'not found'")]
    partial void LogTemplateUnavailable(string template);

    [LoggerMessage(LogLevel.Warning, "Template set '{TemplateSet}' is empty — node treated as 'not matched'")]
    partial void LogEmptyTemplateSet(string templateSet);

    [LoggerMessage(LogLevel.Warning, "Capture failed for hwnd=0x{Hwnd:X} during recognition")]
    partial void LogCaptureFailed(Exception ex, long hwnd);

    [LoggerMessage(LogLevel.Information, "Recognized '{Tag}' (score {Score:F3}) from set '{TemplateSet}'")]
    partial void LogRecognized(string tag, double score, string templateSet);

    [LoggerMessage(LogLevel.Information, "No template of set '{TemplateSet}' matched on hwnd=0x{Hwnd:X}")]
    partial void LogRecognizeNoMatch(string templateSet, long hwnd);

    [LoggerMessage(LogLevel.Error, "Recognition against set '{TemplateSet}' threw")]
    partial void LogRecognizeFailed(Exception ex, string templateSet);
}
