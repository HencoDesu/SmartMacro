using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SmartMacro.Tests.Ui;

/// <summary>Один найденный на экране кусок текста вместе с тем, где он лежит и что просил.</summary>
/// <param name="Control">Сам контрол — нужен, чтобы назвать виноватого в сообщении.</param>
/// <param name="Text">Что напечатано.</param>
/// <param name="Bounds">Прямоугольник КОНТРОЛА в координатах окна.</param>
/// <param name="Ink">
/// Прямоугольник самих БУКВ. Отличается от <paramref name="Bounds"/> у всего, что растянуто:
/// <c>TextBlock</c> по умолчанию тянется на всю ячейку, так что его границы — это границы
/// колонки, а не строки. Проверять соседство по ним значило бы объявить столкновением любые два
/// текста, стоящие в соседних колонках одной сетки.
/// </param>
/// <param name="NaturalWidth">Ширина, которую строка просила бы без ограничений.</param>
/// <param name="MayBeShortened">Разметка САМА разрешила укоротить: есть обрезка или перенос.</param>
internal readonly record struct TextPiece(
    Control Control,
    string Text,
    Rect Bounds,
    Rect Ink,
    double NaturalWidth,
    bool MayBeShortened)
{
    /// <summary>Адрес для сообщения об ошибке: тип, текст и цепочка родителей до вида.</summary>
    public string Where => $"{Control.GetType().Name} «{Shorten(Text)}» в {UiTree.PathOf(Control)}";

    private static string Shorten(string text) =>
        text.Length <= 42 ? text : string.Concat(text.AsSpan(0, 40), "…");
}

/// <summary>
/// Обход визуального дерева живого окна: что на самом деле видно и какого размера.
///
/// <b>«Видно» здесь значит именно видно.</b> Оболочка держит все пять видов живыми и переключает
/// их <c>IsVisible</c> (см. MainWindow.axaml) — невидимая ветка не измеряется вовсе, и её
/// нулевые прямоугольники обвинили бы всё подряд. Поэтому обход обрывается на первом же
/// невидимом узле, а не фильтрует найденное потом.
/// </summary>
internal static class UiTree
{
    /// <summary>Весь видимый текст окна: подписи, поля, содержимое кнопок и шапки.</summary>
    public static IReadOnlyList<TextPiece> VisibleText(Visual root)
    {
        var result = new List<TextPiece>();
        Collect(root, root, result);
        return result;
    }

    /// <summary>Все видимые контролы данного вида — без текста, для геометрических правил.</summary>
    public static IReadOnlyList<T> VisibleOfType<T>(Visual root)
        where T : Visual
    {
        var result = new List<T>();
        CollectOfType(root, result);
        return result;
    }

    /// <summary>Прямоугольник контрола в координатах <paramref name="root"/>.</summary>
    public static Rect BoundsIn(Visual visual, Visual root)
    {
        var origin = visual.TranslatePoint(default, root);
        return origin is { } point
            ? new Rect(point, visual.Bounds.Size)
            : default;
    }

    /// <summary>
    /// То, что от прямоугольника РЕАЛЬНО видно: пересечение со всеми обрезающими предками.
    ///
    /// Без этого правило о соседстве обвиняло бы коробку ноды, уехавшую за край канвы, в наезде
    /// на инспектор — она и правда лежит там по координатам, но её там не видно, потому что
    /// область просмотра канвы обрезает. То же самое у всякого прокрученного списка.
    /// </summary>
    public static Rect ClippedTo(Rect rect, Visual visual, Visual root)
    {
        for (var node = visual; node is not null && node != root; node = node.GetVisualParent())
        {
            if (node.ClipToBounds)
            {
                rect = rect.Intersect(BoundsIn(node, root));
            }
        }

        return rect;
    }

    /// <summary>
    /// Размер, который строка заняла бы, если бы её никто не ограничивал.
    ///
    /// Считается ОТДЕЛЬНЫМ <see cref="TextBlock"/> с теми же шрифтовыми свойствами, а не по
    /// <c>DesiredSize</c> найденного: <c>MeasureCore</c> у Avalonia прижимает желаемый размер к
    /// доступному, так что у зажатого контрола «желаемое» уже равно «полученному», и сравнивать
    /// было бы нечего с чем.
    /// </summary>
    public static Size NaturalSize(Control control)
    {
        var probe = new TextBlock
        {
            Text = TextOf(control),
            FontFamily = control.GetValue(TextBlock.FontFamilyProperty),
            FontSize = control.GetValue(TextBlock.FontSizeProperty),
            FontWeight = control.GetValue(TextBlock.FontWeightProperty),
            FontStyle = control.GetValue(TextBlock.FontStyleProperty),
            FontStretch = control.GetValue(TextBlock.FontStretchProperty),
            LetterSpacing = control is TextBlock block ? block.LetterSpacing : 0,
        };

        probe.Measure(Size.Infinity);

        // Отступ самого контрола к строке не относится, но место занимает: поле в 26px с
        // Padding="8,0" обязано вместить и строку, и эти шестнадцать пикселей.
        var padding = control.GetValue(TemplatedControl.PaddingProperty);
        return new Size(
            probe.DesiredSize.Width + padding.Left + padding.Right,
            probe.DesiredSize.Height + padding.Top + padding.Bottom);
    }

    /// <summary>
    /// Прямоугольник самих букв внутри выделенного контролу места — по его выравниванию.
    ///
    /// Точность здесь не пиксельная и не нужна: правило о соседстве спрашивает «сколько пустого
    /// места между двумя надписями», и ответ «вся ячейка» на этот вопрос неверен на порядок,
    /// а «строка, прижатая к левому краю ячейки» — верен с точностью до кернинга.
    /// </summary>
    public static Rect InkOf(Control control, Rect bounds, Size natural)
    {
        var width = Math.Min(natural.Width, bounds.Width);
        var height = Math.Min(natural.Height, bounds.Height);

        var left = control.GetValue(TextBlock.TextAlignmentProperty) switch
        {
            TextAlignment.Center => bounds.Left + ((bounds.Width - width) / 2),
            TextAlignment.Right or TextAlignment.End => bounds.Right - width,
            _ => control.HorizontalAlignment switch
            {
                HorizontalAlignment.Center => bounds.Left + ((bounds.Width - width) / 2),
                HorizontalAlignment.Right => bounds.Right - width,
                _ => bounds.Left,
            },
        };

        var top = control.VerticalAlignment switch
        {
            VerticalAlignment.Center or VerticalAlignment.Stretch => bounds.Top + ((bounds.Height - height) / 2),
            VerticalAlignment.Bottom => bounds.Bottom - height,
            _ => bounds.Top,
        };

        return new Rect(left, top, width, height);
    }

    /// <summary>Цепочка типов от вида до контрола — адрес, по которому дефект ищут в разметке.</summary>
    public static string PathOf(Visual visual)
    {
        var names = new List<string>();
        for (var node = visual.GetVisualParent(); node is not null; node = node.GetVisualParent())
        {
            var name = (node as Control)?.Name;
            names.Add(name is null ? node.GetType().Name : $"{node.GetType().Name}#{name}");
            if (node is UserControl or Window)
            {
                break;
            }
        }

        names.Reverse();
        return string.Join(" → ", names.TakeLast(4));
    }

    // ---- обход -------------------------------------------------------------------------------

    private static void Collect(Visual node, Visual root, List<TextPiece> result)
    {
        if (!node.IsVisible)
        {
            return;
        }

        if (node is Control control && TextOf(control) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
        {
            var bounds = BoundsIn(control, root);
            var natural = NaturalSize(control);
            result.Add(new TextPiece(
                control,
                text,
                bounds,
                ClippedTo(InkOf(control, bounds, natural), control, root),
                natural.Width,
                MayBeShortened(control)));
        }

        foreach (var child in node.GetVisualChildren())
        {
            Collect(child, root, result);
        }
    }

    private static void CollectOfType<T>(Visual node, List<T> result)
        where T : Visual
    {
        if (!node.IsVisible)
        {
            return;
        }

        if (node is T match)
        {
            result.Add(match);
        }

        foreach (var child in node.GetVisualChildren())
        {
            CollectOfType(child, result);
        }
    }

    /// <summary>Текст контрола — только у тех двух видов, что его печатают.</summary>
    internal static string TextOf(Control control) => control switch
    {
        TextBlock block => block.Text ?? InlinesText(block),
        TextBox box => box.Text ?? string.Empty,
        _ => string.Empty,
    };

    private static string InlinesText(TextBlock block) =>
        block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.OfType<Run>().Select(run => run.Text))
            : string.Empty;

    /// <summary>
    /// Разметка сама разрешила укоротить строку: <c>TextTrimming</c> или <c>TextWrapping</c>.
    ///
    /// Это и есть граница правила об обрезке. Многоточие у имени макроса, пути к папке или
    /// строки лога — решение автора: там стоит ПОЛЬЗОВАТЕЛЬСКИЕ данные произвольной длины, и
    /// вместить их нельзя в принципе. А подпись, написанная в разметке буквами, обязана влезть,
    /// и если не влезает — это дефект, а не решение.
    /// </summary>
    private static bool MayBeShortened(Control control) =>
        control.GetValue(TextBlock.TextTrimmingProperty) != TextTrimming.None ||
        control.GetValue(TextBlock.TextWrappingProperty) != TextWrapping.NoWrap;
}
