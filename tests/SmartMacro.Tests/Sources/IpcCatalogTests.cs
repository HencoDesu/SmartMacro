using System.Reflection;
using System.Text.RegularExpressions;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Sources;

/// <summary>
/// Каталог протокола живой на обоих концах.
///
/// Это не пожелание, а утверждение, записанное в xmldoc самого <see cref="IpcMessageTypes"/>:
/// «каждая константа отсюда живая на обоих концах: у любого типа запроса есть <c>case</c> в
/// <c>IpcRequestDispatcher</c> (демон) и вызывающий в <c>IpcClient</c> (панель); у любого типа
/// события есть публикатор в <c>IpcServer</c> и подписчик в панели. Добавить константу, не
/// подключив оба конца, — как раз то, чего делать не стоит: этот каталог и есть протокол, а не
/// список пожеланий».
///
/// Компилятор проверяет только одну сторону этого: сославшись на снятую константу, панель не
/// соберётся. Обратное — константа есть, а конца у неё нет — не видно ничему: мёртвая ветка
/// диспетчера собирается, тестируется и выглядит рабочей до тех пор, пока кто-нибудь не станет
/// на неё опираться.
///
/// <b>Проверка идёт по исходному тексту, а не по IL.</b> Константа <c>string</c> в C# ВСТРАИВАЕТСЯ
/// в место использования: в собранной сборке панели нет ни следа обращения к
/// <c>IpcMessageTypes</c> — только строковый литерал. Отражение здесь показало бы, что констант
/// тридцать одна, и не смогло бы сказать про них ничего больше.
///
/// «Демонский конец» — это <c>Core</c> плюс <c>Daemon</c>: движок живёт в первом, но лоток и
/// запуск во втором, и <c>ActivateWindow</c> шлют оттуда.
/// </summary>
public class IpcCatalogTests
{
    private const string PanelProject = "SmartMacro.App";
    private static readonly string[] DaemonProjects = ["SmartMacro.Core", "SmartMacro.Daemon"];

    /// <summary>Сам каталог из проверки исключён — там имена только объявляются.</summary>
    private const string CatalogFile = "IpcMessageTypes.cs";

    private static readonly Regex Reference = new(@"\bIpcMessageTypes\.(?<name>\w+)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// ⚠️ ОТКРЫТЫЙ РАЗРЫВ, а не исключение по существу.
    ///
    /// <c>Shutdown</c> — единственная константа каталога, у которой конец есть только один: в
    /// <c>IpcRequestDispatcher</c> лежит <c>case</c> с обработчиком (и тест на него), а
    /// вызывающего нет НИГДЕ в дереве — ни в панели, ни в лотке демона, который останавливает
    /// хост напрямую, изнутри процесса. Так это и заведено в стадии 2B вместе с самим сервером:
    /// ветка не отмирала — у неё с самого начала не было клиента.
    ///
    /// Почему не починено здесь: оба файла, которых касается любое решение
    /// (<c>IpcMessageTypes.cs</c> и <c>IpcRequestDispatcher.cs</c>), правит соседняя волна, а
    /// решений тут два и оба протокольные — либо у панели появляется способ остановить демона
    /// (единственный, если учесть, что оба процесса подняты с правами администратора и
    /// <c>taskkill</c> из обычной оболочки получает отказ), либо константа и ветка уезжают
    /// вместе с обработчиком.
    ///
    /// Список самоочищающийся: как только разрыв закроют, упадёт
    /// <see cref="TheListOfKnownGapsHasNothingStaleInIt"/> и потребует убрать отсюда запись.
    /// </summary>
    private static readonly string[] KnownGaps = ["Shutdown"];

    [Test]
    public async Task EveryCatalogConstantIsSpelledLikeItsOwnName()
    {
        // «Имена совпадают с самими строками, чтобы неразобранная строка всё равно читалась в
        // логе» — из xmldoc каталога. Разойтись они могут только опечаткой, зато опечатка эта
        // ничем больше не ловится: обе стороны берут одну и ту же константу и договорятся между
        // собой о чём угодно, а в журнале останется имя, которого в каталоге нет.
        var mismatched = Constants()
            .Where(constant => !string.Equals(constant.Name, constant.Value, StringComparison.Ordinal))
            .Select(constant => $"  {constant.Name} = \"{constant.Value}\"")
            .ToArray();

        if (mismatched.Length > 0)
        {
            Assert.Fail(
                "Имя константы каталога разошлось с её значением." + NL + NL +
                "Каталог держит это равенство намеренно: строку типа сообщения видно в журнале и " +
                "в дампе трубы, и она обязана читаться как имя, по которому её ищут в коде — " +
                "особенно когда сообщение не разобралось и кроме этой строки нет ничего." + NL + NL +
                "Найдено:" + NL + string.Join(NL, mismatched));
        }

        await Assert.That(mismatched.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EveryCatalogConstantIsLiveOnBothEnds()
    {
        var daemon = ReferencedIn(DaemonProjects);
        var panel = ReferencedIn([PanelProject]);

        var problems = new List<string>();

        foreach (var constant in Constants())
        {
            if (KnownGaps.Contains(constant.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var ends = new List<string>();
            if (!daemon.Contains(constant.Name))
            {
                ends.Add("демона (Core/Daemon)");
            }

            if (!panel.Contains(constant.Name))
            {
                ends.Add("панели (App)");
            }

            if (ends.Count > 0)
            {
                problems.Add($"  {constant.Name} — нет конца в {string.Join(" и в ", ends)}");
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "В каталоге протокола константа, у которой нет одного из концов." + NL + NL +
                "Правило записано в xmldoc самого IpcMessageTypes: у любого типа запроса есть " +
                "case в IpcRequestDispatcher и вызывающий в панели, у любого типа события — " +
                "публикатор в демоне и подписчик в панели. Каталог — это протокол, а не список " +
                "пожеланий." + NL + NL +
                "Полуподключённая константа не ломает сборку и не роняет ни одного теста: " +
                "мёртвая ветка диспетчера выглядит рабочей ровно до того дня, когда на неё " +
                "понадеются. Обратную ошибку (сослаться на снятую константу) компилятор ловит " +
                "сам — эту не ловит никто." + NL + NL +
                "Что делать: подключите недостающий конец или снимите константу вместе с её " +
                "обработчиком. Если конец отсутствует ОСОЗНАННО и надолго — впишите имя в " +
                "KnownGaps этого файла и объясните там, почему; отдельный тест не даст записи " +
                "залежаться." + NL + NL +
                "Найдено:" + NL + string.Join(NL, problems));
        }

        await Assert.That(problems.Count).IsEqualTo(0);

        // Защита от пустого прогона: если поиск по исходникам однажды перестанет что-то
        // находить, тест обязан упасть, а не зазеленеть на нуле.
        await Assert.That(daemon.Count).IsGreaterThan(0);
        await Assert.That(panel.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task TheListOfKnownGapsHasNothingStaleInIt()
    {
        var names = Constants().Select(constant => constant.Name).ToHashSet(StringComparer.Ordinal);
        var daemon = ReferencedIn(DaemonProjects);
        var panel = ReferencedIn([PanelProject]);

        var stale = new List<string>();

        foreach (var gap in KnownGaps)
        {
            if (!names.Contains(gap))
            {
                stale.Add($"  {gap} — такой константы в каталоге больше нет");
            }
            else if (daemon.Contains(gap) && panel.Contains(gap))
            {
                stale.Add($"  {gap} — оба конца на месте, разрыв закрыт");
            }
        }

        if (stale.Count > 0)
        {
            Assert.Fail(
                "В списке известных разрывов лежит запись, которой там больше не место." + NL + NL +
                "Список известных разрывов опасен ровно тем, что переживает свою причину: " +
                "запись, оставшаяся после починки, выключает проверку навсегда и делает вид, что " +
                "так и надо. Поэтому он и самоочищающийся." + NL + NL +
                "Что делать: удалите запись из KnownGaps." + NL + NL +
                "Найдено:" + NL + string.Join(NL, stale));
        }

        await Assert.That(stale.Count).IsEqualTo(0);
    }

    // ---- сканирование ---------------------------------------------------------------------

    private static string NL => Environment.NewLine;

    private static IEnumerable<(string Name, string Value)> Constants() =>
        typeof(IpcMessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (field.Name, (string)field.GetRawConstantValue()!));

    /// <summary>
    /// Имена констант каталога, действительно упомянутые в коде этих проектов.
    ///
    /// Комментарии вычищаются: половина каталога перечислена в xmldoc соседних констант и в
    /// <c>&lt;see cref&gt;</c> у обработчиков, так что поиск по сырому тексту засчитал бы живым
    /// конец, которого нет.
    /// </summary>
    private static HashSet<string> ReferencedIn(IReadOnlyList<string> projects)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            foreach (var file in RepositorySources.CSharpFiles(project))
            {
                if (string.Equals(Path.GetFileName(file), CatalogFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var code = RepositorySources.StripCommentsFromCSharp(File.ReadAllText(file));
                foreach (Match match in Reference.Matches(code))
                {
                    found.Add(match.Groups["name"].Value);
                }
            }
        }

        return found;
    }
}
