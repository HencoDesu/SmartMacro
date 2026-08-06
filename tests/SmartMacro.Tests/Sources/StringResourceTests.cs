using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SmartMacro.Resources;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// Правила ЕДИНСТВЕННОГО файла строк — <c>Shared/Resources/Strings.resx</c>: и того, что рисует
/// панель, и того, что приходит из демона.
///
/// <b>Зачем это вообще проверять.</b> Вынос подписей в ресурсы добавляет ровно один новый способ
/// сломать интерфейс, и он бесшумный: сборка проходит, тесты проходят, а на экране пусто. Причин
/// у пустоты две, и обе не видны компилятору. Первая — имя ресурса в манифесте разошлось с тем,
/// которое порождённый класс передаёт в <c>ResourceManager</c> (ловушка описана в
/// <c>SmartMacro.Shared.csproj</c>: путь файла даёт «Strings», имя класса — «Strings»);
/// тогда падает ВСЁ и сразу, но только в рантайме. Вторая — запись с пустым значением. Первый
/// тест закрывает обе: он читает каждое сгенерированное свойство и требует непустой строки.
///
/// Остальные правила — про то, что resx здесь ещё и РАБОЧИЙ ДОКУМЕНТ, по которому владелец
/// вычитывает формулировки. Пустой <c>&lt;comment&gt;</c> означает «вычитывать вслепую», поэтому
/// он обязателен.
/// </summary>
public class StringResourceTests
{
    /// <summary>
    /// Разрешённые области ключа — те же, что перечислены в шапке resx.
    ///
    /// Первые девять — то, что рисует панель, остальные приходят из демона и общего домена.
    /// Область <c>Diag_</c> отсюда убрана вместе с проверкой среды: список разрешённых областей,
    /// в котором есть область без единого ключа, разрешает завести её обратно по недосмотру.
    /// Список ОДИН, потому что и файл один: пока строки жили в двух resx, у каждого был свой
    /// перечень, и после слияния оба теста разом покраснели на чужих ключах. Два набора правил
    /// на один артефакт — ровно та беда, которую этот проект вычищает.
    /// </summary>
    private static readonly string[] Areas =
    [
        "Shell_", "Windows_", "Macros_", "Editor_", "Node_", "Runs_", "Log_", "Settings_", "Dialog_",
        "Tray_", "Startup_", "Validation_", "Extraction_", "Run_", "Bundle_", "Ipc_",
    ];

    /// <summary>Обращение к строке в коде или в разметке: <c>Strings.Ключ</c>.</summary>
    private static readonly Regex Reference = new(@"\bStrings\.(?<name>\w+)\b", RegexOptions.CultureInvariant);

    [Test]
    public async Task EveryGeneratedPropertyReturnsANonEmptyString()
    {
        var empty = new List<string>();
        foreach (var property in Properties())
        {
            if (string.IsNullOrWhiteSpace(property.GetValue(null) as string))
            {
                empty.Add(property.Name);
            }
        }

        if (empty.Count > 0)
        {
            Assert.Fail(
                "Ресурс отдал пустую строку. На экране это будет пустое место, а сборка об этом " +
                "не скажет ни слова." + Environment.NewLine + Environment.NewLine +
                "Если пусты СРАЗУ ВСЕ — дело не в значениях: разошлись имя ресурса в манифесте и " +
                "имя, которое порождённый класс передаёт в ResourceManager. Лечится метаданными " +
                "LogicalName в SmartMacro.Shared.csproj; там же написано, почему " +
                "StronglyTypedManifestPrefix для этого не годится." + Environment.NewLine +
                string.Join(", ", empty));
        }

        await Assert.That(empty).IsEmpty();
    }

    [Test]
    public async Task TheResxAndTheGeneratedClassHoldTheSameKeys()
    {
        var fromResx = Data().Select(d => d.Attribute("name")!.Value).OrderBy(n => n, StringComparer.Ordinal);
        var fromClass = Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);

        await Assert.That(fromClass).IsEquivalentTo(fromResx);
    }

    [Test]
    public async Task EveryEntryCarriesAComment()
    {
        // Комментарий — это не документация ради документации: по нему вычитывают формулировку,
        // не открывая разметку. «Где показывается» и «во что должно влезать» больше взять негде.
        var silent = Data()
            .Where(d => string.IsNullOrWhiteSpace(d.Element("comment")?.Value))
            .Select(d => d.Attribute("name")!.Value)
            .ToArray();

        if (silent.Length > 0)
        {
            Assert.Fail(
                "У записи нет <comment>. Он обязателен: файл читают как список формулировок, а " +
                "не как код, и без пояснения «где это на экране» решение о правке принимать " +
                "не по чему." + Environment.NewLine + string.Join(", ", silent));
        }

        await Assert.That(silent).IsEmpty();
    }

    [Test]
    public async Task EveryKeyIsAsciiAndNamesAnArea()
    {
        var bad = new List<string>();
        foreach (var name in Data().Select(d => d.Attribute("name")!.Value))
        {
            if (name.Any(ch => ch > 0x7F) || !Areas.Any(a => name.StartsWith(a, StringComparison.Ordinal)))
            {
                bad.Add(name);
            }
        }

        if (bad.Count > 0)
        {
            Assert.Fail(
                "Ключ обязан быть ASCII и начинаться с области: " + string.Join(", ", Areas) +
                Environment.NewLine + string.Join(", ", bad));
        }

        await Assert.That(bad).IsEmpty();
    }

    [Test]
    public async Task PluralFormsComeInThrees()
    {
        // Формы множественного числа — три ЯВНЫХ ключа; выбирает между ними PluralForms.
        // Одинокий _One означает, что две другие формы кто-то оставил в C#.
        var names = Data().Select(d => d.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);
        var lonely = new List<string>();

        foreach (var name in names)
        {
            foreach (var suffix in new[] { "_One", "_Few", "_Many" })
            {
                if (!name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var stem = name[..^suffix.Length];
                if (!names.Contains(stem + "_One") || !names.Contains(stem + "_Few") ||
                    !names.Contains(stem + "_Many"))
                {
                    lonely.Add(name);
                }
            }
        }

        await Assert.That(lonely).IsEmpty();
    }

    [Test]
    public async Task EveryEntryIsReadableThroughTheGeneratedClass()
    {
        var problems = new List<string>();

        foreach (var entry in Data())
        {
            var name = entry.Attribute("name")!.Value;
            var property = typeof(Strings).GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (property is null)
            {
                problems.Add($"  {name}: члена класса нет — задача GenerateResource не отработала?");
                continue;
            }

            // Первое же чтение поднимает набор ресурсов целиком: разошедшееся имя встроенного
            // ресурса всплывёт здесь как MissingManifestResourceException, а не на живой панели.
            if (!string.Equals(Newlines((string?)property.GetValue(null)), Newlines(entry.Element("value")?.Value),
                    StringComparison.Ordinal))
            {
                problems.Add($"  {name}: класс отдаёт не то, что лежит в файле.");
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "Порождённый класс разошёлся с Strings.resx." + Environment.NewLine + Environment.NewLine +
                "Имя встроенного ресурса складывается из пути к resx, а класс передаёт в " +
                "ResourceManager имя, собранное из СВОЕГО имени; выравнивает их LogicalName в " +
                "csproj. Разойдясь, они собираются молча и падают при первом чтении." +
                Environment.NewLine + Environment.NewLine +
                "Найдено:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task EveryEntryIsPrintedSomewhere()
    {
        // Разметка тоже: половина подписей приходит на экран через {x:Static}, и сканер только
        // по C# объявил бы их мёртвыми. На этом уже спотыкались — Shell_TextBox_Cut живёт в
        // Themes/Controls.axaml.
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositorySources.AllCSharpFiles)
        {
            Collect(RepositorySources.StripCommentsFromCSharp(File.ReadAllText(file)), used);
        }

        foreach (var file in RepositorySources.AxamlFiles)
        {
            Collect(File.ReadAllText(file), used);
        }

        var dead = Data()
            .Select(entry => entry.Attribute("name")!.Value)
            .Where(name => !used.Contains(name))
            .Select(name => "  " + name)
            .ToArray();

        if (dead.Length > 0)
        {
            Assert.Fail(
                "Запись Strings.resx не печатается нигде." + Environment.NewLine + Environment.NewLine +
                "Формулировку удаляют вместе с местом показа, и осиротевшая запись — это строка, " +
                "которую вычитывают впустую и по которой судят о поведении программы неверно." +
                Environment.NewLine + Environment.NewLine +
                "Никто не печатает:" + Environment.NewLine + string.Join(Environment.NewLine, dead));
        }

        await Assert.That(dead).IsEmpty();
    }

    private static void Collect(string source, HashSet<string> into)
    {
        foreach (Match match in Reference.Matches(source))
        {
            into.Add(match.Groups["name"].Value);
        }
    }

    /// <summary>
    /// Приводит переводы строк к одному виду.
    ///
    /// ⚠️ Не послабление. Разбор XML нормализует <c>\r\n</c> в <c>\n</c> по спецификации, а
    /// компилятор ресурсов сохраняет то, что лежало в файле, — так что у многострочной записи
    /// побайтовое сравнение расходится на пустом месте и прячет то, ради чего проверка заведена:
    /// расхождение имени ресурса. Сравнение остаётся строгим во всём остальном.
    /// </summary>
    private static string? Newlines(string? value) => value?.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static IEnumerable<PropertyInfo> Properties() =>
        typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string));

    private static IReadOnlyList<XElement> Data()
    {
        var path = Path.Combine(RepositorySources.Root, "src", "SmartMacro.Shared", "Resources", "Strings.resx");
        return XDocument.Parse(File.ReadAllText(path, Encoding.UTF8)).Root!.Elements("data").ToArray();
    }
}
