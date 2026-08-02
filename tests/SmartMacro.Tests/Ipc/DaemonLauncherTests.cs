using SmartMacro.App.Ipc;

namespace SmartMacro.Tests.Ipc;

// Stage 3: the panel can be the process the user starts first, so it has to be able to find
// and launch the daemon — the mirror image of the tray's UiExecutableLocator. Pure path
// logic plus an injected existence probe; TryStart (which spawns a process) is deliberately
// not exercised here.
public class DaemonLauncherTests
{
    private const string Deployed = @"C:\apps\smartmacro";
    private const string DevTree = @"D:\repo\src\SmartMacro.App\bin\Debug\net10.0-windows";

    [Test]
    public async Task Deployed_TheOnlyCandidateIsTheExeNextToUs()
    {
        // Side-by-side in one output directory is the shipped layout, and it must not turn
        // into a filesystem search: launching some other SmartMacro.Daemon.exe we happened
        // to find would be worse than reporting "not found".
        await Assert.That(DaemonLauncher.ProbePaths(Deployed))
            .IsEquivalentTo(new[] { Path.Combine(Deployed, "SmartMacro.Daemon.exe") });
    }

    [Test]
    public async Task DevTree_AlsoLooksInTheSiblingProjectsBin()
    {
        var probes = DaemonLauncher.ProbePaths(DevTree);

        await Assert.That(probes).Count().IsEqualTo(2);
        await Assert.That(probes[0]).IsEqualTo(Path.Combine(DevTree, "SmartMacro.Daemon.exe"));
        // Only the project-folder segment changes: configuration and TFM are identical for
        // both projects, so `dotnet run --project src/SmartMacro.App` still finds the daemon.
        await Assert.That(probes[1])
            .IsEqualTo(@"D:\repo\src\SmartMacro.Daemon\bin\Debug\net10.0-windows\SmartMacro.Daemon.exe");
    }

    [Test]
    public async Task ProbePaths_HandleATrailingSeparator()
    {
        // AppContext.BaseDirectory always ends in one, so this is the real shape of the input.
        var probes = DaemonLauncher.ProbePaths(DevTree + Path.DirectorySeparatorChar);

        await Assert.That(probes[0]).IsEqualTo(Path.Combine(DevTree, "SmartMacro.Daemon.exe"));
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
    public async Task DaemonExecutableName_MatchesTheDaemonProjectsOutput()
    {
        // The only coupling between the two projects' build outputs; a rename of the
        // daemon's AssemblyName has to break here rather than at runtime.
        await Assert.That(DaemonLauncher.DaemonExecutableName).IsEqualTo("SmartMacro.Daemon.exe");
    }
}
