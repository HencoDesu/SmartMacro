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
/// «рядом со мной» — это поставка, где оба exe публикуются в одну папку (build/portable.proj),
/// и там второй кандидат не понадобится никогда; подмена сегмента с именем проекта — это дерево
/// разработки, где у каждого проекта свой <c>bin\Debug\net10.0-windows\</c> и соседа рядом нет
/// по определению. Порядок соответствует: у пользователя попадаем с первой попытки, лишняя
/// проверка достаётся разработчику.
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
    /// <param name="executableName">Имя файла искомого процесса, например <c>SmartMacro.App.exe</c>.</param>
    /// <param name="ownProjectFolder">Имя папки СВОЕГО проекта, например <c>SmartMacro.Daemon</c>.</param>
    /// <param name="peerProjectFolder">Имя папки проекта-напарника, например <c>SmartMacro.App</c>.</param>
    public static IReadOnlyList<string> ProbePaths(
        string baseDirectory,
        string executableName,
        string ownProjectFolder,
        string peerProjectFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownProjectFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerProjectFolder);

        // КАНДИДАТ 1 — раскладка ПОСТАВКИ: оба exe в одной папке, потому что публикуются они
        // туда вместе (build/portable.proj). Идёт первым и проверяется всегда: на машине
        // пользователя это единственный случай, и попадает он с первой попытки.
        var candidates = new List<string>(2) { Path.Combine(baseDirectory, executableName) };

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
    /// <param name="executableName">Имя файла искомого процесса, например <c>SmartMacro.App.exe</c>.</param>
    /// <param name="ownProjectFolder">Имя папки СВОЕГО проекта, например <c>SmartMacro.Daemon</c>.</param>
    /// <param name="peerProjectFolder">Имя папки проекта-напарника, например <c>SmartMacro.App</c>.</param>
    /// <param name="fileExists">
    /// Проба на существование. Параметр обязательный: в Contracts не заезжает файловый
    /// ввод-вывод, поэтому <see cref="File.Exists(string)"/> подставляет вызывающий.
    /// </param>
    public static string? Resolve(
        string baseDirectory,
        string executableName,
        string ownProjectFolder,
        string peerProjectFolder,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        return ProbePaths(baseDirectory, executableName, ownProjectFolder, peerProjectFolder)
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
