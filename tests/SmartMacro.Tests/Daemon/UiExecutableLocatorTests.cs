using SmartMacro.Daemon;

namespace SmartMacro.Tests.Daemon;

// Стадия 2A: «Открыть панель» в трее ищет исполняемый файл интерфейса рядом со сборкой самого
// демона. Чистая логика путей плюс подставленная проверка существования файла, так что файловая
// система не задействована.
public class UiExecutableLocatorTests
{
    private const string BaseDirectory = @"C:\apps\smartmacro";
    private static readonly string ExpectedPath = Path.Combine(BaseDirectory, "SmartMacro.App.exe");

    [Test]
    public async Task ProbePath_IsTheExeNextToTheBaseDirectory()
    {
        await Assert.That(UiExecutableLocator.ProbePath(BaseDirectory)).IsEqualTo(ExpectedPath);
    }

    [Test]
    public async Task ProbePath_HandlesATrailingSeparator()
    {
        // AppContext.BaseDirectory всегда заканчивается разделителем, так что на вход приходит
        // именно такая строка, — Path.Combine не имеет права его удваивать.
        await Assert.That(UiExecutableLocator.ProbePath(BaseDirectory + Path.DirectorySeparatorChar))
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
    public async Task Resolve_DoesNotSearchAnywhereElse()
    {
        // Ровно один кандидат: ни обхода PATH, ни подъёма по родительским папкам. Демон,
        // запустивший какой-то посторонний SmartMacro.App.exe, попавшийся под руку, был бы хуже
        // того, который говорит «не нашли» и называет путь, куда смотрел.
        var probes = new List<string>();
        UiExecutableLocator.Resolve(BaseDirectory, path =>
        {
            probes.Add(path);
            return false;
        });

        await Assert.That(probes).Count().IsEqualTo(1);
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
