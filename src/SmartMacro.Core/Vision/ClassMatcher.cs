using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using SmartMacro.Native;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SmartMacro.Vision;

// Сопоставитель шаблонов на OpenCV для текста значения класса в игровом окне характеристик.
// Ключи — свободные строки тегов (основы имён файлов шаблонов), настройки — через
// ClassMatcherOptions (область + пороги в appsettings.json):
//   1. Обрезать скриншот по настроенной области со значением класса в окне характеристик.
//   2. Перевести в полутона и бинаризовать по LuminanceThreshold, чтобы остался только текст.
//   3. Для каждой пары (тег, шаблон): бинаризовать шаблон так же, запустить Cv2.MatchTemplate с
//      TM_CCOEFF_NORMED, взять максимальную оценку.
//   4. Вернуть тег с наибольшей оценкой, если она переваливает за MatchThreshold.
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

    public TagMatch? Match(byte[] screenshot, IReadOnlyDictionary<string, byte[]> templates, ScreenRect region)
    {
        if (templates.Count == 0)
        {
            return null;
        }

        using var sourceCrop = DecodeAndCropAndBinarize(screenshot, region);

        var brightPixels = Cv2.CountNonZero(sourceCrop);
        var totalPixels = sourceCrop.Width * sourceCrop.Height;
        if (brightPixels < totalPixels * 0.01)
        {
            // Почти пустая бинаризованная область — обычно значит, что в момент снятия
            // скриншота окно характеристик на самом деле не было открыто либо что настроенная
            // область указывает на пустое место интерфейса. Пишем в лог и отказываемся
            // сопоставлять.
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
        // Диагностический путь продолжает работать по НАСТРОЕННОЙ области — он для того и есть,
        // чтобы оператор мог на глаз оценить, подогнан ли "Vision:ClassMatcher:Region" под его
        // разрешение.
        using var binary = DecodeAndCropAndBinarize(screenshot, default);
        Cv2.ImEncode(".png", binary, out var bytes);
        return bytes;
    }

    // Пустая область откатывается на настроенную, чтобы диагностический путь и любой
    // вызывающий, у которого области ещё нет, вели себя как раньше.
    private Mat DecodeAndCropAndBinarize(byte[] screenshot, ScreenRect region)
    {
        using var full = Cv2.ImDecode(screenshot, ImreadModes.Color);
        if (full.Empty())
        {
            throw new InvalidOperationException("Failed to decode screenshot bytes.");
        }

        var requested = region.Width > 0 && region.Height > 0
            ? new Rect(region.X, region.Y, region.Width, region.Height)
            : _region;
        var clamped = ClampToImage(requested, full.Size());
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

    [LoggerMessage(LogLevel.Information,
        "ClassMatcher настроен: область=({X},{Y} {W}x{H}) яркость={Lum:F0} порог совпадения={Match:F2}")]
    partial void LogConfigured(int x, int y, int w, int h, double lum, double match);

    [LoggerMessage(LogLevel.Debug, "Шаблон тега '{Tag}', оценка: {Score:F3}")]
    partial void LogScore(string tag, double score);

    [LoggerMessage(LogLevel.Information, "Тег не опознан: лучший={Tag} оценка={Score:F3} порог={Threshold:F3}")]
    partial void LogNoMatch(string? tag, double? score, double threshold);

    [LoggerMessage(LogLevel.Warning,
        "Шаблон тега '{Tag}' ({TemplateW}x{TemplateH}) больше области поиска ({SourceW}x{SourceH}); пропускаем")]
    partial void LogTemplateTooLarge(string tag, int templateW, int templateH, int sourceW, int sourceH);

    [LoggerMessage(LogLevel.Debug,
        "Бинаризованная область класса почти пуста (ярких пикселей {Bright}/{Total}) — окно характеристик, скорее всего, не открыто")]
    partial void LogEmptyRegion(int bright, int total);
}
