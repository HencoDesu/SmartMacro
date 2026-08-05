using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// Ловушка жёсткой высоты у <c>TextBox</c>: строке не хватает места, и у букв срезает хвосты.
///
/// Механизм описан прямо в теме (<c>Themes/Controls.axaml</c>, у сеттера <c>Padding</c>): отступ
/// поля 8.4,5.6 — то есть 11.2 px по вертикали, — а текст живёт внутри <c>PART_ScrollViewer</c>,
/// который свою область просмотра ОБРЕЗАЕТ. Пока высоту определяет <c>MinHeight</c>, контрол
/// растёт под строку. Стоит вызывающему задать жёсткий <c>Height</c> (24 или 26 — самые ходовые
/// в шапках), как под строку остаётся <c>Height − 2 − 11.2</c>, и низ строки срезается ровно по
/// базовой линии.
///
/// Дефект коварен тем, что буквы остаются целыми и читаемыми: пропадают только хвосты «р», «у»,
/// «д», «ц». Наступали на него трижды (поиск в логе, имя макроса, поиск библиотеки), и нашёлся
/// он только измерением строк пикселей относительно эталонного <c>TextBlock</c>.
///
/// <b>Почему проверка честная, хотя жёсткая высота сама по себе законна.</b> Тест не запрещает
/// <c>Height</c>. Он СЧИТАЕТ, помещается ли строка: берёт эффективные отступ, рамку и кегль
/// (свои у элемента, затем у его классов стиля, затем умолчания темы) и сравнивает остаток с
/// высотой строки. Многострочное поле в 120 px не трогается никогда; поле в 26 px с
/// <c>Padding="8,0"</c> — тоже, потому что там места хватает. Падает только то, что на экране
/// действительно обрезано.
///
/// Числа берутся из темы и токенов, а не вписаны сюда: если отступ поля или шкала кеглей
/// изменятся, арифметика поедет за ними.
///
/// <b>Пара этому тесту — <c>Ui/UiRenderTests</c>, и они не дублируют друг друга.</b> Здесь
/// МОДЕЛЬ: разбор разметки плюс арифметика с коэффициентом 1.27, зато по всем файлам сразу и без
/// поднятия Avalonia. Там ЗАМЕР: те же поля рисуются настоящим Skia, и сравниваются строки
/// пикселей у слова с хвостами и без. Модель дешева и слепа к тому, что приезжает привязкой;
/// замер точен и видит только то, что показано на экране. Если они когда-нибудь разойдутся,
/// правым будет замер, а разбираться надо будет с коэффициентом здесь.
/// </summary>
public class AxamlFieldHeightTests
{
    /// <summary>
    /// Высота строки к кеглю. Комментарий у самой темы называет для кегля 11 строку «высотой
    /// ~14» — это и есть 1.27. Метрики Inter дают чуть меньше (ascent+descent = 1.21 em), так
    /// что число слегка щедрое: тест скорее промолчит о пограничном случае, чем обвинит
    /// работающее поле.
    /// </summary>
    private const double LineHeightRatio = 1.27;

    /// <summary>Погрешность сравнения — чтобы 0.001 px не решал судьбу теста.</summary>
    private const double Epsilon = 0.01;

    private static readonly string ThemeFile = Path.Combine("src", "SmartMacro.App", "Themes", "Controls.axaml");
    private static readonly string TokenFile = Path.Combine("src", "SmartMacro.App", "Themes", "Tokens.axaml");

    private static readonly Regex ResourceReference = new(
        @"^\{\s*(?:Dynamic|Static)Resource\s+(?<key>[^\s}]+)\s*\}$",
        RegexOptions.CultureInvariant);

    /// <summary>Все вхождения <c>TextBox</c> в селекторе; нужное — последнее (цель селектора).</summary>
    private static readonly Regex TextBoxInSelector = new(
        @"TextBox(?<classes>(?:\.[A-Za-z0-9_]+)*)",
        RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> Tokens = LoadTokens();

    [Test]
    public async Task APinnedFieldHeightAlwaysLeavesRoomForTheLine()
    {
        var theme = TextBoxThemeDefaults();
        var problems = new List<string>();
        var seenFields = 0;

        foreach (var file in RepositorySources.AxamlFiles)
        {
            var document = RepositorySources.LoadMarkup(file);
            var styles = TextBoxStyles(document);

            foreach (var field in document.Descendants().Where(element => element.Name.LocalName == "TextBox"))
            {
                seenFields++;

                var classes = (field.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var rawHeight = Effective(field, styles, classes, "Height");
                if (rawHeight is null)
                {
                    // Высота не зафиксирована — работает MinHeight темы, и контрол растёт под
                    // строку. Ровно тот случай, ради которого отступ в теме и не урезан.
                    continue;
                }

                var height = Number(rawHeight);
                if (height is null)
                {
                    if (IsUnresolvedReference(rawHeight))
                    {
                        // Высота приезжает привязкой — числа нет ни у кого, кроме работающего
                        // приложения. Обвинять такое поле не в чем.
                        continue;
                    }

                    problems.Add($"  {RepositorySources.Where(file, field)}  Height=\"{rawHeight}\" — не число");
                    continue;
                }

                var rawPadding = Effective(field, styles, classes, "Padding") ?? theme.Padding;
                var padding = VerticalThickness(rawPadding);
                if (padding is null)
                {
                    if (IsUnresolvedReference(rawPadding))
                    {
                        // Отступ неизвестен, а подставлять сюда умолчание темы нельзя: оно
                        // самое большое, и поле обвинилось бы за отступ, которого у него нет.
                        continue;
                    }

                    problems.Add(
                        $"  {RepositorySources.Where(file, field)}  Padding=\"{rawPadding}\" — не Thickness");
                    continue;
                }

                // Кегль неизвестного вида заменяется умолчанием темы — это самый мелкий кегль из
                // применимых, так что ошибка возможна только в сторону молчания.
                var fontSize = Number(Effective(field, styles, classes, "FontSize") ?? theme.FontSize)
                    ?? Number(theme.FontSize)
                    ?? throw new InvalidOperationException(
                        $"Кегль TextBox по умолчанию ({theme.FontSize}) не разбирается в число.");

                var available = height.Value - theme.VerticalBorder - padding.Value;
                var line = fontSize * LineHeightRatio;

                if (available + Epsilon < line)
                {
                    problems.Add(
                        $"  {RepositorySources.Where(file, field)}  Height={Round(height.Value)}, " +
                        $"отступ по вертикали {Round(padding.Value)}, рамка " +
                        $"{Round(theme.VerticalBorder)} → под строку остаётся {Round(available)} px, " +
                        $"а при кегле {Round(fontSize)} ей нужно {Round(line)} px");
                }
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "У поля с жёсткой высотой строке не хватает места — низ срежется по базовой " +
                "линии." + NL + NL +
                "Отступ поля в теме — 11.2 px по вертикали, а текст лежит внутри " +
                "PART_ScrollViewer, который область просмотра обрезает. Пока высоту определяет " +
                "MinHeight, контрол растёт под строку; жёсткий Height это отключает." + NL + NL +
                "Заметить это на глаз почти нельзя: буквы остаются целыми и читаемыми, пропадают " +
                "только хвосты «р», «у», «д», «ц». Три поля прожили так до тех пор, пока строки " +
                "пикселей не сравнили с эталонным TextBlock." + NL + NL +
                "Лекарство на месте вызова: рядом с Height задать Padding=\"8,0\" и " +
                "VerticalContentAlignment=\"Center\". Отступ в самой теме уменьшать НЕ надо — на " +
                "нём стоит высота всех полей инспектора макросов, которые высоту не фиксируют." +
                NL + NL +
                "Найдено:" + NL + string.Join(NL, problems));
        }

        await Assert.That(problems.Count).IsEqualTo(0);

        // Защита от пустого прогона: разметку тест обязан был увидеть.
        await Assert.That(seenFields).IsGreaterThan(0);
    }

    // ---- разбор темы и разметки ------------------------------------------------------------

    private static string NL => Environment.NewLine;

    private readonly record struct ThemeDefaults(string Padding, string FontSize, double VerticalBorder);

    /// <summary>
    /// Умолчания <c>ControlTheme</c> для <c>TextBox</c> — те самые, из-за которых ловушка и
    /// существует. Читаются из темы, а не вписаны сюда: иначе правка отступа тихо разошлась бы с
    /// арифметикой теста.
    /// </summary>
    private static ThemeDefaults TextBoxThemeDefaults()
    {
        var document = RepositorySources.LoadMarkup(Path.Combine(RepositorySources.Root, ThemeFile));

        var theme = document.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "ControlTheme" &&
                element.Attribute("TargetType")?.Value == "TextBox")
            ?? throw new InvalidOperationException(
                $"В {ThemeFile} нет ControlTheme для TextBox — тест разучился читать тему.");

        var setters = Setters(theme);

        var padding = setters.GetValueOrDefault("Padding")
            ?? throw new InvalidOperationException(
                $"У темы TextBox в {ThemeFile} нет сеттера Padding. Отступ по вертикали — это и " +
                "есть причина ловушки; без него считать нечего.");

        var fontSize = setters.GetValueOrDefault("FontSize")
            ?? throw new InvalidOperationException(
                $"У темы TextBox в {ThemeFile} нет сеттера FontSize — неизвестен кегль по умолчанию.");

        var borderRaw = setters.GetValueOrDefault("BorderThickness")
            ?? throw new InvalidOperationException(
                $"У темы TextBox в {ThemeFile} нет сеттера BorderThickness — неизвестна толщина рамки.");

        var border = VerticalThickness(borderRaw)
            ?? throw new InvalidOperationException(
                $"BorderThickness=\"{borderRaw}\" в {ThemeFile} не разбирается в число.");

        return new ThemeDefaults(padding, fontSize, border);
    }

    /// <summary>Стили этого файла, нацеленные на <c>TextBox</c>: класс (или <c>null</c>) → сеттеры.</summary>
    private static IReadOnlyList<(string? Class, IReadOnlyDictionary<string, string> Setters)> TextBoxStyles(
        XDocument document)
    {
        var result = new List<(string?, IReadOnlyDictionary<string, string>)>();

        foreach (var style in document.Descendants().Where(element => element.Name.LocalName == "Style"))
        {
            if (style.Attribute("Selector")?.Value is not { } selector)
            {
                continue;
            }

            // Цель селектора — последний его элемент: в «Border.libRow TextBox» стиль
            // применяется к полю, а не к рамке.
            var matches = TextBoxInSelector.Matches(selector);
            if (matches.Count == 0)
            {
                continue;
            }

            var setters = Setters(style);
            var classes = matches[^1].Groups["classes"].Value;

            if (classes.Length == 0)
            {
                result.Add((null, setters));
                continue;
            }

            foreach (var name in classes.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add((name, setters));
            }
        }

        return result;
    }

    /// <summary>
    /// Сеттеры ПРЯМЫХ детей. Вглубь ходить нельзя: у темы контрола внутри лежат ещё и
    /// вложенные стили псевдоклассов (<c>^:pointerover</c>), и их значения — не умолчания.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Setters(XElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var setter in owner.Elements().Where(element => element.Name.LocalName == "Setter"))
        {
            if (setter.Attribute("Property")?.Value is { } property &&
                setter.Attribute("Value")?.Value is { } value)
            {
                result[property] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Значение свойства: сперва собственный атрибут элемента, затем стили его классов, затем
    /// бесклассовый стиль этого же файла. <c>null</c> — «не задано нигде», и решение принимает
    /// вызывающий.
    /// </summary>
    private static string? Effective(
        XElement field,
        IReadOnlyList<(string? Class, IReadOnlyDictionary<string, string> Setters)> styles,
        IReadOnlyList<string> classes,
        string property)
    {
        if (field.Attribute(property)?.Value is { } own)
        {
            return own;
        }

        foreach (var (styleClass, setters) in styles)
        {
            var applies = styleClass is null || classes.Contains(styleClass, StringComparer.Ordinal);
            if (applies && setters.TryGetValue(property, out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Число из литерала или из токена, на который ссылается расширение разметки.</summary>
    private static double? Number(string raw)
    {
        var value = Dereference(raw);
        return value is not null &&
               double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Сумма верхнего и нижнего значений <c>Thickness</c> — «1», «8,0» или «1,2,3,4».</summary>
    private static double? VerticalThickness(string raw)
    {
        var value = Dereference(raw);
        if (value is null)
        {
            return null;
        }

        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers.Length switch
        {
            1 => numbers[0] * 2,
            2 => numbers[1] * 2,
            4 => numbers[1] + numbers[3],
            _ => null,
        };
    }

    /// <summary>Разворачивает <c>{DynamicResource Ключ}</c> в текст токена из Tokens.axaml.</summary>
    private static string? Dereference(string raw)
    {
        var value = raw.Trim();
        if (!value.StartsWith('{'))
        {
            return value;
        }

        var match = ResourceReference.Match(value);
        return match.Success ? Tokens.GetValueOrDefault(match.Groups["key"].Value) : null;
    }

    /// <summary>Значение — расширение разметки, которое посчитать здесь нельзя (привязка и т.п.).</summary>
    private static bool IsUnresolvedReference(string raw) =>
        raw.TrimStart().StartsWith('{') && Dereference(raw) is null;

    private static Dictionary<string, string> LoadTokens()
    {
        var document = RepositorySources.LoadMarkup(Path.Combine(RepositorySources.Root, TokenFile));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in document.Descendants())
        {
            var key = element.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value;

            if (key is not null && !element.HasElements)
            {
                result[key] = element.Value.Trim();
            }
        }

        return result;
    }

    private static string Round(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
