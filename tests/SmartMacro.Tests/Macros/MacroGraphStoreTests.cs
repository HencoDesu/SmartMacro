using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2b: the folder-backed macro library — CRUD round-trip, resilience to a hand-broken
// file, name validation, and the watcher's debounce / own-write suppression.
//
// Watcher tests are [NotInParallel] and use real temp folders: FileSystemWatcher is the
// one thing here we can't fake without testing our own mock.
public class MacroGraphStoreTests
{
    private static MacroGraph Chain(string name, params MacroNode[] nodes) =>
        new() { Name = name, StartNodeId = nodes[0].Id, Nodes = [.. nodes] };

    private static MacroGraph SimpleMacro(string name, VirtualKey key = VirtualKey.F1) =>
        Chain(name, new KeyPressNode { Id = "n0", Key = key, Target = new TargetSelector() });

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MacroGraphStore CreateStore(string baseDirectory) =>
        new(baseDirectory, NullLogger<MacroGraphStore>.Instance, seedDefaults: false);

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Watcher may still hold the folder momentarily; temp cleanup isn't the assertion.
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds or the budget runs out.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
    }

    [Test]
    public async Task Save_WritesOneFilePerGraph_AndRoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            using (var store = CreateStore(dir))
            {
                await store.SaveAsync(SimpleMacro("иммунка", VirtualKey.F8));
                await store.SaveAsync(SimpleMacro("ассист", VirtualKey.F2));

                await Assert.That(store.All).Count().IsEqualTo(2);
                await Assert.That(File.Exists(Path.Combine(dir, "macros", "иммунка.json"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(dir, "macros", "ассист.json"))).IsTrue();
            }

            // Fresh store = a real load from disk, not the in-memory snapshot.
            using var reloaded = CreateStore(dir);
            await Assert.That(reloaded.All).Count().IsEqualTo(2);
            var macro = reloaded.TryGet("иммунка");
            await Assert.That(macro).IsNotNull();
            await Assert.That(macro!.Nodes).Count().IsEqualTo(1);
            await Assert.That(((KeyPressNode)macro.Nodes[0]).Key).IsEqualTo(VirtualKey.F8);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Load_SkipsBrokenFile_ButKeepsTheRest()
    {
        var dir = CreateTempDir();
        try
        {
            var macrosDir = Path.Combine(dir, "macros");
            Directory.CreateDirectory(macrosDir);
            File.WriteAllText(Path.Combine(macrosDir, "good.json"), MacroGraphJson.Serialize(SimpleMacro("good")));
            File.WriteAllText(Path.Combine(macrosDir, "broken.json"), "{ this is not json ");
            File.WriteAllText(Path.Combine(macrosDir, "unknown-node.json"),
                """{"Name":"unknown-node","StartNodeId":"a","Nodes":[{"$type":"teleport","Id":"a"}]}""");

            using var store = CreateStore(dir);

            await Assert.That(store.All).Count().IsEqualTo(1);
            await Assert.That(store.All[0].Name).IsEqualTo("good");
            await Assert.That(store.TryGet("broken")).IsNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Load_FileNameWins_OverTheNameFieldInJson()
    {
        var dir = CreateTempDir();
        try
        {
            var macrosDir = Path.Combine(dir, "macros");
            Directory.CreateDirectory(macrosDir);
            // Simulates the user renaming the file: the stem is the identity.
            File.WriteAllText(Path.Combine(macrosDir, "renamed.json"), MacroGraphJson.Serialize(SimpleMacro("old-name")));

            using var store = CreateStore(dir);

            await Assert.That(store.TryGet("renamed")).IsNotNull();
            await Assert.That(store.TryGet("old-name")).IsNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Delete_RemovesFileAndEntry_UnknownNameIsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("gone"));

            var deleted = await store.DeleteAsync("gone");
            var missing = await store.DeleteAsync("never-existed");

            await Assert.That(deleted).IsTrue();
            await Assert.That(missing).IsFalse();
            await Assert.That(store.All).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(dir, "macros", "gone.json"))).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_RejectsNamesThatAreNotUsableFileNames()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);

            await Assert.That(async () => await store.SaveAsync(SimpleMacro("bad/name")))
                .Throws<ArgumentException>();
            await Assert.That(store.All).IsEmpty();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    [Arguments("normal-name", true)]
    [Arguments("кириллица тоже", true)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("has/slash", false)]
    [Arguments("has:colon", false)]
    [Arguments("trailing.", false)]
    [Arguments("CON", false)]
    [Arguments("COM1", false)]
    public async Task ValidateName_MatchesNtfsRules(string name, bool expectedValid)
    {
        var error = MacroGraphStore.ValidateName(name);
        await Assert.That(error is null).IsEqualTo(expectedValid);
    }

    [Test]
    [NotInParallel]
    public async Task Watcher_SuppressesOurOwnWrites()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            var events = 0;
            store.MacrosChanged += _ => Interlocked.Increment(ref events);

            await store.SaveAsync(SimpleMacro("mine"));

            // SaveAsync raises exactly one event; the watcher event its own write triggers
            // must be swallowed. Wait past the 300ms debounce plus slack.
            await Task.Delay(900);
            await Assert.That(Volatile.Read(ref events)).IsEqualTo(1);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Watcher_PicksUpExternalEdits_Debounced()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            var events = 0;
            store.MacrosChanged += _ => Interlocked.Increment(ref events);

            var macrosDir = Path.Combine(dir, "macros");
            var path = Path.Combine(macrosDir, "external.json");

            // Someone edits the folder behind our back (text editor, git checkout).
            // A burst of writes must collapse into a single reload.
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllText(path, MacroGraphJson.Serialize(SimpleMacro("external", VirtualKey.F5)));
                await Task.Delay(30);
            }

            var reloaded = await WaitUntilAsync(() => store.TryGet("external") is not null);

            await Assert.That(reloaded).IsTrue();
            await Assert.That(Volatile.Read(ref events)).IsEqualTo(1);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Dispose_IsIdempotent_EvenAfterTheWatcherArmedAReload()
    {
        // Regression (found running the stage 2B daemon): the store is registered twice in
        // DI — as itself and as IMacroGraphResolver through a factory — so the scope tracks
        // ONE instance in its disposable list TWICE and calls Dispose twice on shutdown. The
        // second call used to Cancel an already-disposed CancellationTokenSource and take
        // host teardown down with it ("terminated unexpectedly", non-zero exit code).
        //
        // It only reproduces once a watcher event has armed a reload — with a never-touched
        // folder the field is null and the double dispose is silently harmless, which is why
        // it went unnoticed until an external edit happened in the same session.
        var dir = CreateTempDir();
        try
        {
            var store = CreateStore(dir);
            var changed = 0;
            store.MacrosChanged += _ => Interlocked.Increment(ref changed);

            File.WriteAllText(
                Path.Combine(dir, "macros", "external.json"),
                MacroGraphJson.Serialize(SimpleMacro("external")));
            var armed = await WaitUntilAsync(() => Volatile.Read(ref changed) > 0);
            await Assert.That(armed).IsTrue();

            store.Dispose();
            await Assert.That(store.Dispose).ThrowsNothing();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
