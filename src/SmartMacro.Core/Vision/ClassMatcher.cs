using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SmartMacro.Vision;

// OpenCV-based template matcher for the in-game stats-window class-value text. Keyed by
// free tag strings (template filename stems) and tunable via ClassMatcherOptions (region
// + thresholds in appsettings.json):
//   1. Crop the screenshot to the configured stats-window class-value region.
//   2. Convert to grayscale + binarize at LuminanceThreshold to keep just the text.
//   3. For each (tag, template) pair: binarize the template the same way, run
//      Cv2.MatchTemplate with TM_CCOEFF_NORMED, take the max score.
//   4. Return the highest-scoring tag if it crosses MatchThreshold.
[SupportedOSPlatform("windows")]
public sealed partial class ClassMatcher : IClassMatcher
{
    private readonly Rect _region;
    private readonly double _luminanceThreshold;
    private readonly double _matchThreshold;
    private readonly ILogger<ClassMatcher> _logger;

    public ClassMatcher(IOptions<ClassMatcherOptions> options, ILogger<ClassMatcher> logger)
    {
        var v = options.Value;
        _region = new Rect(v.Region.X, v.Region.Y, v.Region.Width, v.Region.Height);
        _luminanceThreshold = v.LuminanceThreshold;
        _matchThreshold = v.MatchThreshold;
        _logger = logger;
        LogConfigured(_region.X, _region.Y, _region.Width, _region.Height, _luminanceThreshold, _matchThreshold);
    }

    public TagMatch? Match(byte[] screenshot, IReadOnlyDictionary<string, byte[]> templates)
    {
        if (templates.Count == 0)
        {
            return null;
        }

        using var sourceCrop = DecodeAndCropAndBinarize(screenshot);

        var brightPixels = Cv2.CountNonZero(sourceCrop);
        var totalPixels = sourceCrop.Width * sourceCrop.Height;
        if (brightPixels < totalPixels * 0.01)
        {
            // Nearly-empty binarised region — typically means the stats window wasn't
            // actually open when the screenshot was taken, or the configured region
            // points at empty UI. Log and decline to match.
            LogEmptyRegion(brightPixels, totalPixels);
            return null;
        }

        TagMatch? best = null;
        foreach (var (tag, templateBytes) in templates)
        {
            using var template = BinarizeFullImage(templateBytes);
            if (template.Width > sourceCrop.Width || template.Height > sourceCrop.Height)
            {
                LogTemplateTooLarge(tag, template.Width, template.Height, sourceCrop.Width, sourceCrop.Height);
                continue;
            }

            using var result = new Mat();
            Cv2.MatchTemplate(sourceCrop, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out _);

            LogScore(tag, maxVal);
            if (best is null || maxVal > best.Score)
            {
                best = new TagMatch(tag, maxVal);
            }
        }

        if (best is null || best.Score < _matchThreshold)
        {
            LogNoMatch(best?.Tag, best?.Score, _matchThreshold);
            return null;
        }

        return best;
    }

    public byte[] DebugBinarizeClassRegion(byte[] screenshot)
    {
        using var binary = DecodeAndCropAndBinarize(screenshot);
        Cv2.ImEncode(".png", binary, out var bytes);
        return bytes;
    }

    private Mat DecodeAndCropAndBinarize(byte[] screenshot)
    {
        using var full = Cv2.ImDecode(screenshot, ImreadModes.Color);
        if (full.Empty())
        {
            throw new InvalidOperationException("Failed to decode screenshot bytes.");
        }

        var clamped = ClampToImage(_region, full.Size());
        using var crop = new Mat(full, clamped);
        return Binarize(crop);
    }

    private Mat BinarizeFullImage(byte[] templateBytes)
    {
        using var template = Cv2.ImDecode(templateBytes, ImreadModes.Color);
        if (template.Empty())
        {
            throw new InvalidOperationException("Failed to decode template bytes.");
        }
        return Binarize(template);
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

    [LoggerMessage(LogLevel.Information, "ClassMatcher configured: region=({X},{Y} {W}x{H}) luminance={Lum:F0} matchThreshold={Match:F2}")]
    partial void LogConfigured(int x, int y, int w, int h, double lum, double match);

    [LoggerMessage(LogLevel.Debug, "Tag template '{Tag}' score: {Score:F3}")]
    partial void LogScore(string tag, double score);

    [LoggerMessage(LogLevel.Information, "Tag match: best={Tag} score={Score:F3} threshold={Threshold:F3}")]
    partial void LogNoMatch(string? tag, double? score, double threshold);

    [LoggerMessage(LogLevel.Warning, "Tag template '{Tag}' ({TemplateW}x{TemplateH}) is larger than the search region ({SourceW}x{SourceH}); skipping")]
    partial void LogTemplateTooLarge(string tag, int templateW, int templateH, int sourceW, int sourceH);

    [LoggerMessage(LogLevel.Debug, "Binarised stats-class region is nearly empty ({Bright}/{Total} bright pixels) — stats window probably not open")]
    partial void LogEmptyRegion(int bright, int total);
}
