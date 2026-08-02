using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;
using SmartMacro.Windows;

namespace SmartMacro.Vision;

/// <summary>
/// The "Dump captures" diagnostic: for every window the registry knows about, write what
/// the vision pipeline actually sees into <c>debug/</c> next to the executable.
///
///   <c>{process}-{tags}-full.png</c>      — the raw PrintWindow capture
///   <c>{process}-{tags}-class-bin.png</c> — <see cref="IClassMatcher.DebugBinarizeClassRegion"/>,
///                                            i.e. the mask MatchTemplate is handed
///
/// Between the two you can tell a mis-placed region apart from an unreadable binarisation
/// — the two failure modes that look identical from "the macro didn't find the element".
/// Open the in-game stats window (default <c>C</c>) before dumping, otherwise the class
/// region is empty by construction.
///
/// Lived in <c>MainWindow.axaml.cs</c> until stage 2B. It moved here because the captures
/// have to be taken in the process that owns the windows: after the split the UI has no
/// <see cref="WindowRegistry"/>, no OpenCV, and no <see cref="IGameWindow"/> handles — it
/// sends <c>DumpCaptures</c>, gets a folder path back, and opens Explorer on it.
///
/// Nothing here throws for a single bad window: a capture that fails writes a sibling
/// <c>.error.txt</c> and the sweep continues, because the usual reason to dump captures is
/// that something is already broken.
/// </summary>
public sealed partial class CaptureDumpService
{
    /// <summary>Name of the dump folder, relative to the app directory.</summary>
    public const string FolderName = "debug";

    private readonly WindowRegistry _windows;
    private readonly IClassMatcher _matcher;
    private readonly ILogger<CaptureDumpService> _logger;

    /// <summary>Production constructor: <c>debug/</c> next to the executable.</summary>
    public CaptureDumpService(WindowRegistry windows, IClassMatcher matcher, ILogger<CaptureDumpService> logger)
        : this(AppContext.BaseDirectory, windows, matcher, logger)
    {
    }

    /// <param name="baseDirectory">Folder that will contain <c>debug/</c>. Tests point this at a temp dir.</param>
    /// <param name="windows">Source of the windows to capture and of their hwnd → facade lookup.</param>
    /// <param name="matcher">Supplies the binarised class-region view.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public CaptureDumpService(
        string baseDirectory,
        WindowRegistry windows,
        IClassMatcher matcher,
        ILogger<CaptureDumpService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _windows = windows;
        _matcher = matcher;
        _logger = logger;
        FolderPath = Path.Combine(baseDirectory, FolderName);
    }

    /// <summary>Absolute path of the dump folder. Exists only after the first <see cref="DumpAsync"/>.</summary>
    public string FolderPath { get; }

    /// <summary>
    /// Captures every registered window and writes the two PNGs per window.
    /// </summary>
    /// <returns>
    /// <see cref="FolderPath"/> — the caller (UI, over IPC) only needs somewhere to point
    /// Explorer at. A registry with no windows still yields the created, empty folder
    /// rather than an error: "nothing was captured" is itself the diagnostic.
    /// </returns>
    public async Task<string> DumpAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(FolderPath);

        var captured = 0;
        var failed = 0;
        foreach (var info in _windows.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The registry is the only hwnd → drivable-window lookup there is; an entry
            // registered without a facade (or a window that died since the snapshot) simply
            // has nothing to capture.
            if (_windows.TryGetWindow(info.Hwnd) is not { } window)
            {
                continue;
            }

            var stem = StemFor(info);

            byte[] fullCapture;
            try
            {
                fullCapture = window.CaptureScreenshot();
                await File.WriteAllBytesAsync(Path.Combine(FolderPath, $"{stem}-full.png"), fullCapture, cancellationToken)
                    .ConfigureAwait(false);
                captured++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCaptureFailed(ex, info.Hwnd.ToInt64());
                await WriteErrorAsync($"{stem}.error.txt", ex, cancellationToken).ConfigureAwait(false);
                failed++;
                continue;
            }

            try
            {
                var binarised = _matcher.DebugBinarizeClassRegion(fullCapture);
                await File.WriteAllBytesAsync(Path.Combine(FolderPath, $"{stem}-class-bin.png"), binarised, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBinarizeFailed(ex, info.Hwnd.ToInt64());
                await WriteErrorAsync($"{stem}-class-bin.error.txt", ex, cancellationToken).ConfigureAwait(false);
                failed++;
            }
        }

        LogDumpFinished(captured, failed, FolderPath);
        return FolderPath;
    }

    // "{process}-{tag+tag}", or the handle when the window has no tags yet — which is the
    // interesting case anyway (a dump is usually taken because identification failed).
    private static string StemFor(ManagedWindowInfo info)
    {
        var label = info.Tags.Count > 0
            ? string.Join('+', info.Tags)
            : $"0x{info.Hwnd.ToInt64():X}";
        return SafeFileName($"{info.ProcessName}-{label}");
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        return new string(chars);
    }

    private async Task WriteErrorAsync(string fileName, Exception ex, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(FolderPath, fileName), ex.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
        {
            // The dump folder itself is unwritable. Already logged the real failure above;
            // losing the .txt copy of it is not worth aborting the sweep for.
        }
    }

    [LoggerMessage(LogLevel.Warning, "Capture dump: PrintWindow failed for hwnd=0x{Hwnd:X}")]
    partial void LogCaptureFailed(Exception ex, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Capture dump: class-region binarisation failed for hwnd=0x{Hwnd:X}")]
    partial void LogBinarizeFailed(Exception ex, long hwnd);

    [LoggerMessage(LogLevel.Information, "Capture dump finished: {Captured} window(s) captured, {Failed} failure(s) → '{Folder}'")]
    partial void LogDumpFinished(int captured, int failed, string folder);
}
