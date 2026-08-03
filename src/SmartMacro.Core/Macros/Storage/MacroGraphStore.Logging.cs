using Microsoft.Extensions.Logging;

namespace SmartMacro.Macros.Storage;

// Кодогенерируемые объявления LoggerMessage для MacroGraphStore. Вынесены отдельно, чтобы
// основной файл читался как логика хранения, а не как заготовки логгера.
public sealed partial class MacroGraphStore
{
    [LoggerMessage(LogLevel.Information, "Macro library loaded: {Count} graph(s) ({Skipped} skipped) from {Path}")]
    partial void LogLoaded(int count, int skipped, string path);

    [LoggerMessage(LogLevel.Error, "Skipping unreadable macro file {Path} — the rest of the library still loads")]
    partial void LogFileSkipped(Exception ex, string path);

    [LoggerMessage(LogLevel.Warning, "Macro file name wins over its 'Name' field: '{JsonName}' loaded as '{FileName}'")]
    partial void LogNameMismatch(string jsonName, string fileName);

    [LoggerMessage(LogLevel.Error, "Cannot enumerate macro folder {Path} — treating the library as empty")]
    partial void LogEnumerationFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Macro '{Name}' saved to {Path}")]
    partial void LogSaved(string name, string path);

    [LoggerMessage(LogLevel.Information, "Macro '{Name}' deleted ({Path})")]
    partial void LogDeleted(string name, string path);

    [LoggerMessage(LogLevel.Error,
        "Macro '{Name}' has a validation error at {NodeId}: {Message} — saved anyway, runs will abort there")]
    partial void LogValidationError(string name, string nodeId, string message);

    [LoggerMessage(LogLevel.Information, "Macro library reloaded after external change: {Count} graph(s)")]
    partial void LogReloadedExternally(int count);

    [LoggerMessage(LogLevel.Warning, "Failed to hot-reload the macro library after an external change")]
    partial void LogReloadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "FileSystemWatcher failed to start on {Path}; macro hot-reload disabled")]
    partial void LogWatcherStartFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Migrated {Count} macro graph(s) from {LegacyPath} into {Path}")]
    partial void LogMigrated(int count, string legacyPath, string path);

    [LoggerMessage(LogLevel.Warning, "Migration dropped {Count} legacy macro(s) with no runnable actions")]
    partial void LogMigrationSkipped(int count);

    [LoggerMessage(LogLevel.Information,
        "Migration moved {Count} hotkey binding(s) from hotkeys.json into their macros as triggers")]
    partial void LogMigrationHotkeysAttached(int count);

    [LoggerMessage(LogLevel.Warning,
        "Migration could not place {Count} hotkey binding(s) from hotkeys.json — they bound the old built-in broadcast actions (immunity/assist/cursor-click/identify), which are now the pw-* example macros. Re-bind them there; the old file is kept as hotkeys.json.migrated")]
    partial void LogMigrationHotkeysOrphaned(int count);

    [LoggerMessage(LogLevel.Error, "Cannot read legacy macro file {Path}; skipping migration")]
    partial void LogMigrationReadFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Error, "Cannot parse legacy macro file {Path}; skipping migration")]
    partial void LogMigrationParseFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Error, "Failed to write migrated macro '{Name}'")]
    partial void LogMigrationWriteFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Warning,
        "Migrated macros, but could not rename {Path} — it will be skipped next run because the macros folder now exists")]
    partial void LogMigrationRenameFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Seeded {Count} default example macro(s) into {Path}")]
    partial void LogSeeded(int count, string path);

    [LoggerMessage(LogLevel.Error, "Failed to write default example macro '{Name}'")]
    partial void LogSeedFailed(Exception ex, string name);
}
