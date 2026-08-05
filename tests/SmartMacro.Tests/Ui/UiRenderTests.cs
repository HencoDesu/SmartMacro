using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartMacro.App.ViewModels;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Обход по НАСТОЯЩЕМУ кадру: две ловушки, которых нет в дереве раскладки, потому что там всё
/// верно, а неверны пиксели.
///
/// <b>Отношение к статическим тестам по исходникам — не дубль, а земля под ними.</b>
/// <c>Sources/AxamlFieldHeightTests</c> считает арифметику по разметке («высота минус рамка
/// минус отступ против кегля, умноженного на 1.27») и потому обходит ВСЮ разметку дёшево, но
/// коэффициент 1.27 в нём — модель, о щедрости которой там же и написано.
/// <c>Sources/AxamlGlyphTests</c> сверяет кодовые точки с таблицей эмодзи — тоже модель, только
/// табличная. Здесь меряется то, что модели предсказывают: строки чернил в растре и цвет
/// пикселей глифа. Если модель когда-нибудь разойдётся с растром, покраснеет этот файл — и
/// разбираться надо будет в константе там, а не в числе здесь.
///
/// Обе ловушки нашли ровно так: хвосты букв — «измерением строк пикселей относительно эталонного
/// TextBlock», цветной глиф — взглядом на работающую панель.
/// </summary>
public class UiRenderTests
{
    /// <summary>Слово без подстрочных элементов и оно же с ними. Разница — то, что срезает ловушка.</summary>
    private const string WithoutDescenders = "combo";

    private const string WithDescenders = "друг";

    [Test]
    public async Task EveryFixedHeightFieldStillDrawsTheTailsOfItsLetters()
    {
        var problems = await Ui.RunAsync<IReadOnlyList<string>>(() =>
        {
            var shapes = CollectFieldShapes();
            return shapes.Count == 0
                ? ["  в панели не нашлось ни одного поля с жёсткой высотой — обход ослеп"]
                : MeasureTails(shapes);
        });

        Report(
            problems,
            "У поля с жёсткой высотой срезаны подстрочные элементы букв." + NL + NL +
            "Проверка сама себе эталон: слово «" + WithoutDescenders + "» и слово «" + WithDescenders +
            "» рисуются в ОДИНАКОВЫЕ поля, и нижняя строка чернил у второго обязана быть ниже. " +
            "Совпали — значит хвосты «р», «у», «д» обрезаны ровно по базовой линии; заметить это " +
            "на глаз почти нельзя, буквы остаются целыми и читаемыми." + NL + NL +
            "Механизм описан у темы (Themes/Controls.axaml, сеттер Padding): отступ поля 11.2 px " +
            "по вертикали, а текст лежит внутри PART_ScrollViewer, который обрезает. Пока высоту " +
            "держит MinHeight, контрол растёт под строку; жёсткий Height это отключает." + NL + NL +
            "Лекарство на месте вызова: рядом с Height задать Padding=\"8,0\" и " +
            "VerticalContentAlignment=\"Center\".");
    }

    /// <summary>
    /// Размеры и шрифт КАЖДОГО поля панели с жёсткой высотой — уже разрешённые темой и стилями.
    ///
    /// Берутся с живых контролов, а не из разметки: именно этим замер отличается от
    /// <c>Sources/AxamlFieldHeightTests</c>, который вынужден разбирать селекторы стилей руками и
    /// пропускать всё, что приезжает привязкой.
    /// </summary>
    private static IReadOnlyList<FieldShape> CollectFieldShapes()
    {
        var scene = UiScene.Shared(1520, 840);
        var shapes = new Dictionary<FieldShape, string>();

        foreach (var mode in Enum.GetValues<ShellMode>())
        {
            scene.Select(mode);

            foreach (var field in UiTree.VisibleOfType<TextBox>(scene.Window))
            {
                if (double.IsNaN(field.Height))
                {
                    // Высоту держит MinHeight — контрол растёт под строку, ловушки нет.
                    continue;
                }

                var shape = new FieldShape(
                    field.Height,
                    field.Padding,
                    field.BorderThickness,
                    field.FontSize,
                    field.FontFamily,
                    field.FontWeight,
                    field.VerticalContentAlignment);

                shapes.TryAdd(shape, $"{mode}: {UiTree.PathOf(field)}");
            }
        }

        return [.. shapes.Keys.Select(shape => shape with { Where = shapes[shape] })];
    }

    /// <summary>
    /// Рисует по паре одинаковых полей на каждую форму — со словом без хвостов и со словом с
    /// хвостами — и сравнивает нижние строки чернил.
    ///
    /// <b>ОДИН кадр на всё, и это не про скорость.</b> Первая редакция вписывала слова в живые
    /// поля панели и снимала два кадра подряд; тест проходил в одиночку и падал в общем прогоне,
    /// потому что между «поменяли текст» и «сняли кадр» стоит конвейер отрисовки, и второй кадр
    /// иногда оказывался первым. Здесь сравниваемые поля живут в одном окне и попадают в один
    /// растр, так что сравнивать нечего с чем несинхронно.
    /// </summary>
    private static IReadOnlyList<string> MeasureTails(IReadOnlyList<FieldShape> shapes)
    {
        var rows = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
        var pairs = new List<(FieldShape Shape, TextBox Flat, TextBox Tails)>();

        foreach (var shape in shapes)
        {
            var flat = shape.Build(WithoutDescenders);
            var tails = shape.Build(WithDescenders);
            pairs.Add((shape, flat, tails));
            rows.Children.Add(new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Children = { flat, tails },
            });
        }

        var window = new Window
        {
            Width = 420,
            Height = (shapes.Count * 40) + 40,
            Content = rows,
        };

        // Серое сглаживание — по тому же доводу, что и в UiScene.
        RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);

        var found = new List<string>();
        try
        {
            window.Show();
            UiScene.Settle();
            using var frame = UiPixels.Capture(window);

            foreach (var (shape, flat, tails) in pairs)
            {
                var flatBottom = InkBottom(frame, flat, window);
                var tailBottom = InkBottom(frame, tails, window);

                if (flatBottom is null || tailBottom is null)
                {
                    found.Add($"  чернил не нашлось вовсе — {shape.Where}");
                    continue;
                }

                if (tailBottom <= flatBottom)
                {
                    found.Add(string.Create(CultureInfo.InvariantCulture,
                        $"  Height={shape.Height:F0}, отступ {shape.Padding}, кегль {shape.FontSize:F1}: " +
                        $"«{WithoutDescenders}» кончается на строке {flatBottom}, «{WithDescenders}» — " +
                        $"на {tailBottom} — {shape.Where}"));
                }
            }
        }
        finally
        {
            window.Close();
            UiScene.Settle();
        }

        return found;
    }

    private static int? InkBottom(UiPixels frame, Control field, Window window)
    {
        // Внутренность без рамки и её сглаживания: рамка идёт по всему периметру и сама по себе
        // была бы чернилами в каждой строке.
        var area = frame.Clamp(UiTree.BoundsIn(field, window).Deflate(2));
        var rows = frame.InkRows(area, frame.BackgroundOf(area));
        return rows.Count == 0 ? null : rows[^1];
    }

    /// <summary>Форма поля: всё, от чего зависит, влезет ли строка. <see cref="Where"/> в сравнении не участвует.</summary>
    private readonly record struct FieldShape(
        double Height,
        Thickness Padding,
        Thickness BorderThickness,
        double FontSize,
        FontFamily Font,
        FontWeight Weight,
        Avalonia.Layout.VerticalAlignment ContentAlignment)
    {
        public string Where { get; init; } = string.Empty;

        public bool Equals(FieldShape other) =>
            Height.Equals(other.Height) &&
            Padding.Equals(other.Padding) &&
            BorderThickness.Equals(other.BorderThickness) &&
            FontSize.Equals(other.FontSize) &&
            Equals(Font, other.Font) &&
            Weight == other.Weight &&
            ContentAlignment == other.ContentAlignment;

        public override int GetHashCode() =>
            HashCode.Combine(Height, Padding, BorderThickness, FontSize, Font, Weight, ContentAlignment);

        public TextBox Build(string text) => new()
        {
            Text = text,
            Width = 160,
            Height = Height,
            Padding = Padding,
            BorderThickness = BorderThickness,
            FontSize = FontSize,
            FontFamily = Font,
            FontWeight = Weight,
            VerticalContentAlignment = ContentAlignment,
        };
    }

    [Test]
    public async Task EveryGlyphInTheMarkupObeysItsForeground()
    {
        var problems = await Ui.RunAsync<IReadOnlyList<string>>(() =>
        {
            using var scene = UiScene.Create();
            var found = new List<string>();

            foreach (var mode in Enum.GetValues<ShellMode>())
            {
                scene.Select(mode);
                using var frame = UiPixels.Capture(scene.Window);

                foreach (var piece in UiTree.VisibleText(scene.Window))
                {
                    if (!IsLoneSymbol(piece.Text) || piece.Ink is not { Width: > 0, Height: > 0 })
                    {
                        continue;
                    }

                    if (piece.Control.GetValue(TextBlock.ForegroundProperty) is not ISolidColorBrush brush)
                    {
                        continue;
                    }

                    var area = frame.Clamp(piece.Bounds);
                    var background = frame.BackgroundOf(area);
                    var ink = frame.InkPixels(area, background);
                    if (ink.Count == 0)
                    {
                        continue;
                    }

                    var stray = ink
                        .Select(pixel => (Pixel: pixel, Off: OffTheBlend(pixel, background, brush.Color)))
                        .Where(candidate => candidate.Off > BlendTolerance)
                        .OrderByDescending(candidate => candidate.Off)
                        .ToArray();

                    // Один-два пикселя мимо — это сглаживание на границе с чужой заливкой.
                    // Цветной эмодзи промахивается СОТНЯМИ и на порядок дальше.
                    if (stray.Length > ink.Count / 4)
                    {
                        found.Add(string.Create(CultureInfo.InvariantCulture,
                            $"  {mode}: «{piece.Text}» (U+{char.ConvertToUtf32(piece.Text, 0):X4}) — " +
                            $"Foreground {brush.Color}, фон {background}, но {stray.Length} из " +
                            $"{ink.Count} пикселей мимо смеси, худший {stray[0].Pixel} на " +
                            $"{stray[0].Off:F0} — {piece.Where}"));
                    }
                }
            }

            return found;
        });

        Report(
            problems,
            "Глиф нарисован НЕ тем цветом, который ему задали." + NL + NL +
            "У одноцветного глифа каждый пиксель — смесь фона и Foreground: сглаживание даёт " +
            "промежуточные значения, но никогда третий оттенок. Пиксели вне этого отрезка " +
            "означают, что символ подан из Segoe UI Emoji, чьи глифы полноцветные и Foreground " +
            "игнорируют начисто. Цепочка шрифтов не спасает — подмена происходит ниже неё." + NL + NL +
            "На эту ловушку наступали трижды: D1 — U+25B6 ▶, D4 — U+26A0 ⚠, D5 — U+23F8 ⏸. " +
            "Лечится СМЕНОЙ КОДОВОЙ ТОЧКИ, а не шрифтом; список проверенных — в " +
            "Sources/AxamlGlyphTests.VerifiedTextGlyphs.");
    }

    // ---- служебное ---------------------------------------------------------------------------

    /// <summary>
    /// Насколько далеко цвет от отрезка «фон → Foreground» в RGB. У одноцветного глифа — почти
    /// ноль на любом уровне сглаживания.
    /// </summary>
    private const double BlendTolerance = 48;

    private static string NL => Environment.NewLine;

    /// <summary>Текст — ровно один символ вне ASCII, то есть значок, а не подпись.</summary>
    private static bool IsLoneSymbol(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var runes = trimmed.EnumerateRunes();
        if (!runes.MoveNext())
        {
            return false;
        }

        var first = runes.Current;
        return !runes.MoveNext() && first.Value >= 0x80;
    }

    private static double OffTheBlend(Color pixel, Color background, Color foreground)
    {
        double dr = foreground.R - background.R;
        double dg = foreground.G - background.G;
        double db = foreground.B - background.B;
        var lengthSquared = (dr * dr) + (dg * dg) + (db * db);

        double pr = pixel.R - background.R;
        double pg = pixel.G - background.G;
        double pb = pixel.B - background.B;

        var t = lengthSquared <= 0
            ? 0
            : Math.Clamp(((pr * dr) + (pg * dg) + (pb * db)) / lengthSquared, 0, 1);

        var ex = pr - (t * dr);
        var ey = pg - (t * dg);
        var ez = pb - (t * db);
        return Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez));
    }

    private static void Report(IReadOnlyList<string> problems, string explanation)
    {
        if (problems.Count > 0)
        {
            var report = new StringBuilder(explanation).Append(NL).Append(NL).Append("Найдено:");
            foreach (var problem in problems)
            {
                report.Append(NL).Append(problem);
            }

            Assert.Fail(report.ToString());
        }
    }
}
