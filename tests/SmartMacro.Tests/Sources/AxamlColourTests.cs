using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Media;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// Литеральных цветов в разметке нет — только токены.
///
/// Правило записано в шапке самого <c>Themes/Tokens.axaml</c>: «если представлению понадобился
/// цвет, которого в этом файле нет, лечится это новым именованным токеном здесь — но никак не
/// литералом в разметке». До сих пор оно держалось только на ревью, а нарушить его дешевле
/// всего: <c>#9184d9</c> в атрибуте выглядит совершенно безобидно, собирается, работает — и
/// выпадает из шкалы, из тёмной темы и из любой последующей правки палитры, потому что ни один
/// поиск по имени токена его не найдёт.
///
/// Проверок две, потому что цвет пишется двумя способами: шестнадцатеричным литералом и именем
/// из <see cref="Colors"/>. Список имён берётся отражением из самой Avalonia — переписывать сюда
/// полторы сотни названий значило бы завести второй источник правды о том, что вообще является
/// цветом.
///
/// <b><c>Transparent</c> — не цвет, а отсутствие заливки</b>, и это единственное исключение.
/// Токена «прозрачный» в шкале нет и быть не должно: шкала — это лестница светлоты, а
/// «прозрачно» стоит вне её. В разметке он встречается два с лишним десятка раз (фон кнопки без
/// заливки, фон строки списка), и заводить ради этого имя было бы хуже, чем оставить слово.
/// </summary>
public class AxamlColourTests
{
    /// <summary>
    /// Единственный файл, где литералы и живут. Всё остальное — включая
    /// <c>Themes/Controls.axaml</c>, который цвета только потребляет, — обязано обращаться к
    /// токенам по имени.
    /// </summary>
    private static readonly string TokenFile = Path.Combine("src", "SmartMacro.App", "Themes", "Tokens.axaml");

    /// <summary>
    /// <c>#rgb</c>, <c>#argb</c>, <c>#rrggbb</c>, <c>#aarrggbb</c>. Границы по краям нужны, чтобы
    /// не поймать хвост ресурсного URI вида <c>fonts:Inter#Inter</c>: там за решёткой стоят не
    /// шестнадцатеричные буквы, но проверять это лучше явно.
    /// </summary>
    private static readonly Regex HexColour = new(
        "(?<![0-9A-Za-z_#])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{4}|[0-9a-fA-F]{3})(?![0-9A-Za-z_])",
        RegexOptions.CultureInvariant);

    /// <summary>Имена цветов Avalonia, кроме <c>Transparent</c>.</summary>
    private static readonly HashSet<string> ColourNames = typeof(Colors)
        .GetProperties()
        .Where(property => property.PropertyType == typeof(Color))
        .Select(property => property.Name)
        .Where(name => !string.Equals(name, "Transparent", StringComparison.Ordinal))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Test]
    public async Task NoHexColourLivesOutsideTheTokenFile()
    {
        var found = new List<string>();

        foreach (var file in RepositorySources.AxamlFiles.Where(IsNotTokenFile))
        {
            var document = RepositorySources.LoadMarkup(file);

            foreach (var attribute in document.Descendants().Attributes())
            {
                if (!attribute.IsNamespaceDeclaration && HexColour.IsMatch(attribute.Value))
                {
                    found.Add(
                        $"  {RepositorySources.Where(file, attribute)}  {attribute.Name.LocalName}=" +
                        $"\"{attribute.Value}\"");
                }
            }

            foreach (var text in document.DescendantNodes().OfType<XText>())
            {
                if (HexColour.IsMatch(text.Value))
                {
                    found.Add($"  {RepositorySources.Where(file, text)}  {text.Value.Trim()}");
                }
            }
        }

        if (found.Count > 0)
        {
            Assert.Fail(
                "Шестнадцатеричный цвет в разметке. Значения цвета живут в одном файле — " +
                $"{TokenFile}, — и только там." + NL + NL +
                "Литерал здесь не «то же самое, только короче». Он выпадает из шкалы светлоты, " +
                "на которой держится вся система (ступени 100–900 акцента и нейтрали), его не " +
                "находит поиск по имени токена, и любая последующая правка палитры обойдёт его " +
                "стороной — он останется единственным пятном прежнего цвета, и заметить это " +
                "можно будет только глазами." + NL + NL +
                "Что делать: заведите именованный токен в Tokens.axaml по правилу именования из " +
                "его шапки (Nocturne<Роль|Шкала+Ступень|Семантика><Вид>) и сошлитесь на него " +
                "через DynamicResource. Промежуточных ступеней в шкалу не добавлять — берите " +
                "ближайшую." + NL + NL +
                "Найдено:" + NL + string.Join(NL, found));
        }

        await Assert.That(found.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NoNamedColourLivesOutsideTheTokenFile()
    {
        var found = new List<string>();

        foreach (var file in RepositorySources.AxamlFiles.Where(IsNotTokenFile))
        {
            foreach (var element in RepositorySources.LoadMarkup(file).Descendants())
            {
                foreach (var (property, value, node) in ColourAssignments(element))
                {
                    if (IsLiteralColourName(value))
                    {
                        found.Add($"  {RepositorySources.Where(file, node)}  {property}=\"{value}\"");
                    }
                }
            }
        }

        if (found.Count > 0)
        {
            Assert.Fail(
                "Именованный цвет в разметке. Имена вроде White, Gray или Red — такой же " +
                "литерал, как #e9e9ed, только выглядит безобиднее." + NL + NL +
                "Ни одно из этих имён не входит в шкалу Nocturne, а чистых чёрного и белого в " +
                "ней нет намеренно — они есть только в тенях, где чёрный означает окружающую " +
                "тьму, а не цвет. Имя из системного набора приедет на экран мимо всей палитры." +
                NL + NL +
                "Что делать: возьмите ближайшую ступень шкалы в Tokens.axaml или заведите там " +
                "новый токен. Единственное разрешённое слово — Transparent: это не цвет, а " +
                "отсутствие заливки, и места в шкале светлоты у него нет." + NL + NL +
                "Найдено:" + NL + string.Join(NL, found));
        }

        await Assert.That(found.Count).IsEqualTo(0);
    }

    // ---- разбор ---------------------------------------------------------------------------

    private static string NL => Environment.NewLine;

    private static bool IsNotTokenFile(string file) =>
        !RepositorySources.Relative(file).Equals(TokenFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Присваивания, значение которых — цвет: обычные атрибуты вроде <c>Background</c> и пара
    /// <c>Property</c>/<c>Value</c> у <c>&lt;Setter&gt;</c>.
    ///
    /// Отбор идёт ПО ИМЕНИ СВОЙСТВА, а не по виду значения, и это принципиально: искать слово
    /// «White» где попало значило бы поймать <c>Classes="White"</c> или подпись, в которой
    /// встретилось название цвета. Цветовыми считаются свойства, чьё имя оканчивается на Brush
    /// или Color, плюс четыре исторических имени без суффикса.
    /// </summary>
    private static IEnumerable<(string Property, string Value, XObject Node)> ColourAssignments(XElement element)
    {
        if (element.Name.LocalName == "Setter" &&
            element.Attribute("Property") is { } property &&
            element.Attribute("Value") is { } value &&
            IsColourProperty(property.Value))
        {
            yield return (property.Value, value.Value, value);
            yield break;
        }

        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && IsColourProperty(attribute.Name.LocalName))
            {
                yield return (attribute.Name.LocalName, attribute.Value, attribute);
            }
        }
    }

    private static bool IsColourProperty(string name)
    {
        // Присоединённые и уточнённые имена: «TextElement.Foreground», «(TextBlock.Foreground)».
        var local = name.Trim('(', ')');
        var dot = local.LastIndexOf('.');
        if (dot >= 0)
        {
            local = local[(dot + 1)..];
        }

        return local.EndsWith("Brush", StringComparison.Ordinal)
            || local.EndsWith("Color", StringComparison.Ordinal)
            || local is "Background" or "Foreground" or "Fill" or "Stroke";
    }

    /// <summary>
    /// Значение — литеральный цвет, а не ссылка. Всё, что начинается с фигурной скобки, — это
    /// расширение разметки (DynamicResource, StaticResource, Binding, TemplateBinding, x:Null) и
    /// к литералам отношения не имеет.
    /// </summary>
    private static bool IsLiteralColourName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed[0] != '{' && ColourNames.Contains(trimmed);
    }
}
