using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macro;
using SmartMacro.Native;

namespace SmartMacro.Tests;

// W0.1: macro model bridge — ActionsByClass (enum-keyed) became ActionsByTag
// (string-keyed) but OLD macros.json files must keep loading: the enum keys were
// serialized as their names ("Лучник"), which now deserialize as plain tag strings.
// Loading goes through MacroLibrary so the real load path (options, converters,
// polymorphic $type discriminators) is what's under test.
public class MacroJsonCompatTests
{
    // Verbatim shape of a pre-W0.1 macros.json (enum-name keys under "ActionsByClass").
    private const string OldFormatFixture =
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
                ],
                "Жрец": [
                  { "$type": "key", "Key": "F5" }
                ]
              }
            }
          ]
        }
        """;

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task OldEnumKeyedJson_LoadsInto_TagKeyedModel()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "macros.json"), OldFormatFixture);

            using var library = new MacroLibrary(dir, NullLogger<MacroLibrary>.Instance);

            await Assert.That(library.Macros).Count().IsEqualTo(1);
            var macro = library.Macros[0];
            await Assert.That(macro.Name).IsEqualTo("Баг госта");
            await Assert.That(macro.ActionsByTag).Count().IsEqualTo(2);

            var archer = macro.ActionsByTag["Лучник"];
            await Assert.That(archer).Count().IsEqualTo(3);
            await Assert.That(archer[0]).IsEqualTo(new KeyPressAction(VirtualKey.D9));
            await Assert.That(archer[1]).IsEqualTo(new DelayAction(9000));
            await Assert.That(archer[2]).IsEqualTo(new ClickAction(new ScreenPoint(100, 200), true));

            var priest = macro.ActionsByTag["Жрец"];
            await Assert.That(priest).Count().IsEqualTo(1);
            await Assert.That(priest[0]).IsEqualTo(new KeyPressAction(VirtualKey.F5));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TagKeyedModel_RoundTrips_ThroughPersistAndReload()
    {
        var dir = CreateTempDir();
        try
        {
            using (var library = new MacroLibrary(dir, NullLogger<MacroLibrary>.Instance))
            {
                var macro = new Macro.Macro
                {
                    Name = "иммунка",
                    ActionsByTag = new Dictionary<string, List<MacroAction>>
                    {
                        // Free-form tag, not a former enum value — must survive as-is.
                        ["мой-произвольный-тег"] =
                        [
                            new KeyPressAction(VirtualKey.F8),
                            new DelayAction(300),
                        ],
                    },
                };
                await library.ReplaceAsync([macro]);
            }

            using var reloaded = new MacroLibrary(dir, NullLogger<MacroLibrary>.Instance);

            await Assert.That(reloaded.Macros).Count().IsEqualTo(1);
            var roundTripped = reloaded.Macros[0];
            await Assert.That(roundTripped.Name).IsEqualTo("иммунка");
            var actions = roundTripped.ActionsByTag["мой-произвольный-тег"];
            await Assert.That(actions).Count().IsEqualTo(2);
            await Assert.That(actions[0]).IsEqualTo(new KeyPressAction(VirtualKey.F8));
            await Assert.That(actions[1]).IsEqualTo(new DelayAction(300));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PersistedFile_KeepsLegacyPropertyName_ForBackCompat()
    {
        var dir = CreateTempDir();
        try
        {
            using (var library = new MacroLibrary(dir, NullLogger<MacroLibrary>.Instance))
            {
                var macro = new Macro.Macro
                {
                    Name = "m",
                    ActionsByTag = new Dictionary<string, List<MacroAction>>
                    {
                        ["Лучник"] = [new KeyPressAction(VirtualKey.D1)],
                    },
                };
                await library.ReplaceAsync([macro]);
            }

            var json = File.ReadAllText(Path.Combine(dir, "macros.json"));

            // TODO(W0.2): dies with the node-graph macro format. Until then new saves
            // stay readable by anything expecting the pre-tag schema.
            await Assert.That(json).Contains("ActionsByClass");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
