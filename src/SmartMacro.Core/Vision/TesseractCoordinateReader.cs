using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using SmartMacro.Models;
using Tesseract;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Rect = OpenCvSharp.Rect;

namespace SmartMacro.Vision;

// Tesseract-backed coordinate reader. Pipeline:
//   1. Crop to the fixed HUD coord region (top-right corner, tuned for 3840×2160).
//   2. 3× cubic-interpolated upscale — Tesseract's LSTM works best on text 40-50 px
//      tall and PW's digits are only ~25 px at native 4K.
//   3. Grayscale + luminance threshold — drops the cyan location name and dim
//      background, keeps the bright yellow digit text.
//   4. Invert to black-on-white (LSTM training assumption).
//   5. OCR with PageSegMode.SingleLine.
//   6. Regex out "XXX, ZZZ · YY" (with comma + optional separator) → Coordinates(X,Z,Y).
//
// Accuracy note: Tesseract's general-purpose LSTM model has stable misreads on PW's
// custom digit font (notably 5→6, 1→7 in some contexts). Errors are POSITION-PRESERVING
// per glyph, so delta-based logic (stuck detection, master-follower distance) still
// works correctly — the bias cancels out on subtraction. If exact values become
// important, the right fix is training a Tesseract model on PW's font, or switching to
// digit-template matching with cropped reference glyphs. Either is a separate workstream.
//
// TesseractEngine isn't thread-safe per its docs, so we lock around Process. With 9
// agents at ~2s poll and ~50 ms per Read, contention is negligible.
[SupportedOSPlatform("windows")]
public sealed partial class TesseractCoordinateReader : ICoordinateReader, IDisposable
{
    // Hand-tuned for 3840×2160 captures (standard 4K UHD) at default in-game UI scale.
    // Wide enough for varying location-name length (the coords are right-aligned to the
    // HUD edge; the location name pushes them left when long) plus the height-component.
    // Note: the location name (in cyan/teal) co-lives in this region — but it's far less
    // bright than the digit text, so the luminance threshold below filters it out.
    private static readonly Rect CoordRegion = new(3460, 12, 380, 48);

    // Luminance threshold for binarisation. PW digit text renders near-white with a
    // saturated yellow tint; in grayscale that's ~Y=200+. The smaller height-component
    // digits are slightly less bright, so we keep the threshold conservative (180) to
    // catch them. The cyan location name in the same strip is closer to Y=150 and drops
    // out — at the cost of some text-edge speckle that Tesseract handles fine.
    private const double LuminanceThreshold = 180.0;

    // Match the exact coord triple pattern "XXX, YYY · ZZ" — three integers separated by
    // a comma and either a bullet (·), period (.), or middle-dot (•). Tesseract may
    // garble the bullet character; we accept several alternatives. Skipping the
    // free-form "find any 3 integers" approach because the sun-icon binarises into an
    // artifact that's sometimes read as a stray digit, polluting a naive int-extraction.
    private static readonly Regex CoordPattern = new(
        @"(-?\d+)\s*,\s*(-?\d+)\s*[·.•:|\-]?\s*(-?\d+)",
        RegexOptions.Compiled);

    private readonly TesseractEngine _engine;
    private readonly object _lock = new();
    private readonly ILogger<TesseractCoordinateReader> _logger;

    public TesseractCoordinateReader(
        IOptions<CoordinateReaderOptions> options,
        ILogger<TesseractCoordinateReader> logger)
    {
        _logger = logger;
        _engine = new TesseractEngine(options.Value.TessdataPath, "eng", EngineMode.LstmOnly);
    }

    public Coordinates? Read(byte[] screenshot)
    {
        byte[] preprocessed;
        try
        {
            preprocessed = Preprocess(screenshot);
        }
        catch (Exception ex)
        {
            LogPreprocessFailed(ex);
            return null;
        }

        string text;
        try
        {
            lock (_lock)
            {
                using var pix = Pix.LoadFromMemory(preprocessed);
                using var page = _engine.Process(pix, PageSegMode.SingleLine);
                text = page.GetText() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            LogOcrFailed(ex);
            return null;
        }

        var match = CoordPattern.Match(text);
        if (!match.Success)
        {
            LogParseFailed(text.Trim());
            return null;
        }

        if (!int.TryParse(match.Groups[1].ValueSpan, out var x) ||
            !int.TryParse(match.Groups[2].ValueSpan, out var z) ||
            !int.TryParse(match.Groups[3].ValueSpan, out var y))
        {
            LogParseFailed(text.Trim());
            return null;
        }

        var coords = new Coordinates(x, z, y);
        LogRead(text.Trim(), coords);
        return coords;
    }

    // Diagnostic — returns the binarised view that Tesseract actually sees. Used by
    // VisionSampleRunner to eyeball the preprocessing pipeline.
    public byte[] DebugBinarize(byte[] screenshot) => Preprocess(screenshot);

    // Diagnostic — returns the raw cropped coord region (full colour). Lets us verify
    // the region is positioned correctly before any HSV/threshold work.
    public byte[] DebugCrop(byte[] screenshot)
    {
        using var full = Cv2.ImDecode(screenshot, ImreadModes.Color);
        if (full.Empty())
        {
            throw new InvalidOperationException("Failed to decode screenshot bytes.");
        }
        var clamped = ClampToImage(CoordRegion, full.Size());
        using var crop = new Mat(full, clamped);
        Cv2.ImEncode(".png", crop, out var bytes);
        return bytes;
    }

    private static byte[] Preprocess(byte[] screenshot)
    {
        using var full = Cv2.ImDecode(screenshot, ImreadModes.Color);
        if (full.Empty())
        {
            throw new InvalidOperationException("Failed to decode screenshot bytes.");
        }

        var clamped = ClampToImage(CoordRegion, full.Size());
        using var crop = new Mat(full, clamped);

        // Upscale 3× before binarisation. At native 4K the digit glyphs are ~25 px tall,
        // well below Tesseract's comfort zone (40-50 px). Bicubic preserves stroke shape
        // better than nearest-neighbor for antialiased text.
        using var upscaled = new Mat();
        Cv2.Resize(crop, upscaled, new Size(0, 0), 3.0, 3.0, InterpolationFlags.Cubic);

        using var gray = new Mat();
        Cv2.CvtColor(upscaled, gray, ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, LuminanceThreshold, 255, ThresholdTypes.Binary);

        // Tesseract is trained on black text on white background — invert so the digits
        // are dark and the surroundings are light.
        using var inverted = new Mat();
        Cv2.BitwiseNot(binary, inverted);

        Cv2.ImEncode(".png", inverted, out var bytes);
        return bytes;
    }

    private static Rect ClampToImage(Rect rect, Size imageSize)
    {
        var x = Math.Max(0, Math.Min(rect.X, imageSize.Width - 1));
        var y = Math.Max(0, Math.Min(rect.Y, imageSize.Height - 1));
        var w = Math.Min(rect.Width, imageSize.Width - x);
        var h = Math.Min(rect.Height, imageSize.Height - y);
        return new Rect(x, y, w, h);
    }

    public void Dispose() => _engine.Dispose();

    [LoggerMessage(LogLevel.Debug, "Read coords {Coords} from raw '{RawText}'")]
    partial void LogRead(string rawText, Coordinates coords);

    [LoggerMessage(LogLevel.Debug, "Failed to parse coords from raw OCR '{RawText}'")]
    partial void LogParseFailed(string rawText);

    [LoggerMessage(LogLevel.Warning, "Coordinate preprocessing failed")]
    partial void LogPreprocessFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "Tesseract OCR failed on coord region")]
    partial void LogOcrFailed(Exception ex);
}
