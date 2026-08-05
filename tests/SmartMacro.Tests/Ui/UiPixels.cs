using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Снимок настоящего кадра Skia и чтение его пикселей.
///
/// <b>Кадр здесь настоящий.</b> <see cref="UiTestApp"/> собирает платформу с
/// <c>UseHeadlessDrawing = false</c> и <c>UseSkia()</c>, поэтому
/// <see cref="HeadlessWindowExtensions.CaptureRenderedFrame"/> отдаёт то, что нарисовал бы
/// настоящий рисовальщик: сглаживание, подбор шрифта, растеризация глифов. Ради этого шага всё и
/// затевалось — обе ловушки, которые он ловит (срезанные хвосты букв и цветной эмодзи вместо
/// подписи), в дереве раскладки не видны вовсе: там размеры верные, а неверны пиксели.
///
/// Снятие кадра детерминировано: <c>CaptureRenderedFrame</c> сам проворачивает тик таймера
/// отрисовки и возвращает уже готовый битмап — ждать нечего и нечем.
/// </summary>
internal sealed class UiPixels : IDisposable
{
    private readonly byte[] _bytes;
    private readonly int _stride;

    private UiPixels(byte[] bytes, int stride, PixelSize size)
    {
        _bytes = bytes;
        _stride = stride;
        Size = size;
    }

    public PixelSize Size { get; }

    /// <summary>Снимает кадр окна.</summary>
    public static UiPixels Capture(TopLevel window)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException(
                "Кадр пуст. Так бывает, когда платформа собрана с UseHeadlessDrawing = true — " +
                "тогда рисовальщик заменён пустышкой и мерить нечего.");

        return From(frame);
    }

    private static UiPixels From(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var height = buffer.Size.Height;
        var stride = buffer.RowBytes;
        var bytes = new byte[stride * height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

        var pixels = new UiPixels(bytes, stride, buffer.Size);
        if (buffer.Format == PixelFormat.Bgra8888)
        {
            return pixels;
        }

        return buffer.Format == PixelFormat.Rgba8888
            ? pixels.SwapRedAndBlue()
            : throw new InvalidOperationException($"Неожиданный формат кадра: {buffer.Format}.");
    }

    /// <summary>Цвет пикселя; за пределами кадра — прозрачный чёрный.</summary>
    public Color At(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Size.Width || y >= Size.Height)
        {
            return default;
        }

        var offset = (y * _stride) + (x * 4);
        return Color.FromArgb(_bytes[offset + 3], _bytes[offset + 2], _bytes[offset + 1], _bytes[offset]);
    }

    /// <summary>
    /// Самый частый цвет прямоугольника — фон под текстом. Считается, а не берётся из кисти:
    /// под контролом может лежать что угодно, включая заливку его собственной темы.
    /// </summary>
    public Color BackgroundOf(PixelRect area)
    {
        var counts = new Dictionary<uint, int>();
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                var key = At(x, y).ToUInt32();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return counts.Count == 0 ? default : Color.FromUInt32(counts.MaxBy(pair => pair.Value).Key);
    }

    /// <summary>
    /// Строки пикселей, где есть чернила, — то есть где хоть один пиксель заметно отличается от
    /// фона. Именно этим измерением и нашли срезанные хвосты «р», «у», «д».
    /// </summary>
    public IReadOnlyList<int> InkRows(PixelRect area, Color background, int tolerance = 24)
    {
        var rows = new List<int>();
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                if (Differs(At(x, y), background, tolerance))
                {
                    rows.Add(y);
                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>Все пиксели чернил прямоугольника — для проверки цвета глифа.</summary>
    public IReadOnlyList<Color> InkPixels(PixelRect area, Color background, int tolerance = 24)
    {
        var found = new List<Color>();
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                var pixel = At(x, y);
                if (Differs(pixel, background, tolerance))
                {
                    found.Add(pixel);
                }
            }
        }

        return found;
    }

    /// <summary>Прямоугольник в координатах окна → прямоугольник кадра, урезанный по кадру.</summary>
    public PixelRect Clamp(Rect rect)
    {
        var left = Math.Clamp((int)Math.Floor(rect.X), 0, Size.Width);
        var top = Math.Clamp((int)Math.Floor(rect.Y), 0, Size.Height);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right), 0, Size.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, Size.Height);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public void Dispose()
    {
    }

    private static bool Differs(Color pixel, Color background, int tolerance) =>
        Math.Abs(pixel.R - background.R) > tolerance ||
        Math.Abs(pixel.G - background.G) > tolerance ||
        Math.Abs(pixel.B - background.B) > tolerance;

    private UiPixels SwapRedAndBlue()
    {
        for (var i = 0; i + 3 < _bytes.Length; i += 4)
        {
            (_bytes[i], _bytes[i + 2]) = (_bytes[i + 2], _bytes[i]);
        }

        return this;
    }
}
