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

// Чтение координат на Tesseract. Конвейер:
//   1. Обрезать по фиксированной области координат на HUD (правый верхний угол, подогнано под
//      3840×2160).
//   2. Увеличить втрое с кубической интерполяцией — LSTM у Tesseract лучше всего работает по
//      тексту высотой 40–50 px, а цифры PW в родном 4K всего около 25 px.
//   3. Полутона + порог яркости — убирает голубое название локации и тусклый фон, оставляет
//      яркий жёлтый текст цифр.
//   4. Инвертировать в «чёрное по белому» (на этом обучалась LSTM).
//   5. OCR с PageSegMode.SingleLine.
//   6. Вытащить регуляркой «XXX, ZZZ · YY» (запятая плюс необязательный разделитель) →
//      Coordinates(X,Z,Y).
//
// О точности: универсальная LSTM-модель Tesseract стабильно ошибается на самодельном шрифте
// цифр PW (заметнее всего 5→6 и 1→7 в некоторых сочетаниях). Ошибки СОХРАНЯЮТ ПОЗИЦИЮ глифа,
// поэтому логика на разностях (обнаружение застревания, расстояние от ведущего до ведомого)
// по-прежнему работает верно — при вычитании смещение взаимно гасится. Если точные значения
// станут важны, правильное решение — обучить модель Tesseract на шрифте PW либо перейти на
// сопоставление шаблонов цифр по вырезанным эталонным глифам. И то и другое — отдельный
// фронт работ.
//
// TesseractEngine, по его же документации, не потокобезопасен, поэтому Process мы берём под
// блокировку. При девяти агентах с опросом раз в ~2 с и ~50 мс на Read конкуренция за неё
// пренебрежимо мала.
[SupportedOSPlatform("windows")]
public sealed partial class TesseractCoordinateReader : ICoordinateReader, IDisposable
{
    // Подогнано руками под захваты 3840×2160 (обычный 4K UHD) при масштабе игрового интерфейса
    // по умолчанию. Достаточно широкая, чтобы вместить название локации любой длины (координаты
    // прижаты вправо к краю HUD, и длинное название сдвигает их влево) плюс компоненту высоты.
    // Замечание: название локации (голубым) живёт в этой же области — но оно куда тусклее текста
    // цифр, так что порог яркости ниже его отфильтровывает.
    private static readonly Rect CoordRegion = new(3460, 12, 380, 48);

    // Порог яркости для бинаризации. Текст цифр PW отрисован почти белым с насыщенным жёлтым
    // отливом; в полутонах это примерно Y=200 и выше. Цифры компоненты высоты помельче и чуть
    // тусклее, поэтому порог держим осторожным (180), чтобы поймать и их. Голубое название
    // локации в той же полосе ближе к Y=150 и выпадает — ценой некоторой ряби по краям букв, с
    // которой Tesseract прекрасно справляется.
    private const double LuminanceThreshold = 180.0;

    // Ищем ровно тройку координат «XXX, YYY · ZZ» — три целых числа, разделённых запятой и либо
    // маркером (·), либо точкой (.), либо средней точкой (•). Tesseract может исковеркать символ
    // маркера, поэтому принимаем несколько вариантов. От вольного подхода «найти любые три целых
    // числа» отказались: иконка солнца при бинаризации даёт артефакт, который иногда читается как
    // лишняя цифра и загрязняет наивное извлечение чисел.
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

    // Диагностика — возвращает бинаризованный вид, который Tesseract на самом деле и видит.
    // Нужна VisionSampleRunner, чтобы глазами оценить конвейер предобработки.
    public byte[] DebugBinarize(byte[] screenshot) => Preprocess(screenshot);

    // Диагностика — возвращает сырую обрезку области координат (в цвете). Позволяет убедиться,
    // что область стоит на своём месте, ещё до всякой возни с HSV и порогами.
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

        // Увеличиваем втрое до бинаризации. В родном 4K глифы цифр высотой около 25 px — сильно
        // ниже комфортной для Tesseract зоны (40–50 px). Бикубика сохраняет форму штриха лучше,
        // чем ближайший сосед, когда текст сглажен.
        using var upscaled = new Mat();
        Cv2.Resize(crop, upscaled, new Size(0, 0), 3.0, 3.0, InterpolationFlags.Cubic);

        using var gray = new Mat();
        Cv2.CvtColor(upscaled, gray, ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, LuminanceThreshold, 255, ThresholdTypes.Binary);

        // Tesseract обучен на чёрном тексте по белому фону — инвертируем, чтобы цифры стали
        // тёмными, а всё вокруг светлым.
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
