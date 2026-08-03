using SmartMacro.App.Ipc;

namespace SmartMacro.Tests.Ipc;

// Стадия 3: панель вполне может оказаться тем процессом, который пользователь запускает первым,
// а значит, обязана уметь найти и поднять демон — зеркальное отражение UiExecutableLocator из
// трея. Чистая логика путей плюс подставленная проверка существования файла; TryStart (он
// порождает процесс) здесь намеренно не гоняется.
//
// Зеркальность и в кандидатах: панель лежит в корне поставки и ищет демона ВНИЗ, в daemon\, а
// демон ищет панель вверх. Второй кандидат у обоих один и тот же — подмена сегмента с именем
// проекта, дерево разработки.
public class DaemonLauncherTests
{
    private const string Deployed = @"C:\apps\SmartMacro";
    private const string DevTree = @"D:\repo\src\SmartMacro.App\bin\Debug\net10.0-windows";

    [Test]
    public async Task Deployed_TheOnlyCandidateIsTheDaemonSubfolder()
    {
        // Раскладка поставки не имеет права превращаться в поиск по файловой системе: запустить
        // какой-то посторонний SmartMacro.Daemon.exe, попавшийся под руку, хуже, чем сказать
        // «не нашли» и назвать путь.
        await Assert.That(DaemonLauncher.ProbePaths(Deployed))
            .IsEquivalentTo(new[] { Path.Combine(Deployed, "daemon", "SmartMacro.Daemon.exe") });
    }

    [Test]
    public async Task DevTree_AlsoLooksInTheSiblingProjectsBin()
    {
        var probes = DaemonLauncher.ProbePaths(DevTree);

        await Assert.That(probes).Count().IsEqualTo(2);
        // Первый кандидат описывает поставку и в дереве разработки не существует: подпапки
        // daemon\ внутри bin\ панели нет.
        await Assert.That(probes[0]).IsEqualTo(Path.Combine(DevTree, "daemon", "SmartMacro.Daemon.exe"));
        // Меняется только сегмент с папкой проекта: конфигурация и TFM у обоих проектов
        // одинаковы, поэтому `dotnet run --project src/SmartMacro.App` всё равно находит демон.
        await Assert.That(probes[1])
            .IsEqualTo(@"D:\repo\src\SmartMacro.Daemon\bin\Debug\net10.0-windows\SmartMacro.Daemon.exe");
    }

    [Test]
    public async Task ProbePaths_HandleATrailingSeparator()
    {
        // AppContext.BaseDirectory всегда заканчивается разделителем, так что на вход приходит
        // именно такая строка, — Path.Combine не имеет права его удваивать.
        var probes = DaemonLauncher.ProbePaths(DevTree + Path.DirectorySeparatorChar);

        await Assert.That(probes[0]).IsEqualTo(Path.Combine(DevTree, "daemon", "SmartMacro.Daemon.exe"));
        await Assert.That(probes[1])
            .IsEqualTo(@"D:\repo\src\SmartMacro.Daemon\bin\Debug\net10.0-windows\SmartMacro.Daemon.exe");
    }

    [Test]
    public async Task Resolve_ReturnsTheFirstCandidateThatExists()
    {
        var missingFirst = DaemonLauncher.Resolve(
            DevTree,
            path => path.Contains("SmartMacro.Daemon\\bin", StringComparison.Ordinal));

        await Assert.That(missingFirst)
            .IsEqualTo(@"D:\repo\src\SmartMacro.Daemon\bin\Debug\net10.0-windows\SmartMacro.Daemon.exe");
    }

    [Test]
    public async Task Resolve_WhenNothingExists_ReturnsNull()
    {
        await Assert.That(DaemonLauncher.Resolve(Deployed, _ => false)).IsNull();
    }

    [Test]
    public async Task ResolveDirectory_IsTheFolderOfTheFoundExe()
    {
        // Это тот вход, из которого панель считает корень установки в дереве разработки, — то
        // есть промах здесь означает «панель смотрит в чужой macros\».
        await Assert.That(DaemonLauncher.ResolveDirectory(Deployed, _ => true))
            .IsEqualTo(Path.Combine(Deployed, "daemon"));
    }

    [Test]
    public async Task ResolveDirectory_WhenNothingExists_ReturnsNull()
    {
        await Assert.That(DaemonLauncher.ResolveDirectory(Deployed, _ => false)).IsNull();
    }

    [Test]
    public async Task DaemonExecutableName_MatchesTheDaemonProjectsOutput()
    {
        // Единственная связка между выходами сборки двух проектов; переименование AssemblyName
        // демона обязано ломаться здесь, а не во время работы.
        await Assert.That(DaemonLauncher.DaemonExecutableName).IsEqualTo("SmartMacro.Daemon.exe");
    }
}
