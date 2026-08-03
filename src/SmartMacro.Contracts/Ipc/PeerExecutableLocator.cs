namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// Где лежит исполняемый файл ВТОРОГО процесса пары. Тот же довод, что и у
/// <see cref="IpcPipe"/>: знать это нужно обоим концам, а другой общей сборки у них нет.
/// Демон запускает панель по пункту трея «Открыть панель», панель поднимает демона, если труба
/// молчит, — и это один и тот же поиск, только с зеркальными именами.
///
/// Держать его в одном месте не косметика. Раньше локатор панели умел только «рядом со мной», а
/// пусковик демона знал ещё и про дерево разработки, — расхождение, из-за которого «Открыть
/// панель» из трея молча не работало у всех, кто гоняет проект через <c>dotnet run</c>.
///
/// Кандидатов ровно два, и они обслуживают РАЗНЫЕ раскладки, а не подстраховывают друг друга:
///
/// <list type="table">
///   <listheader><term>кандидат</term><description>раскладка</description></listheader>
///   <item>
///     <term>соседняя папка поставки</term>
///     <description>
///       панель ищет демона в <c>daemon\</c>, демон панель — уровнем выше (см.
///       <see cref="InstallationLayout"/>); у пользователя попадаем с первой попытки
///     </description>
///   </item>
///   <item>
///     <term>подмена сегмента <c>SmartMacro.App</c> ⇄ <c>SmartMacro.Daemon</c></term>
///     <description>дерево разработки, где у каждого проекта свой <c>bin\Debug\net10.0-windows\</c></description>
///   </item>
/// </list>
///
/// Кандидата «рядом со мной» здесь больше нет: с тех пор как панель переехала в корень поставки,
/// а демон в подпапку, соседями по одной папке два exe не бывают ни в одной раскладке. Первый
/// кандидат подставляется вызывающим — папкой, а не флагом, — потому что направление у двух
/// локаторов разное (вниз и вверх), и знает о нём тот, у кого оно своё.
///
/// Тут только арифметика над путями: файловую систему не трогаем, проба на существование
/// приходит параметром (и это не только ради тестов — в Contracts по жёсткому правилу не
/// заезжает файловый ввод-вывод, см. комментарий в SmartMacro.Contracts.csproj).
/// </summary>
public static class PeerExecutableLocator
{
    /// <summary>
    /// Пути, которые стоит проверить, — по порядку, от самого достоверного. Выставлено наружу,
    /// чтобы неудачу можно было записать в лог вместе с тем, куда на самом деле смотрели:
    /// «не найдено» без пути — бесполезная диагностика.
    /// </summary>
    /// <param name="baseDirectory">Каталог, откуда ищем, — обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="shippedPeerDirectory">
    /// Где напарник лежит В ПОСТАВКЕ: для панели — <c>{своя папка}\daemon</c>, для демона — папка
    /// уровнем выше своей. Проверяется всегда, в том числе в дереве разработки, где его просто
    /// не существует и он уступает второму кандидату.
    /// </param>
    /// <param name="executableName">Имя файла искомого процесса, например <c>SmartMacro.Daemon.exe</c>.</param>
    /// <param name="ownProjectFolder">Имя папки СВОЕГО проекта, например <c>SmartMacro.App</c>.</param>
    /// <param name="peerProjectFolder">Имя папки проекта-напарника, например <c>SmartMacro.Daemon</c>.</param>
    public static IReadOnlyList<string> ProbePaths(
        string baseDirectory,
        string shippedPeerDirectory,
        string executableName,
        string ownProjectFolder,
        string peerProjectFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(shippedPeerDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownProjectFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerProjectFolder);

        // КАНДИДАТ 1 — раскладка ПОСТАВКИ. Идёт первым и проверяется всегда: на машине
        // пользователя это единственный случай, и попадает он с первой попытки.
        var candidates = new List<string>(2) { Path.Combine(shippedPeerDirectory, executableName) };

        if (SwapProjectFolder(baseDirectory, ownProjectFolder, peerProjectFolder) is { } sibling)
        {
            candidates.Add(Path.Combine(sibling, executableName));
        }

        return candidates;
    }

    /// <summary>
    /// Первый из <see cref="ProbePaths"/>, который существует, или <c>null</c>, если ни одного.
    /// </summary>
    /// <param name="baseDirectory">Каталог, откуда ищем, — обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="shippedPeerDirectory">Где напарник лежит в поставке; см. <see cref="ProbePaths"/>.</param>
    /// <param name="executableName">Имя файла искомого процесса, например <c>SmartMacro.Daemon.exe</c>.</param>
    /// <param name="ownProjectFolder">Имя папки СВОЕГО проекта, например <c>SmartMacro.App</c>.</param>
    /// <param name="peerProjectFolder">Имя папки проекта-напарника, например <c>SmartMacro.Daemon</c>.</param>
    /// <param name="fileExists">
    /// Проба на существование. Параметр обязательный: в Contracts не заезжает файловый
    /// ввод-вывод, поэтому <see cref="File.Exists(string)"/> подставляет вызывающий.
    /// </param>
    public static string? Resolve(
        string baseDirectory,
        string shippedPeerDirectory,
        string executableName,
        string ownProjectFolder,
        string peerProjectFolder,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        return ProbePaths(baseDirectory, shippedPeerDirectory, executableName, ownProjectFolder, peerProjectFolder)
            .FirstOrDefault(fileExists);
    }

    /// <summary>
    /// КАНДИДАТ 2 — раскладка РАЗРАБОТКИ: у <c>.../src/SmartMacro.App/bin/Debug/net10.0-windows/</c>
    /// есть близнец на уровень выше по дереву проектов. Подменить имя папки проекта достаточно —
    /// сегменты конфигурации и TFM у обоих проектов одинаковые. <c>null</c>, если своей папки
    /// проекта в пути нет, — обычная раскладка поставки, второго кандидата у неё не бывает.
    ///
    /// Если установочный каталог сам называется как проект (<c>C:\Program Files\SmartMacro.Daemon\</c>),
    /// второй кандидат появится и там. Это безобидно: он ведёт в несуществующую папку, проверяется
    /// вторым и попадает разве что в строку лога «искали здесь».
    /// </summary>
    private static string? SwapProjectFolder(string baseDirectory, string ownProjectFolder, string peerProjectFolder)
    {
        var normalized = baseDirectory.Replace('/', Path.DirectorySeparatorChar);
        var needle = $"{Path.DirectorySeparatorChar}{ownProjectFolder}{Path.DirectorySeparatorChar}";
        var index = normalized.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        return string.Concat(
            normalized.AsSpan(0, index + 1),
            peerProjectFolder,
            normalized.AsSpan(index + needle.Length - 1));
    }
}
