using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace SmartMacro.IconForge;

/// <summary>
/// Растеризует знак SmartMacro из <c>assets/logo/*.svg</c> в то, что нужно Windows: набор ICO для
/// иконки исполняемых файлов и трея, и PNG для иконки окна Avalonia.
///
/// <b>Источник один — SVG.</b> Геометрия здесь не повторяется: пути читаются из файла и уходят в
/// <see cref="SKPath.ParseSvgPathData"/> как есть. Разъехаться исходнику и растру поэтому негде.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Размеры кадров в ICO. 256 нужен проводнику и Alt+Tab, 16 — трею и заголовку окна;
    /// промежуточные Windows выбирает сама под масштаб экрана, и без них она масштабирует
    /// ближайший, что на 24 и 40 px даёт кашу.
    /// </summary>
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>
    /// Граница между упрощённым знаком и полным. Ровно то, что сказано в макете: на 32 px и ниже
    /// хвост стрелки убран, штрих поднят до 3.
    /// </summary>
    private const int SmallUpTo = 32;

    private static int Main()
    {
        var root = FindRepositoryRoot();
        var logo = Path.Combine(root, "assets", "logo");

        // Имена — как в макете; файлы лежат ДОСЛОВНО такими, какими их отдал Claude Design,
        // чтобы повторный импорт читался диффом, а не разбирательством.
        var full = SvgMark.Load(Path.Combine(logo, "icon.svg"));
        var small = SvgMark.Load(Path.Combine(logo, "icon-small.svg"));

        SKBitmap Render(int size) => (size <= SmallUpTo ? small : full).Render(size);

        // ICO — один файл на оба исполняемых и на трей; см. комментарии в csproj обоих проектов.
        var icoPath = Path.Combine(logo, "icon.ico");
        using (var ico = File.Create(icoPath))
        {
            IcoWriter.Write(ico, IconSizes.Select(size => (size, Render(size))).ToList());
        }

        Console.WriteLine($"{icoPath} — кадров {IconSizes.Length}, {new FileInfo(icoPath).Length} байт");

        // PNG для окна панели: Avalonia берёт его как AvaloniaResource и масштабирует сама.
        var pngPath = Path.Combine(logo, "icon-256.png");
        using (var bitmap = Render(256))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var png = File.Create(pngPath))
        {
            data.SaveTo(png);
        }

        Console.WriteLine($"{pngPath} — 256x256, {new FileInfo(pngPath).Length} байт");

        // Лист для разглядывания глазами: знак на светлом и тёмном фоне во всех размерах. Ловит
        // то, чего не видно в отдельном PNG на прозрачном, — контраст штриха с реальной панелью.
        var previewPath = Path.Combine(logo, "preview.png");
        using (var preview = Preview.Build(IconSizes, Render))
        using (var image = SKImage.FromBitmap(preview))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(previewPath))
        {
            data.SaveTo(file);
        }

        Console.WriteLine($"{previewPath} — лист для проверки глазами");
        return 0;
    }

    /// <summary>Идёт вверх до папки с <c>SmartMacro.slnx</c> — тот же приём, что в тестах.</summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartMacro.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория (SmartMacro.slnx).");
    }
}

/// <summary>
/// Знак, прочитанный из SVG. Разбирается намеренно узкое подмножество формата — только элементы
/// <c>path</c> с плоскими атрибутами: ровно то, из чего состоят оба файла знака. Полноценный
/// разбор SVG здесь был бы библиотекой, которую никто не просил.
/// </summary>
internal sealed class SvgMark
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private readonly List<(SKPath Path, SKPaint Paint)> _shapes = [];
    private readonly float _viewBox;

    private SvgMark(float viewBox) => _viewBox = viewBox;

    public static SvgMark Load(string path)
    {
        var doc = XDocument.Load(path);
        var svg = doc.Root ?? throw new InvalidOperationException($"«{path}» пуст.");
        var viewBox = (svg.Attribute("viewBox")?.Value ?? "0 0 32 32").Split(' ');
        var mark = new SvgMark(float.Parse(viewBox[2], CultureInfo.InvariantCulture));

        foreach (var element in svg.Descendants(Svg + "path"))
        {
            var data = element.Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            var geometry = SKPath.ParseSvgPathData(data)
                           ?? throw new InvalidOperationException($"Не разобран путь: {data}");

            var stroke = element.Attribute("stroke")?.Value;
            var fill = element.Attribute("fill")?.Value;

            if (!string.IsNullOrWhiteSpace(stroke) && stroke != "none")
            {
                mark._shapes.Add((geometry, new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    Color = ParseColour(stroke),
                    StrokeWidth = float.Parse(
                        element.Attribute("stroke-width")?.Value ?? "1", CultureInfo.InvariantCulture),
                    StrokeCap = element.Attribute("stroke-linecap")?.Value == "round"
                        ? SKStrokeCap.Round
                        : SKStrokeCap.Butt,
                    StrokeJoin = element.Attribute("stroke-linejoin")?.Value == "round"
                        ? SKStrokeJoin.Round
                        : SKStrokeJoin.Miter,
                }));
            }
            else if (!string.IsNullOrWhiteSpace(fill) && fill != "none")
            {
                mark._shapes.Add((geometry, new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = ParseColour(fill),
                }));
            }
        }

        return mark;
    }

    public SKBitmap Render(int size)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / _viewBox);

        foreach (var (path, paint) in _shapes)
        {
            canvas.DrawPath(path, paint);
        }

        return bitmap;
    }

    private static SKColor ParseColour(string value) =>
        SKColor.TryParse(value, out var colour)
            ? colour
            : throw new InvalidOperationException($"Не разобран цвет: {value}");
}

/// <summary>
/// Пишет ICO вручную. Библиотеки для этого не нашлось такой, что не тянула бы за собой пол-мира.
///
/// ⚠️ <b>Кадры до 256 px — классический DIB, и только 256 — PNG.</b> Соблазн сделать PNG везде
/// велик (короче код, меньше файл), но иконку трея грузит <c>LoadImage</c> с
/// <c>LR_LOADFROMFILE</c>, а он PNG-кадры внутри ICO понимает не на всех сборках Windows —
/// в отличие от <c>LoadIconWithScaleDown</c>. Отказ был бы «в трее пусто», без сообщения.
/// Для 256 выбора нет: DIB такого размера ICO-каталог не описывает разумно, и PNG там — норма
/// с Vista.
/// </summary>
internal static class IcoWriter
{
    public static void Write(Stream output, IReadOnlyList<(int Size, SKBitmap Bitmap)> frames)
    {
        var payloads = frames
            .Select(frame => frame.Size == 256 ? EncodePng(frame.Bitmap) : EncodeDib(frame.Bitmap))
            .ToList();

        using var writer = new BinaryWriter(output);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // тип: 1 = иконка
        writer.Write((ushort)frames.Count);

        // Каталог фиксированной длины идёт целиком перед данными, поэтому смещения считаются
        // заранее: 6 байт заголовка + 16 на запись.
        var offset = 6 + (16 * frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var size = frames[i].Size;
            writer.Write((byte)(size == 256 ? 0 : size));   // 0 означает 256 — так задан формат
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);            // палитры нет
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // плоскостей
            writer.Write((ushort)32);         // бит на пиксель
            writer.Write(payloads[i].Length);
            writer.Write(offset);
            offset += payloads[i].Length;
        }

        foreach (var payload in payloads)
        {
            writer.Write(payload);
        }
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// BITMAPINFOHEADER + BGRA снизу вверх + маска AND.
    ///
    /// Маска заполняется нулями («пиксель непрозрачен»): при 32 битах Windows берёт прозрачность
    /// из альфа-канала, но саму маску формат всё равно требует, и её высота входит в
    /// <c>biHeight</c> — отсюда удвоение, которое иначе выглядит опечаткой.
    /// </summary>
    private static byte[] EncodeDib(SKBitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var maskStride = ((width + 31) / 32) * 4;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(40);                     // biSize
        writer.Write(width);
        writer.Write(height * 2);             // XOR + AND, см. пояснение выше
        writer.Write((ushort)1);              // biPlanes
        writer.Write((ushort)32);             // biBitCount
        writer.Write(0);                      // biCompression = BI_RGB
        writer.Write((width * height * 4) + (maskStride * height));
        writer.Write(0);                      // biXPelsPerMeter
        writer.Write(0);                      // biYPelsPerMeter
        writer.Write(0);                      // biClrUsed
        writer.Write(0);                      // biClrImportant

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                writer.Write(pixel.Blue);
                writer.Write(pixel.Green);
                writer.Write(pixel.Red);
                writer.Write(pixel.Alpha);
            }
        }

        writer.Write(new byte[maskStride * height]);
        writer.Flush();
        return buffer.ToArray();
    }
}

/// <summary>Контрольный лист: все размеры на светлой и тёмной подложке, рядом друг с другом.</summary>
internal static class Preview
{
    /// <summary>
    /// Верхняя половина — светлая подложка, нижняя — тёмная; в каждой сперва малые размеры,
    /// увеличенные до пиксельной сетки (иначе 16 px на экране автора не разглядеть), затем
    /// натуральная величина.
    /// </summary>
    public static SKBitmap Build(IReadOnlyList<int> sizes, Func<int, SKBitmap> render)
    {
        const int Pad = 20;
        const int ZoomTo = 128;

        // Увеличенные — только те, что реально мелкие: остальные и так видны.
        var zoomed = sizes.Where(size => size <= 48).ToList();
        var rowHeight = Math.Max(ZoomTo, sizes.Max()) + (Pad * 2);
        var width = Pad
                    + zoomed.Sum(_ => ZoomTo + Pad)
                    + sizes.Sum(size => size + Pad);

        var bitmap = new SKBitmap(width, rowHeight * 2, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);

        // Те же подложки, на которых знак показан в макете: панель светлой темы и тёмной.
        using var light = new SKPaint { Color = SKColor.Parse("#d9d9de") };
        using var dark = new SKPaint { Color = SKColor.Parse("#1b1b21") };
        canvas.DrawRect(0, 0, width, rowHeight, light);
        canvas.DrawRect(0, rowHeight, width, rowHeight, dark);

        // Ближайший сосед: увеличение обязано показать РЕАЛЬНЫЕ пиксели кадра, а сглаженное
        // растягивание нарисовало бы картинку красивее, чем она есть в трее.
        using var pixels = new SKPaint { FilterQuality = SKFilterQuality.None };

        var x = (float)Pad;
        foreach (var size in zoomed)
        {
            using var frame = render(size);
            foreach (var top in (float[])[0, rowHeight])
            {
                canvas.DrawBitmap(frame, new SKRect(x, top + Pad, x + ZoomTo, top + Pad + ZoomTo), pixels);
            }

            x += ZoomTo + Pad;
        }

        foreach (var size in sizes)
        {
            using var frame = render(size);
            foreach (var top in (float[])[0, rowHeight])
            {
                canvas.DrawBitmap(frame, x, top + ((rowHeight - size) / 2f));
            }

            x += size + Pad;
        }

        return bitmap;
    }
}
