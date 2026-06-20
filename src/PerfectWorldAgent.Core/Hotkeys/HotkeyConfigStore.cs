using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Hotkeys;

// JSON-backed store for hotkey bindings, mirroring the JsonCharacterRoster pattern.
//
// Two reasons we need this on top of (or instead of) IOptions<HotkeyOptions>:
//   1. We want the bindings editable from the Settings dialog at runtime, without
//      restarting the app.
//   2. IOptions snapshots the values at composition time and the orchestrator/listener
//      have no signal to re-read them. Even IOptionsMonitor only reflects appsettings.json
//      file changes — it has no mechanism for the UI to push new values.
//
// File layout:
//   <BaseDirectory>/hotkeys.json
//
// If the file is missing, we fall back to the HotkeyOptions defaults (so a fresh install
// still works). On every successful ReplaceAsync we persist and raise BindingsChanged so
// HotkeyListener can re-register against Win32.
//
// Concurrency: ReplaceAsync serialised by SemaphoreSlim — only one write in flight.
// Reads grab the immutable snapshot lock-free.
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

    /// <summary>
    /// Raised after a successful <see cref="ReplaceAsync"/>. Subscribers (HotkeyListener)
    /// re-register their Win32 hotkeys against the new list.
    /// </summary>
    public event Action<IReadOnlyList<HotkeyBinding>>? BindingsChanged;

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
        _bindings = LoadFromDisk(defaults);
    }

    /// <summary>
    /// Current immutable snapshot of bindings. Replaced atomically by <see cref="ReplaceAsync"/>;
    /// reads are lock-free.
    /// </summary>
    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    /// <summary>
    /// Persists a new full set of bindings to disk and atomically swaps the in-memory
    /// snapshot, then raises <see cref="BindingsChanged"/>. Writes are serialised by an
    /// internal semaphore so concurrent saves don't race the file.
    /// </summary>
    public async Task ReplaceAsync(IReadOnlyList<HotkeyBinding> bindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var snapshot = bindings.ToImmutableList();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _bindings = snapshot;
            LogBindingsReplaced(snapshot.Count, _path);
        }
        finally
        {
            _writeLock.Release();
        }

        // Raised outside the lock so subscribers (which call back into the Win32 monitor
        // and may take a while) don't keep the file lock blocked.
        BindingsChanged?.Invoke(snapshot);
    }

    private async Task PersistAsync(ImmutableList<HotkeyBinding> bindings, CancellationToken cancellationToken)
    {
        var dto = new HotkeyFile { Bindings = bindings.ToList() };
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private ImmutableList<HotkeyBinding> LoadFromDisk(IReadOnlyList<HotkeyBinding> defaults)
    {
        if (!File.Exists(_path))
        {
            LogFileMissing(_path, defaults.Count);
            return defaults.ToImmutableList();
        }

        try
        {
            // Two-stage parse: read with string-typed Trigger so we can survive entries
            // that reference orchestrator triggers that no longer exist (e.g. GoFollow
            // after v1 stripped the mode state machine). Skip-and-log instead of
            // failing the whole file.
            using var stream = File.OpenRead(_path);
            var raw = JsonSerializer.Deserialize<RawHotkeyFile>(stream, JsonOptions);
            var rawBindings = raw?.Bindings ?? new List<RawHotkeyBinding>();

            var bindings = new List<HotkeyBinding>(rawBindings.Count);
            var skipped = 0;
            foreach (var rb in rawBindings)
            {
                if (string.IsNullOrEmpty(rb.Trigger) || !Enum.TryParse<OrchestratorTrigger>(rb.Trigger, ignoreCase: true, out var trigger))
                {
                    LogSkippedUnknownTrigger(rb.Trigger ?? "<null>");
                    skipped++;
                    continue;
                }
                bindings.Add(new HotkeyBinding(trigger, rb.Modifiers, rb.Key, rb.MouseButton));
            }

            LogLoaded(bindings.Count, _path);
            if (skipped > 0)
            {
                LogSkippedSummary(skipped, _path);
            }
            return bindings.ToImmutableList();
        }
        catch (Exception ex)
        {
            LogLoadFailed(ex, _path, defaults.Count);
            return defaults.ToImmutableList();
        }
    }

    // Persisted shape — writes use the strongly-typed HotkeyBinding so enums serialise
    // by name via JsonStringEnumConverter.
    private sealed class HotkeyFile
    {
        public List<HotkeyBinding> Bindings { get; set; } = new();
    }

    // Read shape — string-typed Trigger so unknown enum values don't fail the whole
    // deserialization. Mirrors HotkeyBinding for on-disk compatibility.
    private sealed class RawHotkeyFile
    {
        public List<RawHotkeyBinding> Bindings { get; set; } = new();
    }

    private sealed class RawHotkeyBinding
    {
        public string? Trigger { get; set; }
        public HotkeyModifiers Modifiers { get; set; }
        public VirtualKey Key { get; set; }
        public MouseButton MouseButton { get; set; } = MouseButton.None;
    }

    [LoggerMessage(LogLevel.Information, "Hotkey bindings loaded: {Count} from {Path}")]
    partial void LogLoaded(int count, string path);

    [LoggerMessage(LogLevel.Information, "Hotkey config file not found at {Path}; using {Count} default binding(s)")]
    partial void LogFileMissing(string path, int count);

    [LoggerMessage(LogLevel.Error, "Failed to load hotkey config from {Path}; falling back to {Count} default binding(s)")]
    partial void LogLoadFailed(Exception ex, string path, int count);

    [LoggerMessage(LogLevel.Information, "Hotkey bindings replaced: {Count} entries persisted to {Path}")]
    partial void LogBindingsReplaced(int count, string path);

    [LoggerMessage(LogLevel.Warning, "Skipping hotkey binding for unknown trigger '{Trigger}' — left over from older version, will not be re-saved on next write")]
    partial void LogSkippedUnknownTrigger(string trigger);

    [LoggerMessage(LogLevel.Warning, "Dropped {Skipped} hotkey binding(s) with unknown trigger(s) from {Path}")]
    partial void LogSkippedSummary(int skipped, string path);
}
