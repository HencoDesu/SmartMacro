using SmartMacro.Daemon;

namespace SmartMacro.Tests.Daemon;

// Stage 2A: the tray's "Открыть панель" resolves the UI executable as a sibling of the
// daemon's own assembly. Pure path logic plus an injected existence probe, so no filesystem
// is involved.
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
        // AppContext.BaseDirectory always ends in a separator, so this is the real shape of
        // the input — Path.Combine must not double it.
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
        // Exactly one candidate: no PATH walk, no parent-directory climb. A daemon that
        // launched some other SmartMacro.App.exe it happened to find would be worse than
        // one that reports "not found" with the path it looked at.
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
        // No fake: an empty temp directory really doesn't contain the UI exe.
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
        // Guards against a rename of SmartMacro.App's AssemblyName silently breaking the
        // tray's launch path — this constant is the only coupling between the two projects.
        await Assert.That(UiExecutableLocator.UiExecutableName).IsEqualTo("SmartMacro.App.exe");
    }
}
