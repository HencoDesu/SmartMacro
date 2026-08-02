using System.Text.Json;
using System.Text.Json.Serialization;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// One-time conversion of the pre-W0.2 <c>macros.json</c> (a single file holding named
/// macros, each a per-tag dictionary of flat action lists) into node graphs.
///
/// Shape mapping:
///   * ONE tag block → a single chain graph named after the macro. Every action node
///     carries <c>Target = RequireTags:[tag]</c>, which reproduces the legacy broadcast
///     semantics ("every window carrying this tag runs these actions") and keeps the
///     graph runnable from a hotkey or the UI, neither of which supplies a context window.
///   * MANY tag blocks → a dispatcher graph named after the macro: a chain of
///     <c>RunMacroNode</c>s, one per tag, each selecting <c>RequireTags:[tag]</c> and
///     pointing at a generated <c>{macro}-{tag}</c> graph. The per-tag graphs hold the
///     original chain with TARGETLESS nodes, because the dispatcher's selector fan-out
///     hands each sub-run its own context window.
///
/// Hotkeys used to live in a separate <c>hotkeys.json</c>. Its <c>MacroBindings</c>
/// (macro name → chord) become <see cref="HotkeyTrigger"/>s on the matching migrated
/// graph, which is exactly the consolidation this wave is about: the binding now lives in
/// the macro it starts. Its <c>Bindings</c> (chords for the hard-coded broadcast actions)
/// have no destination — those behaviors are the <c>pw-*</c> example macros now — so they
/// are counted and reported rather than silently dropped.
/// </summary>
public static class LegacyMacroMigration
{
    /// <summary>File name of the legacy library, relative to the app directory.</summary>
    public const string LegacyFileName = "macros.json";

    /// <summary>File name of the legacy hotkey config, relative to the app directory.</summary>
    public const string LegacyHotkeysFileName = "hotkeys.json";

    /// <summary>Suffix appended to a legacy file once it has been migrated.</summary>
    public const string MigratedSuffix = ".migrated";

    private static readonly JsonSerializerOptions LegacyOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Outcome of one migration pass.</summary>
    /// <param name="Graphs">Graphs to write: each dispatcher followed by its per-tag children.</param>
    /// <param name="SkippedMacros">Legacy macros dropped because they had no runnable actions.</param>
    /// <param name="AttachedHotkeys">Legacy macro hotkeys turned into triggers on a graph.</param>
    /// <param name="OrphanedHotkeys">Legacy hotkeys with nowhere to go (broadcast-action chords, or names with no matching macro).</param>
    public sealed record Result(
        List<MacroGraph> Graphs,
        int SkippedMacros,
        int AttachedHotkeys,
        int OrphanedHotkeys);

    /// <summary>
    /// Converts the contents of a legacy <c>macros.json</c> — and, when supplied, the
    /// macro bindings of a legacy <c>hotkeys.json</c> — into graphs.
    /// </summary>
    /// <param name="json">Raw legacy <c>macros.json</c> contents.</param>
    /// <param name="hotkeysJson">Raw legacy <c>hotkeys.json</c> contents, or <c>null</c> when absent/unreadable.</param>
    /// <exception cref="JsonException">The macro document isn't a legacy macro file.</exception>
    public static Result Convert(string json, string? hotkeysJson = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        var file = JsonSerializer.Deserialize<LegacyFile>(json, LegacyOptions)
                   ?? throw new JsonException("Legacy macro file is 'null'.");

        var result = new List<MacroGraph>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedMacros = 0;

        foreach (var macro in file.Macros)
        {
            var name = Sanitize(macro.Name);
            if (name.Length == 0)
            {
                skippedMacros++;
                continue;
            }

            // Only tag blocks that actually do something survive — an empty list would
            // produce a graph with no start node.
            var blocks = macro.ActionsByTag
                .Where(pair => pair.Value is { Count: > 0 })
                .ToList();
            if (blocks.Count == 0)
            {
                skippedMacros++;
                continue;
            }

            name = Deduplicate(name, usedNames);

            if (blocks.Count == 1)
            {
                var (tag, actions) = (blocks[0].Key, blocks[0].Value);
                result.Add(BuildChain(name, actions, new TargetSelector { RequireTags = [tag] }));
                continue;
            }

            var dispatcherNodes = new List<MacroNode>(blocks.Count);
            var children = new List<MacroGraph>(blocks.Count);
            for (var i = 0; i < blocks.Count; i++)
            {
                var (tag, actions) = (blocks[i].Key, blocks[i].Value);
                var childName = Deduplicate(Sanitize($"{name}-{tag}"), usedNames);
                children.Add(BuildChain(childName, actions, target: null));
                dispatcherNodes.Add(new RunMacroNode
                {
                    Id = NodeId(i),
                    MacroName = childName,
                    Target = new TargetSelector { RequireTags = [tag] },
                    Await = true,
                    Next = i + 1 < blocks.Count ? NodeId(i + 1) : null,
                });
            }

            result.Add(new MacroGraph
            {
                Name = name,
                StartNodeId = dispatcherNodes[0].Id,
                Nodes = dispatcherNodes,
            });
            result.AddRange(children);
        }

        var (attached, orphaned) = AttachLegacyHotkeys(result, hotkeysJson);
        return new Result(result, skippedMacros, attached, orphaned);
    }

    // hotkeys.json → triggers. Only MacroBindings have a destination: they name a macro,
    // and that macro is now the thing that owns its chord. The other list bound the
    // hard-coded broadcast actions, which are example macros now — nothing to attach them
    // to, so they're counted for the caller to report.
    private static (int Attached, int Orphaned) AttachLegacyHotkeys(List<MacroGraph> graphs, string? hotkeysJson)
    {
        if (string.IsNullOrWhiteSpace(hotkeysJson))
        {
            return (0, 0);
        }

        LegacyHotkeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LegacyHotkeyFile>(hotkeysJson, LegacyOptions);
        }
        catch (JsonException)
        {
            // A broken hotkey file must not sink the macro migration.
            return (0, 0);
        }
        if (file is null)
        {
            return (0, 0);
        }

        var byName = graphs.ToDictionary(graph => graph.Name, StringComparer.Ordinal);
        var attached = 0;
        var orphaned = file.Bindings.Count;

        foreach (var binding in file.MacroBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.MacroName)
                || !byName.TryGetValue(Sanitize(binding.MacroName), out var graph))
            {
                orphaned++;
                continue;
            }
            graph.Triggers.Add(new HotkeyTrigger(binding.Modifiers, binding.Key, binding.MouseButton));
            attached++;
        }

        return (attached, orphaned);
    }

    // Flat action list → linear node chain. `target` is stamped on every action node that
    // supports one; DelayNode has no target (the pause is global to the run).
    private static MacroGraph BuildChain(string name, List<LegacyAction> actions, TargetSelector? target)
    {
        var nodes = new List<MacroNode>(actions.Count);
        for (var i = 0; i < actions.Count; i++)
        {
            var id = NodeId(i);
            var next = i + 1 < actions.Count ? NodeId(i + 1) : null;
            nodes.Add(actions[i] switch
            {
                LegacyKeyPressAction key => new KeyPressNode { Id = id, Key = key.Key, Target = target, Next = next },
                LegacyDelayAction delay => new DelayNode { Id = id, Ms = delay.Ms, Next = next },
                LegacyClickAction click => new ClickNode
                {
                    Id = id,
                    Point = click.Point,
                    DoubleClick = click.DoubleClick,
                    Target = target,
                    Next = next,
                },
                var other => throw new JsonException($"Unsupported legacy action type {other.GetType().Name}."),
            });
        }

        return new MacroGraph { Name = name, StartNodeId = nodes[0].Id, Nodes = nodes };
    }

    private static string NodeId(int index) => $"n{index}";

    // Macro names become file names; legacy names were free-form.
    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }
        var chars = name.Trim().ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        return new string(chars).TrimEnd('.', ' ');
    }

    private static string Deduplicate(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name}-{suffix}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    // ---- legacy DTOs ---------------------------------------------------------------
    // Read-only mirrors of the deleted SmartMacro.Macro model. They live here (rather
    // than being kept alive in the domain) so the legacy shape survives exactly as long
    // as the migration needs it.

    private sealed class LegacyFile
    {
        public List<LegacyMacro> Macros { get; init; } = [];
    }

    private sealed class LegacyMacro
    {
        public string? Name { get; init; }

        /// <summary>Tag → actions. The property name predates tags (keys were class-enum names).</summary>
        [JsonPropertyName("ActionsByClass")]
        public Dictionary<string, List<LegacyAction>> ActionsByTag { get; init; } = [];
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(LegacyKeyPressAction), typeDiscriminator: "key")]
    [JsonDerivedType(typeof(LegacyDelayAction), typeDiscriminator: "delay")]
    [JsonDerivedType(typeof(LegacyClickAction), typeDiscriminator: "click")]
    private abstract record LegacyAction;

    private sealed record LegacyKeyPressAction(VirtualKey Key) : LegacyAction;

    private sealed record LegacyDelayAction(int Ms) : LegacyAction;

    private sealed record LegacyClickAction(ScreenPoint Point, bool DoubleClick) : LegacyAction;

    private sealed class LegacyHotkeyFile
    {
        /// <summary>Chords for the hard-coded broadcast actions. No migration destination.</summary>
        public List<LegacyBinding> Bindings { get; init; } = [];

        /// <summary>Chords bound to a macro by name — these become the macro's triggers.</summary>
        public List<LegacyMacroBinding> MacroBindings { get; init; } = [];
    }

    private sealed class LegacyBinding
    {
        public string? Trigger { get; init; }
    }

    private sealed class LegacyMacroBinding
    {
        public string? MacroName { get; init; }
        public HotkeyModifiers Modifiers { get; init; }
        public VirtualKey Key { get; init; }
        public MouseButton MouseButton { get; init; } = MouseButton.None;
    }
}
