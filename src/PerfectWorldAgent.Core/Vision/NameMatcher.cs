using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PerfectWorldAgent.Vision;

// OpenCV-based template matcher for the HUD character name. Workflow:
//   1. Crop the screenshot to a fixed HUD region (SearchRegion).
//   2. Convert the crop to grayscale and binarize — keeps just the bright text pixels and
//      drops the variable background (sky / minimap / wall / etc.). TM_CCOEFF_NORMED is
//      brightness-invariant but binarisation gets us most of the way to background-
//      invariance too, with the bonus of being trivially debuggable.
//   3. For each (name, template) pair: binarize the template the same way, run
//      Cv2.MatchTemplate with TM_CCOEFF_NORMED, take the max score.
//   4. Return the highest-scoring name if it crosses the threshold.
//
// Both regions are fixed pixel rects tuned for 3839×2129 captures. Other resolutions
// need either anchor-based detection (find the HP bar, offset down) or a scale-aware
// mapping — TODO once we have screenshots at other resolutions.
[SupportedOSPlatform("windows")]
public sealed partial class NameMatcher : INameMatcher
{
    // Hand-tuned for 3840×2160 captures (standard 4K UHD) of the elementclient at the
    // default in-game UI scale. Captures at different UI scales or non-standard
    // resolutions will land their HUD elsewhere — the binarised search region comes back
    // empty in that case, the matcher returns null with an EmptyRegion warning, and the
    // agent stays unidentified until labeled or until an anchor-based detector is
    // implemented (TODO).
    //
    // TemplateRegion — the tight crop saved into the roster when labeling. Just covers
    // the text body, excludes the rage-bar underline (bottom edge) and the rage crystal
    // pip (right edge), and starts a few px below the HP bar so the bar's antialiased
    // bottom doesn't binarise as noise.
    //
    // SearchRegion — what the matcher binarises when looking for a match. Strictly larger
    // than TemplateRegion (must be: Cv2.MatchTemplate requires template ≤ source) on three
    // sides only (top/left/right by 4px). Bottom edge is the SAME as the template's — the
    // rage-bar fill sits just below this line, and at 100/100 it lights up as a bright
    // horizontal stripe that would otherwise pollute the binarised search image with a
    // feature that's absent from cross-screenshot samples with a different fill state.
    //
    // The HUD text position is fixed by the game's UI layout; cosmetic-frame variations
    // change the background under the text, not its position, so 4px slack is plenty.
    private static readonly Rect TemplateRegion = new(273, 210, 225, 26);
    private static readonly Rect SearchRegion = new(269, 206, 233, 34);

    // Luminance threshold for binarisation. The HUD name is rendered in near-white pixels
    // against a dark outline; 240/255 leaves only the text core without picking up bright
    // background patches (cyan friend-list bar, light sky, rage-bar fill, cosmetic-frame
    // edges, etc.) that drift the score on cross-screenshot matching.
    private const double LuminanceThreshold = 240.0;

    private readonly double _matchThreshold;
    private readonly ILogger<NameMatcher> _logger;

    public NameMatcher(ILogger<NameMatcher> logger, double matchThreshold = 0.6)
    {
        _matchThreshold = matchThreshold;
        _logger = logger;
    }

    public NameMatch? Match(byte[] screenshot, IReadOnlyDictionary<string, byte[]> templates)
    {
        if (templates.Count == 0)
        {
            return null;
        }

        using var sourceCrop = DecodeAndCropAndBinarize(screenshot);

        // If the binarised search region has almost no bright pixels, the screenshot
        // probably has the HUD in a different position (different UI scale, different
        // resolution, character-select screen, etc.). MatchTemplate would still return
        // 0.000 for every template but the diagnostic is more useful than a low score.
        var brightPixels = Cv2.CountNonZero(sourceCrop);
        var totalPixels = sourceCrop.Width * sourceCrop.Height;
        if (brightPixels < totalPixels * 0.01)
        {
            LogEmptyRegion(brightPixels, totalPixels);
            return null;
        }

        NameMatch? best = null;
        foreach (var (name, templateBytes) in templates)
        {
            using var template = BinarizeFullImage(templateBytes);
            if (template.Width > sourceCrop.Width || template.Height > sourceCrop.Height)
            {
                LogTemplateTooLarge(name, template.Width, template.Height, sourceCrop.Width, sourceCrop.Height);
                continue;
            }

            using var result = new Mat();
            Cv2.MatchTemplate(sourceCrop, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out _);

            LogScore(name, maxVal);
            if (best is null || maxVal > best.Score)
            {
                best = new NameMatch(name, maxVal);
            }
        }

        if (best is null || best.Score < _matchThreshold)
        {
            LogNoMatch(best?.Name, best?.Score, _matchThreshold);
            return null;
        }

        return best;
    }

    public byte[] CropNameRegion(byte[] screenshot)
    {
        using var full = Cv2.ImDecode(screenshot, ImreadModes.Color);
        if (full.Empty())
        {
            throw new InvalidOperationException("Failed to decode screenshot bytes.");
        }

        var clamped = ClampToImage(TemplateRegion, full.Size());
        using var crop = new Mat(full, clamped);
        Cv2.ImEncode(".png", crop, out var bytes);
        return bytes;
    }

    // Diagnostic — exposes the binarised view of either a screenshot's name region or a
    // raw template. Used by VisionSampleRunner to visualise what MatchTemplate actually
    // sees so tuning Threshold/SearchRegion is data-driven instead of guesswork.
    public byte[] DebugBinarizeFullImage(byte[] imageBytes)
    {
        using var binary = BinarizeFullImage(imageBytes);
        Cv2.ImEncode(".png", binary, out var bytes);
        return bytes;
    }

    public byte[] DebugBinarizeNameRegion(byte[] screenshot)
    {
        using var binary = DecodeAndCropAndBinarize(screenshot);
        Cv2.ImEncode(".png", binary, out var bytes);
        return bytes;
    }

    public (int Bright, int Total) GetNameplatePixelStats(byte[] screenshot)
    {
        using var binary = DecodeAndCropAndBinarize(screenshot);
        return (Cv2.CountNonZero(binary), binary.Width * binary.Height);
    }

    private static Mat DecodeAndCropAndBinarize(byte[] screenshot)
    {
        using var full = Cv2.ImDecode(screenshot, ImreadModes.Color);
        if (full.Empty())
        {
            throw new InvalidOperationException("Failed to decode screenshot bytes.");
        }

        var clamped = ClampToImage(SearchRegion, full.Size());
        using var crop = new Mat(full, clamped);
        return Binarize(crop);
    }

    private static Mat BinarizeFullImage(byte[] templateBytes)
    {
        using var template = Cv2.ImDecode(templateBytes, ImreadModes.Color);
        if (template.Empty())
        {
            throw new InvalidOperationException("Failed to decode template bytes.");
        }
        return Binarize(template);
    }

    private static Mat Binarize(Mat bgr)
    {
        var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var binary = new Mat();
        Cv2.Threshold(gray, binary, LuminanceThreshold, 255, ThresholdTypes.Binary);
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

    #region Logging

    [LoggerMessage(LogLevel.Debug, "Template '{Name}' score: {Score:F3}")]
    partial void LogScore(string name, double score);

    [LoggerMessage(LogLevel.Debug, "No template matched; best='{Name}' score={Score:F3} threshold={Threshold:F3}")]
    partial void LogNoMatch(string? name, double? score, double threshold);

    [LoggerMessage(LogLevel.Warning, "Template '{Name}' ({TemplateW}x{TemplateH}) is larger than the search region ({SourceW}x{SourceH}); skipping")]
    partial void LogTemplateTooLarge(string name, int templateW, int templateH, int sourceW, int sourceH);

    // Debug-level because this fires on every polling tick while a character sits on the
    // server-/character-select screen (no nameplate rendered yet) or when PrintWindow
    // returns a black frame for a background/minimised client — entirely normal transient
    // states. Promote back to Warning if you need to diagnose "why isn't auto-id working".
    [LoggerMessage(LogLevel.Debug, "Binarised search region is nearly empty ({Bright}/{Total} bright pixels) — no nameplate to match against")]
    partial void LogEmptyRegion(int bright, int total);

    #endregion
}
