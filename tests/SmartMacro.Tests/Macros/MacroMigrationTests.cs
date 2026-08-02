using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2b: first-run bootstrap of the macro folder — converting a legacy macros.json into
// graphs, and seeding the PW examples when there's nothing to convert.
//
// The fixtures are verbatim pre-W0.2 files (enum-name keys under "ActionsByClass"), so
// this also covers the old-format compatibility the deleted MacroJsonCompatTests used to.
public class MacroMigrationTests
{
    private const string SingleTagFixture =
        """
        {
          "Macros": [
            {
              "Name": "Баг госта",
              "ActionsByClass": {
                "Лучник": [
                  { "$type": "key", "Key": "D9" },
                  { "$type": "delay", "Ms": 9000 },
                  { "$type": "click", "Point": { "X": 100, "Y": 200 }, "DoubleClick": true }
                ]
              }
            }
          ]
        }
        """;

    private const string MultiTagFixture =
        """
        {
          "Macros": [
            {
              "Name": "бурст",
              "ActionsByClass": {
                "Лучник": [ { "$type": "key", "Key": "F5" } ],
                "Жрец": [ { "$type": "key", "Key": "F6" }, { "$type": "delay", "Ms": 300 } ]
              }
            }
          ]
        }
        """;

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MacroGraphStore CreateStore(string baseDirectory, bool seedDefaults = true) =>
        new(baseDirectory, NullLogger<MacroGraphStore>.Instance, seedDefaults);

    [Test]
    public async Task SingleTagMacro_BecomesOneChainGraph_WithTagSelectorOnEveryAction()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "macros.json"), SingleTagFixture);

            // seedDefaults:false isolates the migration result — example seeding is
            // covered separately by MigratedFolder_StillReceivesThePwExamples.
            using var store = CreateStore(dir, seedDefaults: false);

            await Assert.That(store.All).Count().IsEqualTo(1);
            var graph = store.TryGet("Баг госта");
            await Assert.That(graph).IsNotNull();
            await Assert.That(graph!.Triggers).IsEmpty();
            await Assert.That(graph.Nodes).Count().IsEqualTo(3);
            await Assert.That(graph.StartNodeId).IsEqualTo(graph.Nodes[0].Id);

            var key = (KeyPressNode)graph.Nodes[0];
            await Assert.That(key.Key).IsEqualTo(VirtualKey.D9);
            await Assert.That(key.Target!.RequireTags).Contains("Лучник");
            await Assert.That(key.Next).IsEqualTo(graph.Nodes[1].Id);

            var delay = (DelayNode)graph.Nodes[1];
            await Assert.That(delay.Ms).IsEqualTo(9000);
            await Assert.That(delay.Next).IsEqualTo(graph.Nodes[2].Id);

            var click = (ClickNode)graph.Nodes[2];
            await Assert.That(click.Point).IsEqualTo(new ScreenPoint(100, 200));
            await Assert.That(click.DoubleClick).IsTrue();
            await Assert.That(click.Target!.RequireTags).Contains("Лучник");
            // Last node in the chain ends the run.
            await Assert.That(click.Next).IsNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task MultiTagMacro_BecomesDispatcher_PlusOneGraphPerTag()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "macros.json"), MultiTagFixture);

            using var store = CreateStore(dir, seedDefaults: false);

            await Assert.That(store.All).Count().IsEqualTo(3);

            var dispatcher = store.TryGet("бурст");
            await Assert.That(dispatcher).IsNotNull();
            var calls = dispatcher!.Nodes.OfType<RunMacroNode>().ToList();
            await Assert.That(calls).Count().IsEqualTo(2);
            await Assert.That(calls.All(call => call.Await)).IsTrue();
            // Each branch is selected by tag, and the chain runs them one after another.
            await Assert.That(calls[0].Target!.RequireTags).Contains("Лучник");
            await Assert.That(calls[0].MacroName).IsEqualTo("бурст-Лучник");
            await Assert.That(calls[0].Next).IsEqualTo(calls[1].Id);
            await Assert.That(calls[1].Target!.RequireTags).Contains("Жрец");
            await Assert.That(calls[1].Next).IsNull();

            // Per-tag graphs are TARGETLESS: their context window comes from the
            // dispatcher's selector fan-out.
            var archer = store.TryGet("бурст-Лучник");
            await Assert.That(archer).IsNotNull();
            await Assert.That(((KeyPressNode)archer!.Nodes[0]).Key).IsEqualTo(VirtualKey.F5);
            await Assert.That(((KeyPressNode)archer.Nodes[0]).Target).IsNull();

            var priest = store.TryGet("бурст-Жрец");
            await Assert.That(priest).IsNotNull();
            await Assert.That(priest!.Nodes).Count().IsEqualTo(2);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Migration_RenamesLegacyFile_AndDoesNotRunTwice()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "macros.json"), SingleTagFixture);

            using (var store = CreateStore(dir, seedDefaults: false))
            {
                await Assert.That(store.All).Count().IsEqualTo(1);
            }

            await Assert.That(File.Exists(Path.Combine(dir, "macros.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "macros.json.migrated"))).IsTrue();

            // Deleting everything the migration produced must NOT resurrect it on the
            // next start — the folder existing is what marks migration as done.
            File.Delete(Path.Combine(dir, "macros", "Баг госта.json"));

            using var second = CreateStore(dir, seedDefaults: false);
            await Assert.That(second.All).IsEmpty();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task EmptyFolder_SeedsThePwExampleSet()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);

            var names = store.All.Select(graph => graph.Name).ToList();
            await Assert.That(names).Contains("pw-boot");
            await Assert.That(names).Contains("pw-immunity");
            await Assert.That(names).Contains("pw-assist");
            await Assert.That(names).Contains("pw-cursor-click");
            await Assert.That(names).Contains("pw-identify");
            await Assert.That(names).Contains("pw-identify-one");

            // Seeding writes real files, so a restart picks them up rather than re-seeding.
            await Assert.That(File.Exists(Path.Combine(dir, "macros", "pw-boot.json"))).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Regression: seeding used to be gated on "folder is empty", which silently skipped
    // the examples for every upgrading user — migration fills the folder with their own
    // macros first, and the pw-* examples are the only remaining implementation of the
    // built-in broadcasts that the same upgrade deletes.
    [Test]
    public async Task MigratedFolder_StillReceivesThePwExamples()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "macros.json"), SingleTagFixture);

            using var store = CreateStore(dir);

            var names = store.All.Select(graph => graph.Name).ToList();
            await Assert.That(names).Contains("pw-immunity");
            await Assert.That(names).Contains("Баг госта");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task DeletedExample_StaysDeletedAcrossRestarts()
    {
        var dir = CreateTempDir();
        try
        {
            using (var first = CreateStore(dir))
            {
                await Assert.That(first.All.Any(g => g.Name == "pw-immunity")).IsTrue();
            }

            File.Delete(Path.Combine(dir, "macros", "pw-immunity.json"));

            using var second = CreateStore(dir);
            await Assert.That(second.All.Any(g => g.Name == "pw-immunity")).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task DefaultExamples_AreValid_AndSurviveAJsonRoundTrip()
    {
        foreach (var graph in DefaultMacroGraphs.Build())
        {
            var roundTripped = MacroGraphJson.Deserialize(MacroGraphJson.Serialize(graph));
            await Assert.That(roundTripped.Name).IsEqualTo(graph.Name);
            await Assert.That(roundTripped.Nodes).Count().IsEqualTo(graph.Nodes.Count);
            await Assert.That(roundTripped.Triggers).Count().IsEqualTo(graph.Triggers.Count);

            var errors = MacroGraphValidator.Validate(roundTripped)
                .Where(issue => issue.Severity == ValidationSeverity.Error)
                .Select(issue => $"{graph.Name}/{issue.NodeId}: {issue.Message}")
                .ToList();
            await Assert.That(errors).IsEmpty();
        }
    }

    [Test]
    public async Task EmptyLegacyMacros_AreSkipped_NotTurnedIntoStartNodelessGraphs()
    {
        const string fixture =
            """
            { "Macros": [ { "Name": "пусто", "ActionsByClass": { "Лучник": [] } } ] }
            """;

        var result = LegacyMacroMigration.Convert(fixture);

        await Assert.That(result.Graphs).IsEmpty();
        await Assert.That(result.SkippedMacros).IsEqualTo(1);
    }

    [Test]
    public async Task LegacyMacroHotkeys_BecomeTriggersOnTheirMacro()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "macros.json"), MultiTagFixture);
            File.WriteAllText(Path.Combine(dir, "hotkeys.json"),
                """
                {
                  "Bindings": [
                    { "Trigger": "BroadcastImmunity", "Modifiers": "None", "Key": 0, "MouseButton": "Middle" }
                  ],
                  "MacroBindings": [
                    { "MacroName": "бурст", "Modifiers": "None", "Key": "F21", "MouseButton": "None" },
                    { "MacroName": "макрос которого нет", "Modifiers": "None", "Key": "F24", "MouseButton": "None" }
                  ]
                }
                """);

            using var store = CreateStore(dir);

            // The chord now lives in the macro it starts — that's the whole point of
            // dropping hotkeys.json.
            var dispatcher = store.TryGet("бурст");
            await Assert.That(dispatcher).IsNotNull();
            var trigger = dispatcher!.Triggers.OfType<HotkeyTrigger>().Single();
            await Assert.That(trigger.Key).IsEqualTo(VirtualKey.F21);
            await Assert.That(trigger.Modifiers).IsEqualTo(HotkeyModifiers.None);

            // Per-tag children stay trigger-less: only the dispatcher is user-facing.
            await Assert.That(store.TryGet("бурст-Лучник")!.Triggers).IsEmpty();

            await Assert.That(File.Exists(Path.Combine(dir, "hotkeys.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "hotkeys.json.migrated"))).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task BroadcastActionHotkeys_AreReportedAsOrphaned_NotAttachedAnywhere()
    {
        const string hotkeys =
            """
            {
              "Bindings": [
                { "Trigger": "BroadcastImmunity", "Modifiers": "None", "Key": 0, "MouseButton": "Middle" },
                { "Trigger": "BroadcastIdentify", "Modifiers": "None", "Key": "F20", "MouseButton": "None" }
              ],
              "MacroBindings": [
                { "MacroName": "Баг госта", "Modifiers": "None", "Key": "F21", "MouseButton": "None" }
              ]
            }
            """;

        var result = LegacyMacroMigration.Convert(SingleTagFixture, hotkeys);

        await Assert.That(result.AttachedHotkeys).IsEqualTo(1);
        // The two broadcast chords have no destination — those behaviors are pw-* examples now.
        await Assert.That(result.OrphanedHotkeys).IsEqualTo(2);
        await Assert.That(result.Graphs.Single().Triggers).Count().IsEqualTo(1);
    }

    [Test]
    public async Task BrokenHotkeysFile_DoesNotSinkTheMacroMigration()
    {
        var result = LegacyMacroMigration.Convert(SingleTagFixture, "{ not json at all ");

        await Assert.That(result.Graphs).Count().IsEqualTo(1);
        await Assert.That(result.Graphs[0].Triggers).IsEmpty();
    }
}
