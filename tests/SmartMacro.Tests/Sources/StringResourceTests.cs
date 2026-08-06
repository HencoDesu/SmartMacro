using System.Reflection;
using System.Text;
using System.Xml.Linq;
using SmartMacro.Resources;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// Правила файла ресурсов панели — <c>Shared/Resources/Strings.App.resx</c>.
///
/// <b>Зачем это вообще проверять.</b> Вынос подписей в ресурсы добавляет ровно один новый способ
/// сломать интерфейс, и он бесшумный: сборка проходит, тесты проходят, а на экране пусто. Причин
/// у пустоты две, и обе не видны компилятору. Первая — имя ресурса в манифесте разошлось с тем,
/// которое порождённый класс передаёт в <c>ResourceManager</c> (ловушка описана в
/// <c>SmartMacro.Shared.csproj</c>: путь файла даёт «Strings.App», имя класса — «Strings_App»);
/// тогда падает ВСЁ и сразу, но только в рантайме. Вторая — запись с пустым значением. Первый
/// тест закрывает обе: он читает каждое сгенерированное свойство и требует непустой строки.
///
/// Остальные правила — про то, что resx здесь ещё и РАБОЧИЙ ДОКУМЕНТ, по которому владелец
/// вычитывает формулировки. Пустой <c>&lt;comment&gt;</c> означает «вычитывать вслепую», поэтому
/// он обязателен.
/// </summary>
public class StringResourceTests
{
    /// <summary>Разрешённые области ключа — те же, что перечислены в шапке resx.</summary>
    private static readonly string[] Areas =
        ["Shell_", "Windows_", "Macros_", "Editor_", "Node_", "Runs_", "Log_", "Settings_", "Dialog_"];

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

    private static IEnumerable<PropertyInfo> Properties() =>
        typeof(Strings_App)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string));

    private static IReadOnlyList<XElement> Data()
    {
        var path = Path.Combine(RepositorySources.Root, "src", "SmartMacro.Shared", "Resources", "Strings.App.resx");
        return XDocument.Parse(File.ReadAllText(path, Encoding.UTF8)).Root!.Elements("data").ToArray();
    }
}
