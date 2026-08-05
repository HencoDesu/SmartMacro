using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SmartMacro.Resources;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// Файл строк движка держится как РАБОЧИЙ ДОКУМЕНТ, а не как свалка литералов.
///
/// <c>Strings.Engine.resx</c> заведён затем, чтобы формулировки можно было вычитать и править в
/// одном месте, глазами и подряд. Три свойства делают это возможным, и ни одно из них не
/// держится само собой:
///
/// <list type="number">
///   <item><b>У каждой записи есть пояснение.</b> Текст движка оторван от места показа: «Нода
///     недостижима из стартовой.» без подписи «предупреждение на ноде, кликом переходит на
///     канву» вычитать нельзя — непонятно ни где это видно, ни насколько строго звучать.</item>
///   <item><b>Класс доступа и файл не разъезжаются.</b> Класс порождается из resx на сборке
///     (см. цель <c>GenerateEngineStrings</c>), и здесь проверяется вторая половина: что имя
///     встроенного ресурса совпадает с тем, которое зашито в порождённый класс. Разъедься они —
///     программа собралась бы молча, а <c>MissingManifestResourceException</c> прилетел бы
///     пользователю на первом же сообщении об ошибке.</item>
///   <item><b>Мёртвых записей нет.</b> Формулировку удаляют вместе с местом показа, и запись,
///     которую больше никто не печатает, — это строка, которую вычитывают впустую. В обратную
///     сторону работает компилятор: снятая из resx запись исчезает как член класса.</item>
/// </list>
///
/// Проверка по ИСХОДНОМУ ТЕКСТУ, а не по отражению: обращение к строке — это чтение статического
/// свойства, и в собранной сборке от него остаётся вызов <c>get</c>, по которому не понять, какое
/// именно свойство читали.
/// </summary>
public class EngineStringsTests
{
    private const string ResxRelativePath = @"src\SmartMacro.Shared\Resources\Strings.Engine.resx";

    /// <summary>Проекты, которым разрешено печатать строки движка. Панель ведёт свой файл.</summary>
    private static readonly string[] EngineProjects =
        ["SmartMacro.Shared", "SmartMacro.Contracts", "SmartMacro.Core", "SmartMacro.Daemon"];

    private static readonly Regex Reference = new(@"\bStrings_Engine\.(?<name>\w+)\b", RegexOptions.CultureInvariant);

    [Test]
    public async Task EveryEntryCarriesAComment()
    {
        var missing = Entries()
            .Where(entry => string.IsNullOrWhiteSpace(entry.Comment))
            .Select(entry => "  " + entry.Name)
            .ToArray();

        if (missing.Length > 0)
        {
            Assert.Fail(
                "У записи в Strings.Engine.resx нет пояснения." + NL + NL +
                "Пояснение обязательно, потому что файл существует ради ВЫЧИТКИ: у движка текст " +
                "оторван от места показа, и без ответа на «где это видно, что подставляется в " +
                "дырки, ошибка это или предупреждение» править формулировку приходится вслепую." +
                NL + NL + "Без пояснения:" + NL + string.Join(NL, missing));
        }

        await Assert.That(missing.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EveryEntryIsReadableThroughTheGeneratedClass()
    {
        var problems = new List<string>();

        foreach (var (name, value, _) in Entries())
        {
            var property = typeof(Strings_Engine).GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (property is null)
            {
                problems.Add($"  {name}: члена класса нет — цель GenerateEngineStrings не отработала?");
                continue;
            }

            // Первое же чтение поднимает набор ресурсов целиком: неверное имя встроенного
            // ресурса всплывёт здесь как MissingManifestResourceException, а не у пользователя.
            var actual = (string?)property.GetValue(null);
            if (!string.Equals(actual, value, StringComparison.Ordinal))
            {
                problems.Add($"  {name}: класс отдаёт не то, что лежит в файле.");
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "Порождённый класс разошёлся с Strings.Engine.resx." + NL + NL +
                "Имя встроенного ресурса складывается из RootNamespace и пути к resx, а в " +
                "порождённом классе оно записано строкой. Переезд файла, не поправивший цель " +
                "GenerateEngineStrings, собирается молча." + NL + NL +
                "Найдено:" + NL + string.Join(NL, problems));
        }

        await Assert.That(problems.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EveryEntryIsPrintedSomewhere()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in EngineProjects)
        {
            foreach (var file in RepositorySources.CSharpFiles(project))
            {
                var source = RepositorySources.StripCommentsFromCSharp(File.ReadAllText(file));
                foreach (Match match in Reference.Matches(source))
                {
                    used.Add(match.Groups["name"].Value);
                }
            }
        }

        var dead = Entries()
            .Select(entry => entry.Name)
            .Where(name => !used.Contains(name))
            .Select(name => "  " + name)
            .ToArray();

        if (dead.Length > 0)
        {
            Assert.Fail(
                "Запись Strings.Engine.resx не печатается нигде." + NL + NL +
                "Формулировку удаляют вместе с местом показа, и осиротевшая запись — это строка, " +
                "которую вычитывают впустую и по которой судят о поведении программы неверно." +
                NL + NL + "Никто не печатает:" + NL + string.Join(NL, dead));
        }

        await Assert.That(dead.Length).IsEqualTo(0);
    }

    [Test]
    public async Task KeysAreAscii()
    {
        // Ключ — это имя члена класса C# и общий язык двух половин файла строк (движок и
        // панель сводят в один). Кириллица в нём собралась бы, но искать по ней в коде и
        // диффах было бы мучением.
        var wrong = Entries()
            .Select(entry => entry.Name)
            .Where(name => !name.All(char.IsAscii))
            .Select(name => "  " + name)
            .ToArray();

        if (wrong.Length > 0)
        {
            Assert.Fail("Ключ Strings.Engine.resx не в ASCII:" + NL + string.Join(NL, wrong));
        }

        await Assert.That(wrong.Length).IsEqualTo(0);
    }

    private static IReadOnlyList<(string Name, string Value, string? Comment)> Entries()
    {
        var path = Path.Combine(RepositorySources.Root, ResxRelativePath);
        var document = RepositorySources.LoadMarkup(path);

        return document.Root!
            .Elements("data")
            .Select(data => (
                Name: data.Attribute("name")!.Value,
                Value: data.Element("value")?.Value ?? string.Empty,
                Comment: data.Element("comment")?.Value))
            .ToArray();
    }

    private static readonly string NL = Environment.NewLine;
}
