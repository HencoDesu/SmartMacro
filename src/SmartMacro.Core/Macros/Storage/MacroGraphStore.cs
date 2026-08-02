using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// The macro library on disk: one JSON file per graph under <c>macros/</c> next to the
/// executable, where the FILE NAME STEM is the macro name. One file per macro (rather
/// than the legacy single <c>macros.json</c>) so hand-editing, diffing, and sharing a
/// single macro are all natural operations.
///
/// Responsibilities:
///   * load-all on construction, skipping (never failing on) unreadable files;
///   * <see cref="SaveAsync"/> / <see cref="DeleteAsync"/> CRUD with NTFS name validation;
///   * hot-reload via <see cref="FileSystemWatcher"/>, debounced, with our own writes
///     suppressed by comparing a folder signature of last-write timestamps;
///   * one-time migration of a legacy <c>macros.json</c>, and seeding of the PW example
///     set when the folder ends up empty.
///
/// Implements <see cref="IMacroGraphResolver"/>, so <c>RunMacroNode</c> resolves
/// sub-macros straight out of the live library.
///
/// Concurrency: writes are serialised by a semaphore; reads go through the immutable
/// <see cref="All"/> snapshot and are lock-free.
/// </summary>
public sealed partial class MacroGraphStore : IMacroGraphResolver, IDisposable
{
    /// <summary>Name of the macro folder, relative to the app directory.</summary>
    public const string FolderName = "macros";

    private const int ReloadDebounceMs = 300;

    private readonly string _directory;
    private readonly ILogger<MacroGraphStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FileSystemWatcher? _watcher;

    private CancellationTokenSource? _pendingReload;
    private ImmutableList<MacroGraph> _macros = [];
    // Snapshot of "path → last write time" as of the last load/write WE performed. A
    // debounced reload whose signature matches this is either our own write echoing back
    // or a duplicate event, and is dropped.
    private ImmutableDictionary<string, DateTime> _signature = ImmutableDictionary<string, DateTime>.Empty;

    /// <summary>Production constructor: <c>macros/</c> next to the executable.</summary>
    public MacroGraphStore(ILogger<MacroGraphStore> logger)
        : this(AppContext.BaseDirectory, logger)
    {
    }

    /// <param name="baseDirectory">Folder containing (or to contain) <c>macros/</c> and any legacy <c>macros.json</c>.</param>
    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="seedDefaults">
    /// Write the built-in PW example graphs when the folder ends up empty. Tests that
    /// want a bare library pass <c>false</c>.
    /// </param>
    public MacroGraphStore(string baseDirectory, ILogger<MacroGraphStore> logger, bool seedDefaults = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _logger = logger;
        _directory = Path.Combine(baseDirectory, FolderName);

        MigrateLegacyIfNeeded(baseDirectory);
        Directory.CreateDirectory(_directory);
        if (seedDefaults)
        {
            SeedDefaultsIfEmpty();
        }
        Reload(raiseEvent: false);

        // Best-effort watcher — hot-reload is a nice-to-have, so permission/platform
        // failures degrade to "restart to pick up external edits" instead of crashing.
        try
        {
            _watcher = new FileSystemWatcher(_directory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
        catch (Exception ex)
        {
            LogWatcherStartFailed(ex, _directory);
        }
    }

    /// <summary>Raised after the library changes — save, delete, or an external edit.</summary>
    public event Action<IReadOnlyList<MacroGraph>>? MacrosChanged;

    /// <summary>Absolute path of the macro folder.</summary>
    public string FolderPath => _directory;

    /// <summary>Current immutable snapshot of the library, ordered by name.</summary>
    public IReadOnlyList<MacroGraph> All => _macros;

    /// <inheritdoc />
    public MacroGraph? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        foreach (var macro in _macros)
        {
            if (string.Equals(macro.Name, name, StringComparison.Ordinal))
            {
                return macro;
            }
        }
        return null;
    }

    /// <summary>
    /// Writes <paramref name="graph"/> to <c>macros/{Name}.json</c>, replacing any
    /// existing file of that name, and raises <see cref="MacrosChanged"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The graph's name is not a usable file name.</exception>
    public async Task SaveAsync(MacroGraph graph, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (ValidateName(graph.Name) is { } nameError)
        {
            throw new ArgumentException(nameError, nameof(graph));
        }

        // Errors don't block the save — the editor must be able to persist a
        // work-in-progress graph — but they're loud, because the executor will abort a
        // run that reaches the broken part.
        foreach (var issue in MacroGraphValidator.Validate(graph))
        {
            if (issue.Severity == ValidationSeverity.Error)
            {
                LogValidationError(graph.Name, issue.NodeId ?? "(graph)", issue.Message);
            }
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(graph.Name);
            await File.WriteAllTextAsync(path, MacroGraphJson.Serialize(graph), cancellationToken).ConfigureAwait(false);
            LogSaved(graph.Name, path);
            Reload(raiseEvent: false);
        }
        finally
        {
            _writeLock.Release();
        }

        MacrosChanged?.Invoke(_macros);
    }

    /// <summary>
    /// Deletes <c>macros/{name}.json</c> and raises <see cref="MacrosChanged"/>.
    /// </summary>
    /// <returns><c>false</c> when no such file exists (no event raised).</returns>
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (ValidateName(name) is not null)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            LogDeleted(name, path);
            Reload(raiseEvent: false);
        }
        finally
        {
            _writeLock.Release();
        }

        MacrosChanged?.Invoke(_macros);
        return true;
    }

    /// <summary>
    /// Checks a macro name against NTFS file-name rules (the name IS the file stem).
    /// </summary>
    /// <returns>An error description, or <c>null</c> when the name is usable.</returns>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Macro name must not be empty.";
        }
        if (name.Length > 100)
        {
            return "Macro name must be 100 characters or shorter.";
        }
        var invalid = name.IndexOfAny(Path.GetInvalidFileNameChars());
        if (invalid >= 0)
        {
            return $"Macro name must not contain '{name[invalid]}'.";
        }
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "Macro name must not end with a dot or a space.";
        }
        if (IsReservedDeviceName(name))
        {
            return $"'{name}' is a reserved Windows device name.";
        }
        return null;
    }

    private static bool IsReservedDeviceName(string name)
    {
        // CON, PRN, AUX, NUL, COM0-9, LPT0-9 — unusable as file stems on Windows.
        var stem = name.Split('.')[0];
        if (stem is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }
        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && char.IsAsciiDigit(stem[3]);
    }

    private string PathFor(string name) => Path.Combine(_directory, $"{name}.json");

    // Full re-read of the folder. Never throws: a file we can't parse is logged and
    // skipped so one bad hand-edit can't empty the library.
    private void Reload(bool raiseEvent)
    {
        var macros = new List<MacroGraph>();
        var signature = ImmutableDictionary.CreateBuilder<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var path in EnumerateFilesSafe())
        {
            try
            {
                signature[path] = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                // Mid-write or deleted between enumeration and stat; the next event re-reads.
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            try
            {
                var graph = MacroGraphJson.Deserialize(File.ReadAllText(path));
                if (!string.Equals(graph.Name, stem, StringComparison.Ordinal))
                {
                    // The file name is authoritative — renaming a file renames the macro.
                    LogNameMismatch(graph.Name, stem);
                    graph = new MacroGraph
                    {
                        Name = stem,
                        Triggers = graph.Triggers,
                        StartNodeId = graph.StartNodeId,
                        Nodes = graph.Nodes,
                    };
                }
                macros.Add(graph);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                LogFileSkipped(ex, path);
                skipped++;
            }
        }

        macros.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        _macros = [.. macros];
        _signature = signature.ToImmutable();
        LogLoaded(macros.Count, skipped, _directory);

        if (raiseEvent)
        {
            MacrosChanged?.Invoke(_macros);
        }
    }

    private IEnumerable<string> EnumerateFilesSafe()
    {
        try
        {
            return Directory.EnumerateFiles(_directory, "*.json").OrderBy(static p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEnumerationFailed(ex, _directory);
            return [];
        }
    }

    // FileSystemWatcher fires on the threadpool and editors emit several events per save,
    // so every burst inside the debounce window collapses into one reload check.
    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pendingReload, cts);
        previous?.Cancel();
        previous?.Dispose();
        _ = ReloadAfterDelayAsync(cts.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReloadDebounceMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer event
        }

        var changed = false;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!HasFolderChanged())
            {
                // Our own write echoing back, or a duplicate event — nothing to do.
                return;
            }
            Reload(raiseEvent: false);
            changed = true;
            LogReloadedExternally(_macros.Count);
        }
        catch (Exception ex)
        {
            LogReloadFailed(ex);
        }
        finally
        {
            _writeLock.Release();
        }

        // Raised outside the lock so subscribers can call back in without deadlocking.
        if (changed)
        {
            MacrosChanged?.Invoke(_macros);
        }
    }

    private bool HasFolderChanged()
    {
        var current = _signature;
        var seen = 0;
        foreach (var path in EnumerateFilesSafe())
        {
            seen++;
            if (!current.TryGetValue(path, out var known))
            {
                return true;
            }
            try
            {
                if (File.GetLastWriteTimeUtc(path) != known)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
        }
        return seen != current.Count;
    }

    // ---- first-run bootstrap -------------------------------------------------------

    private void MigrateLegacyIfNeeded(string baseDirectory)
    {
        var legacyPath = Path.Combine(baseDirectory, LegacyMacroMigration.LegacyFileName);
        if (Directory.Exists(_directory) || !File.Exists(legacyPath))
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(legacyPath);
        }
        catch (Exception ex)
        {
            LogMigrationReadFailed(ex, legacyPath);
            return;
        }

        // Hotkeys come along for the ride: their whole point in the new model is to live
        // inside the macro they start.
        var legacyHotkeysPath = Path.Combine(baseDirectory, LegacyMacroMigration.LegacyHotkeysFileName);
        string? hotkeysJson = null;
        if (File.Exists(legacyHotkeysPath))
        {
            try
            {
                hotkeysJson = File.ReadAllText(legacyHotkeysPath);
            }
            catch (Exception ex)
            {
                LogMigrationReadFailed(ex, legacyHotkeysPath);
            }
        }

        LegacyMacroMigration.Result migration;
        try
        {
            migration = LegacyMacroMigration.Convert(json, hotkeysJson);
        }
        catch (Exception ex)
        {
            LogMigrationParseFailed(ex, legacyPath);
            return;
        }

        if (migration.SkippedMacros > 0)
        {
            LogMigrationSkipped(migration.SkippedMacros);
        }
        if (migration.AttachedHotkeys > 0)
        {
            LogMigrationHotkeysAttached(migration.AttachedHotkeys);
        }
        if (migration.OrphanedHotkeys > 0)
        {
            LogMigrationHotkeysOrphaned(migration.OrphanedHotkeys);
        }

        Directory.CreateDirectory(_directory);
        var written = 0;
        foreach (var graph in migration.Graphs)
        {
            try
            {
                File.WriteAllText(PathFor(graph.Name), MacroGraphJson.Serialize(graph));
                written++;
            }
            catch (Exception ex)
            {
                LogMigrationWriteFailed(ex, graph.Name);
            }
        }

        // Keep the source data, just move it out of the way so we never migrate twice.
        RenameMigrated(legacyPath);
        if (hotkeysJson is not null)
        {
            RenameMigrated(legacyHotkeysPath);
        }

        LogMigrated(written, legacyPath, _directory);
    }

    private void RenameMigrated(string path)
    {
        try
        {
            File.Move(path, path + LegacyMacroMigration.MigratedSuffix, overwrite: true);
        }
        catch (Exception ex)
        {
            LogMigrationRenameFailed(ex, path);
        }
    }

    // Seeded exactly once per install, tracked by a marker file rather than by
    // "is the folder empty". Empty-folder gating looked equivalent but wasn't: a user
    // migrating from the legacy pipeline lands here with a non-empty folder (their own
    // macros) and would never receive the pw-* examples — which are the ONLY remaining
    // implementation of the built-in broadcasts (immunity/assist/cursor-click/identify)
    // that migration deletes. The marker also keeps deletions sticky: remove an example
    // you don't want and it stays gone.
    private void SeedDefaultsIfEmpty()
    {
        var marker = Path.Combine(_directory, ".examples-seeded");
        if (File.Exists(marker))
        {
            return;
        }

        var written = 0;
        foreach (var graph in DefaultMacroGraphs.Build())
        {
            // Never clobber a user's own macro that happens to share the name.
            var path = PathFor(graph.Name);
            if (File.Exists(path))
            {
                continue;
            }
            try
            {
                File.WriteAllText(path, MacroGraphJson.Serialize(graph));
                written++;
            }
            catch (Exception ex)
            {
                LogSeedFailed(ex, graph.Name);
            }
        }

        try
        {
            File.WriteAllText(marker, string.Empty);
        }
        catch (Exception ex)
        {
            // Marker write failed — examples would be re-offered next start. Harmless
            // (the File.Exists guard above makes re-seeding a no-op), so just log.
            LogSeedFailed(ex, ".examples-seeded");
        }

        LogSeeded(written, _directory);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
        }
        _pendingReload?.Cancel();
        _pendingReload?.Dispose();
        _writeLock.Dispose();
    }
}
