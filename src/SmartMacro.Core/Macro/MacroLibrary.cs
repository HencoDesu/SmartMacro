using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SmartMacro.Macro;

// JSON-backed library of named combat macros. Mirrors HotkeyConfigStore's shape: load on
// construction, expose immutable snapshot + change event, atomic replace.
//
// File layout: <BaseDirectory>/macros.json. Missing file → empty library on first run.
// Schema uses string enum names for VirtualKey (round-trips through hand-edit / version
// changes without breaking on unknown values).
//
// Hot-reload: FileSystemWatcher notices external edits (git checkout, text editor save)
// and re-reads. Our own writes are suppressed via LastWriteTime tracking so we don't
// loop back on our persist. Debounced 300ms — editors often fire multiple events per
// save.
//
// Concurrency: ReplaceAsync serialised by SemaphoreSlim. Reads via the Macros snapshot
// are lock-free.
public sealed partial class MacroLibrary : IDisposable
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
    private readonly ILogger<MacroLibrary> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FileSystemWatcher? _watcher;
    private CancellationTokenSource? _pendingReload;
    private DateTime _lastKnownWriteTime = DateTime.MinValue;

    private ImmutableList<Macro> _macros;

    /// <summary>
    /// Raised after a successful <see cref="ReplaceAsync"/>. UI components refresh their
    /// macro pickers; agents already-in-combat finish their current run (the runner uses
    /// the macro reference captured at OnEntry, not a fresh lookup per step).
    /// </summary>
    public event Action<IReadOnlyList<Macro>>? MacrosChanged;

    public MacroLibrary(ILogger<MacroLibrary> logger)
        : this(AppContext.BaseDirectory, logger)
    {
    }

    public MacroLibrary(string baseDirectory, ILogger<MacroLibrary> logger)
    {
        _logger = logger;
        _path = Path.Combine(baseDirectory, "macros.json");
        _macros = LoadFromDisk();
        _lastKnownWriteTime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;

        // Best-effort watcher — swallow init failures (permissions, missing dir on
        // some hosted-service scenarios) since hot-reload is a nice-to-have.
        try
        {
            _watcher = new FileSystemWatcher(baseDirectory, "macros.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += (s, e) => OnFileChanged(s, e);
        }
        catch (Exception ex)
        {
            LogWatcherStartFailed(ex, baseDirectory);
        }
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Dispose();
        }
        _pendingReload?.Cancel();
        _pendingReload?.Dispose();
    }

    /// <summary>
    /// Current immutable snapshot of all macros.
    /// </summary>
    public IReadOnlyList<Macro> Macros => _macros;

    /// <summary>
    /// Looks up a macro by exact name.
    /// </summary>
    /// <returns>The macro, or <c>null</c> when not found / name is empty.</returns>
    public Macro? TryGet(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        foreach (var m in _macros)
        {
            if (string.Equals(m.Name, name, StringComparison.Ordinal))
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>
    /// Persists a new full set of macros to disk, atomically swaps the in-memory
    /// snapshot, and raises <see cref="MacrosChanged"/>.
    /// </summary>
    public async Task ReplaceAsync(IReadOnlyList<Macro> macros, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var snapshot = macros.ToImmutableList();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _macros = snapshot;
            LogReplaced(snapshot.Count, _path);
        }
        finally
        {
            _writeLock.Release();
        }

        MacrosChanged?.Invoke(snapshot);
    }

    private async Task PersistAsync(ImmutableList<Macro> macros, CancellationToken cancellationToken)
    {
        var dto = new MacroFile { Macros = macros.ToList() };
        await using (var stream = File.Create(_path))
        {
            await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        // Track own write time so the FileSystemWatcher's Changed event doesn't trigger
        // a spurious reload immediately after our persist.
        _lastKnownWriteTime = File.GetLastWriteTimeUtc(_path);
    }

    // FileSystemWatcher fires on threadpool. Debounce: any burst of events within 300ms
    // collapses into a single reload check. Reload compares LastWriteTimeUtc — if it
    // matches what we last wrote, no-op (suppresses our own writes).
    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _pendingReload, cts);
        prev?.Cancel();
        prev?.Dispose();
        _ = ReloadAfterDelayAsync(cts.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer event
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        ImmutableList<Macro> reloaded;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            var writeTime = File.GetLastWriteTimeUtc(_path);
            if (writeTime == _lastKnownWriteTime)
            {
                // Either our own write or a duplicate event with no real change.
                return;
            }
            reloaded = LoadFromDisk();
            _macros = reloaded;
            _lastKnownWriteTime = writeTime;
            LogReloadedExternally(reloaded.Count);
        }
        catch (Exception ex)
        {
            LogReloadFailed(ex);
            return;
        }
        finally
        {
            _writeLock.Release();
        }

        // Raised outside the lock so subscribers can call back in without deadlock.
        MacrosChanged?.Invoke(reloaded);
    }

    private ImmutableList<Macro> LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            LogFileMissing(_path);
            return ImmutableList<Macro>.Empty;
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var dto = JsonSerializer.Deserialize<MacroFile>(stream, JsonOptions);
            var macros = dto?.Macros ?? new List<Macro>();
            LogLoaded(macros.Count, _path);
            return macros.ToImmutableList();
        }
        catch (Exception ex)
        {
            LogLoadFailed(ex, _path);
            return ImmutableList<Macro>.Empty;
        }
    }

    private sealed class MacroFile
    {
        public List<Macro> Macros { get; set; } = new();
    }

    [LoggerMessage(LogLevel.Information, "Macros loaded: {Count} from {Path}")]
    partial void LogLoaded(int count, string path);

    [LoggerMessage(LogLevel.Information, "Macros file not found at {Path}; starting with empty library")]
    partial void LogFileMissing(string path);

    [LoggerMessage(LogLevel.Error, "Failed to load macros from {Path}; starting with empty library")]
    partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Macros replaced: {Count} entries persisted to {Path}")]
    partial void LogReplaced(int count, string path);

    [LoggerMessage(LogLevel.Information, "Macros reloaded externally: {Count} entries from disk")]
    partial void LogReloadedExternally(int count);

    [LoggerMessage(LogLevel.Warning, "Failed to hot-reload macros from disk after external change")]
    partial void LogReloadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "FileSystemWatcher failed to start on {Path}; hot-reload disabled")]
    partial void LogWatcherStartFailed(Exception ex, string path);
}
