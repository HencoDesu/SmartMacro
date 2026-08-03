using SmartMacro.Daemon;

namespace SmartMacro.Tests.Daemon;

// Стадия 2A: «Открыть панель» в трее ищет исполняемый файл интерфейса. Чистая логика путей плюс
// подставленная проверка существования файла, так что файловая система не задействована.
//
// Кандидатов два, и они обслуживают разные раскладки. В ПОСТАВКЕ панель лежит в корне, а демон
// в daemon\, то есть искать её надо УРОВНЕМ ВЫШЕ себя (раньше это было «рядом с собой» — общая
// папка). В ДЕРЕВЕ РАЗРАБОТКИ у каждого проекта свой bin\, и там работает подмена сегмента с
// именем проекта; когда-то локатор панели её не знал, а зеркальный DaemonLauncher знал, и из-за
// расхождения «Открыть панель» из трея молча не работало под `dotnet run`. Обе стороны теперь
// ходят в один PeerExecutableLocator, а парные проверки лежат в DaemonLauncherTests.
public class UiExecutableLocatorTests
{
    private const string Deployed = @"C:\apps\SmartMacro\daemon";
    private const string DeployedRoot = @"C:\apps\SmartMacro";
    private const string DevTree = @"D:\repo\src\SmartMacro.Daemon\bin\Debug\net10.0-windows";

    private const string DevTreeSibling =
        @"D:\repo\src\SmartMacro.App\bin\Debug\net10.0-windows\SmartMacro.exe";

    private static readonly string ExpectedPath = Path.Combine(DeployedRoot, "SmartMacro.exe");

    [Test]
    public async Task ProbePaths_LookOneLevelUp_WhereTheShippedPanelLives()
    {
        await Assert.That(UiExecutableLocator.ProbePaths(Deployed)[0]).IsEqualTo(ExpectedPath);
    }

    [Test]
    public async Task ProbePaths_HandleATrailingSeparator()
    {
        // AppContext.BaseDirectory всегда заканчивается разделителем — и это тот самый случай,
        // где он не безобиден: Path.GetDirectoryName на строке с хвостовым слешем отдаёт саму
        // папку вместо родителя, то есть демон искал бы панель у себя и не находил никогда.
        await Assert.That(UiExecutableLocator.ProbePaths(Deployed + Path.DirectorySeparatorChar)[0])
            .IsEqualTo(ExpectedPath);
    }

    [Test]
    public async Task Resolve_WhenPresent_ReturnsTheFullPath()
    {
        string? probed = null;
        var resolved = UiExecutableLocator.Resolve(Deployed, path =>
        {
            probed = path;
            return true;
        });

        await Assert.That(resolved).IsEqualTo(ExpectedPath);
        await Assert.That(probed).IsEqualTo(ExpectedPath);
    }

    [Test]
    public async Task Resolve_WhenMissing_ReturnsNull()
    {
        var resolved = UiExecutableLocator.Resolve(Deployed, _ => false);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task Deployed_DoesNotSearchAnywhereElse()
    {
        // В раскладке поставки кандидат ровно один: ни обхода PATH, ни подъёма по родительским
        // папкам выше первого уровня. Демон, запустивший какой-то посторонний SmartMacro.exe,
        // попавшийся под руку, был бы хуже того, который говорит «не нашли» и называет путь,
        // куда смотрел. Второй кандидат появляется ТОЛЬКО когда в пути виден собственный
        // каталог проекта.
        var probes = new List<string>();
        UiExecutableLocator.Resolve(Deployed, path =>
        {
            probes.Add(path);
            return false;
        });

        await Assert.That(probes).Count().IsEqualTo(1);
    }

    [Test]
    public async Task DevTree_AlsoLooksInTheSiblingProjectsBin()
    {
        var probes = UiExecutableLocator.ProbePaths(DevTree);

        await Assert.That(probes).Count().IsEqualTo(2);
        // Первый кандидат описывает поставку и в дереве разработки просто не существует:
        // панели в bin\Debug\ нет.
        await Assert.That(probes[0])
            .IsEqualTo(@"D:\repo\src\SmartMacro.Daemon\bin\Debug\SmartMacro.exe");
        // Меняется только сегмент с папкой проекта: конфигурация и TFM у обоих проектов
        // одинаковы, поэтому `dotnet run --project src/SmartMacro.Daemon` всё равно находит
        // панель.
        await Assert.That(probes[1]).IsEqualTo(DevTreeSibling);
    }

    [Test]
    public async Task DevTree_ProbePaths_HandleATrailingSeparator()
    {
        var probes = UiExecutableLocator.ProbePaths(DevTree + Path.DirectorySeparatorChar);

        await Assert.That(probes[0])
            .IsEqualTo(@"D:\repo\src\SmartMacro.Daemon\bin\Debug\SmartMacro.exe");
        await Assert.That(probes[1]).IsEqualTo(DevTreeSibling);
    }

    [Test]
    public async Task DevTree_Resolve_ReturnsTheFirstCandidateThatExists()
    {
        // Ровно тот случай, который до этого не работал: рядом с демоном панели нет, она в
        // соседнем bin.
        var resolved = UiExecutableLocator.Resolve(
            DevTree,
            path => path.Contains(@"SmartMacro.App\bin", StringComparison.Ordinal));

        await Assert.That(resolved).IsEqualTo(DevTreeSibling);
    }

    [Test]
    public async Task Resolve_DefaultProbe_UsesTheFilesystem()
    {
        // Без подделок: в настоящей раскладке поставки панель лежит уровнем выше демона.
        var root = Directory.CreateTempSubdirectory("smartmacro-locator-");
        try
        {
            var daemon = root.CreateSubdirectory("daemon");
            await Assert.That(UiExecutableLocator.Resolve(daemon.FullName)).IsNull();

            var panel = Path.Combine(root.FullName, UiExecutableLocator.UiExecutableName);
            await File.WriteAllTextAsync(panel, "not really an exe");

            await Assert.That(UiExecutableLocator.Resolve(daemon.FullName)).IsEqualTo(panel);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UiExecutableName_MatchesTheAppProjectsOutput()
    {
        // Страхует от того, что переименование AssemblyName у SmartMacro.App молча сломает путь
        // запуска из трея, — эта константа и есть единственная связка между двумя проектами.
        // Имя именно SmartMacro.exe: в корне поставки видна одна кнопка, и зовут её как
        // программу, а не как проект.
        await Assert.That(UiExecutableLocator.UiExecutableName).IsEqualTo("SmartMacro.exe");
    }
}
