using SmartMacro.Daemon;

namespace SmartMacro.Tests.Daemon;

// Стадия 2A: «Открыть панель» в трее ищет исполняемый файл интерфейса рядом со сборкой самого
// демона. Чистая логика путей плюс подставленная проверка существования файла, так что файловая
// система не задействована.
//
// Позже сюда добавился второй кандидат — дерево разработки. Локатор панели умел только «рядом со
// мной», а зеркальный ему DaemonLauncher знал про оба места, и из-за этого расхождения «Открыть
// панель» из трея молча не работало под `dotnet run`. Обе стороны теперь ходят в один
// PeerExecutableLocator, а парные проверки лежат в DaemonLauncherTests.
public class UiExecutableLocatorTests
{
    private const string BaseDirectory = @"C:\apps\smartmacro";
    private const string DevTree = @"D:\repo\src\SmartMacro.Daemon\bin\Debug\net10.0-windows";

    private const string DevTreeSibling =
        @"D:\repo\src\SmartMacro.App\bin\Debug\net10.0-windows\SmartMacro.App.exe";

    private static readonly string ExpectedPath = Path.Combine(BaseDirectory, "SmartMacro.App.exe");

    [Test]
    public async Task ProbePaths_IsTheExeNextToTheBaseDirectory()
    {
        await Assert.That(UiExecutableLocator.ProbePaths(BaseDirectory)[0]).IsEqualTo(ExpectedPath);
    }

    [Test]
    public async Task ProbePaths_HandleATrailingSeparator()
    {
        // AppContext.BaseDirectory всегда заканчивается разделителем, так что на вход приходит
        // именно такая строка, — Path.Combine не имеет права его удваивать.
        await Assert.That(UiExecutableLocator.ProbePaths(BaseDirectory + Path.DirectorySeparatorChar)[0])
            .IsEqualTo(ExpectedPath);
    }

    [Test]
    public async Task Resolve_WhenPresent_ReturnsTheFullPath()
    {
        string? probed = null;
        var resolved = UiExecutableLocator.Resolve(BaseDirectory, path =>
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
        var resolved = UiExecutableLocator.Resolve(BaseDirectory, _ => false);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task Deployed_DoesNotSearchAnywhereElse()
    {
        // В раскладке поставки кандидат ровно один: ни обхода PATH, ни подъёма по родительским
        // папкам. Демон, запустивший какой-то посторонний SmartMacro.App.exe, попавшийся под
        // руку, был бы хуже того, который говорит «не нашли» и называет путь, куда смотрел.
        // Второй кандидат появляется ТОЛЬКО когда в пути виден собственный каталог проекта.
        var probes = new List<string>();
        UiExecutableLocator.Resolve(BaseDirectory, path =>
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
        await Assert.That(probes[0]).IsEqualTo(Path.Combine(DevTree, "SmartMacro.App.exe"));
        // Меняется только сегмент с папкой проекта: конфигурация и TFM у обоих проектов
        // одинаковы, поэтому `dotnet run --project src/SmartMacro.Daemon` всё равно находит
        // панель.
        await Assert.That(probes[1]).IsEqualTo(DevTreeSibling);
    }

    [Test]
    public async Task DevTree_ProbePaths_HandleATrailingSeparator()
    {
        var probes = UiExecutableLocator.ProbePaths(DevTree + Path.DirectorySeparatorChar);

        await Assert.That(probes[0]).IsEqualTo(Path.Combine(DevTree, "SmartMacro.App.exe"));
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
        // Без подделок: во временной пустой папке действительно нет exe с интерфейсом.
        var empty = Directory.CreateTempSubdirectory("smartmacro-locator-");
        try
        {
            await Assert.That(UiExecutableLocator.Resolve(empty.FullName)).IsNull();

            await File.WriteAllTextAsync(
                Path.Combine(empty.FullName, UiExecutableLocator.UiExecutableName),
                "not really an exe");

            await Assert.That(UiExecutableLocator.Resolve(empty.FullName))
                .IsEqualTo(Path.Combine(empty.FullName, UiExecutableLocator.UiExecutableName));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UiExecutableName_MatchesTheAppProjectsOutput()
    {
        // Страхует от того, что переименование AssemblyName у SmartMacro.App молча сломает путь
        // запуска из трея, — эта константа и есть единственная связка между двумя проектами.
        await Assert.That(UiExecutableLocator.UiExecutableName).IsEqualTo("SmartMacro.App.exe");
    }
}
