using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Hotkeys;

// JSON-backed store for hotkey bindings. Two collections:
//   * Bindings — fixed OrchestratorTrigger → key/mouse (BroadcastImmunity, etc.)
//   * MacroBindings — user-defined macro name → key/mouse (RunMacro by name)
//
// File layout:
//   <BaseDirectory>/hotkeys.json
//   {
//     "Bindings":      [ { "Trigger": "...", "Modifiers": "...", "Key": "...", "MouseButton": "..." } ],
//     "MacroBindings": [ { "MacroName": "...", "Modifiers": "...", "Key": "...", "MouseButton": "..." } ]
//   }
//
// On missing file → fall back to HotkeyOptions defaults for triggers; macros start
// empty. On every ReplaceAsync we persist BOTH lists and raise BindingsChanged so
// HotkeyListener can re-register against Win32.
//
// Concurrency: ReplaceAsync serialised by SemaphoreSlim. Reads grab immutable snapshots.
public sealed partial class HotkeyConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<HotkeyConfigStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ImmutableList<HotkeyBinding> _bindings;
    private ImmutableList<MacroHotkeyBinding> _macroBindings;

    /// <summary>
    /// Raised after a successful <see cref="ReplaceAsync"/>. Subscribers (HotkeyListener)
    /// re-register their Win32 hotkeys against the new lists.
    /// </summary>
    public event Action<IReadOnlyList<HotkeyBinding>, IReadOnlyList<MacroHotkeyBinding>>? BindingsChanged;

    public HotkeyConfigStore(IOptions<HotkeyOptions> defaults, ILogger<HotkeyConfigStore> logger)
        : this(AppContext.BaseDirectory, defaults.Value.Bindings, logger)
    {
    }

    public HotkeyConfigStore(
        string baseDirectory,
        IReadOnlyList<HotkeyBinding> defaults,
        ILogger<HotkeyConfigStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(baseDirectory, "hotkeys.json");
        (_bindings, _macroBindings) = LoadFromDisk(defaults);
    }

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;
    public IReadOnlyList<MacroHotkeyBinding> MacroBindings => _macroBindings;

    /// <summary>
    /// Persists both binding lists, atomically swaps in-memory snapshots, raises event.
    /// </summary>
    public async Task ReplaceAsync(
        IReadOnlyList<HotkeyBinding> bindings,
        IReadOnlyList<MacroHotkeyBinding> macroBindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(macroBindings);

        var triggerSnapshot = bindings.ToImmutableList();
        var macroSnapshot = macroBindings.ToImmutableList();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistAsync(triggerSnapshot, macroSnapshot, cancellationToken).ConfigureAwait(false);
            _bindings = triggerSnapshot;
            _macroBindings = macroSnapshot;
            LogBindingsReplaced(triggerSnapshot.Count, macroSnapshot.Count, _path);
        }
        finally
        {
            _writeLock.Release();
        }

        // Raised outside the lock so subscribers (which call back into the Win32 monitor
        // and may take a while) don't keep the file lock blocked.
        BindingsChanged?.Invoke(triggerSnapshot, macroSnapshot);
    }

    private async Task PersistAsync(
        ImmutableList<HotkeyBinding> bindings,
        ImmutableList<MacroHotkeyBinding> macroBindings,
        CancellationToken cancellationToken)
    {
        var dto = new HotkeyFile
        {
            Bindings = bindings.ToList(),
            MacroBindings = macroBindings.ToList(),
        };
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private (ImmutableList<HotkeyBinding> Triggers, ImmutableList<MacroHotkeyBinding> Macros) LoadFromDisk(
        IReadOnlyList<HotkeyBinding> defaults)
    {
        if (!File.Exists(_path))
        {
            LogFileMissing(_path, defaults.Count);
            return (defaults.ToImmutableList(), ImmutableList<MacroHotkeyBinding>.Empty);
        }

        try
        {
            // Two-stage parse: read with string-typed Trigger so we can survive entries
            // that reference orchestrator triggers that no longer exist. Skip-and-log
            // instead of failing the whole file.
            using var stream = File.OpenRead(_path);
            var raw = JsonSerializer.Deserialize<RawHotkeyFile>(stream, JsonOptions);
            var rawBindings = raw?.Bindings ?? new List<RawHotkeyBinding>();
            var rawMacros = raw?.MacroBindings ?? new List<RawMacroHotkeyBinding>();

            var triggers = new List<HotkeyBinding>(rawBindings.Count);
            var skipped = 0;
            foreach (var rb in rawBindings)
            {
                if (string.IsNullOrEmpty(rb.Trigger) || !Enum.TryParse<OrchestratorTrigger>(rb.Trigger, ignoreCase: true, out var trigger))
                {
                    LogSkippedUnknownTrigger(rb.Trigger ?? "<null>");
                    skipped++;
                    continue;
                }
                triggers.Add(new HotkeyBinding(trigger, rb.Modifiers, rb.Key, rb.MouseButton));
            }

            var macros = new List<MacroHotkeyBinding>(rawMacros.Count);
            foreach (var rm in rawMacros)
            {
                if (string.IsNullOrEmpty(rm.MacroName))
                {
                    LogSkippedEmptyMacroName();
                    skipped++;
                    continue;
                }
                macros.Add(new MacroHotkeyBinding(rm.MacroName, rm.Modifiers, rm.Key, rm.MouseButton));
            }

            LogLoaded(triggers.Count, macros.Count, _path);
            if (skipped > 0)
            {
                LogSkippedSummary(skipped, _path);
            }
            return (triggers.ToImmutableList(), macros.ToImmutableList());
        }
        catch (Exception ex)
        {
            LogLoadFailed(ex, _path, defaults.Count);
            return (defaults.ToImmutableList(), ImmutableList<MacroHotkeyBinding>.Empty);
        }
    }

    // Persisted shape — writes use the strongly-typed records so enums serialise
    // by name via JsonStringEnumConverter.
    private sealed class HotkeyFile
    {
        public List<HotkeyBinding> Bindings { get; set; } = new();
        public List<MacroHotkeyBinding> MacroBindings { get; set; } = new();
    }

    // Read shape — string-typed Trigger so unknown enum values don't fail the whole
    // deserialization.
    private sealed class RawHotkeyFile
    {
        public List<RawHotkeyBinding> Bindings { get; set; } = new();
        public List<RawMacroHotkeyBinding> MacroBindings { get; set; } = new();
    }

    private sealed class RawHotkeyBinding
    {
        public string? Trigger { get; set; }
        public HotkeyModifiers Modifiers { get; set; }
        public VirtualKey Key { get; set; }
        public MouseButton MouseButton { get; set; } = MouseButton.None;
    }

    private sealed class RawMacroHotkeyBinding
    {
        public string? MacroName { get; set; }
        public HotkeyModifiers Modifiers { get; set; }
        public VirtualKey Key { get; set; }
        public MouseButton MouseButton { get; set; } = MouseButton.None;
    }

    [LoggerMessage(LogLevel.Information, "Hotkey bindings loaded: {TriggerCount} trigger(s) + {MacroCount} macro(s) from {Path}")]
    partial void LogLoaded(int triggerCount, int macroCount, string path);

    [LoggerMessage(LogLevel.Information, "Hotkey config file not found at {Path}; using {Count} default trigger binding(s), 0 macro binding(s)")]
    partial void LogFileMissing(string path, int count);

    [LoggerMessage(LogLevel.Error, "Failed to load hotkey config from {Path}; falling back to {Count} default trigger binding(s), 0 macro binding(s)")]
    partial void LogLoadFailed(Exception ex, string path, int count);

    [LoggerMessage(LogLevel.Information, "Hotkey bindings replaced: {TriggerCount} trigger(s) + {MacroCount} macro(s) persisted to {Path}")]
    partial void LogBindingsReplaced(int triggerCount, int macroCount, string path);

    [LoggerMessage(LogLevel.Warning, "Skipping hotkey binding for unknown trigger '{Trigger}' — left over from older version, will not be re-saved on next write")]
    partial void LogSkippedUnknownTrigger(string trigger);

    [LoggerMessage(LogLevel.Warning, "Skipping macro hotkey binding with empty MacroName")]
    partial void LogSkippedEmptyMacroName();

    [LoggerMessage(LogLevel.Warning, "Dropped {Skipped} hotkey binding(s) from {Path}")]
    partial void LogSkippedSummary(int skipped, string path);
}
