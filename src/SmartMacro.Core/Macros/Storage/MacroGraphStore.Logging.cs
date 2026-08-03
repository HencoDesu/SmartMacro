using Microsoft.Extensions.Logging;

namespace SmartMacro.Macros.Storage;

// Кодогенерируемые объявления LoggerMessage для MacroGraphStore. Вынесены отдельно, чтобы
// основной файл читался как логика хранения, а не как заготовки логгера.
public sealed partial class MacroGraphStore
{
    [LoggerMessage(LogLevel.Information,
        "Библиотека макросов загружена: графов {Count} (пропущено {Skipped}) из {Path}")]
    partial void LogLoaded(int count, int skipped, string path);

    [LoggerMessage(LogLevel.Error,
        "Пропущен нечитаемый файл макроса {Path} — остальная библиотека всё равно загрузится")]
    partial void LogFileSkipped(Exception ex, string path);

    [LoggerMessage(LogLevel.Warning, "Имя файла важнее поля 'Name': '{JsonName}' загружен как '{FileName}'")]
    partial void LogNameMismatch(string jsonName, string fileName);

    [LoggerMessage(LogLevel.Error, "Не удалось перечислить папку макросов {Path} — считаем библиотеку пустой")]
    partial void LogEnumerationFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Макрос '{Name}' сохранён в {Path}")]
    partial void LogSaved(string name, string path);

    [LoggerMessage(LogLevel.Information, "Макрос '{Name}' удалён ({Path})")]
    partial void LogDeleted(string name, string path);

    [LoggerMessage(LogLevel.Error,
        "Ошибка валидации макроса '{Name}' на ноде {NodeId}: {Message} — сохранён всё равно, прогон на ней оборвётся")]
    partial void LogValidationError(string name, string nodeId, string message);

    [LoggerMessage(LogLevel.Information, "Библиотека перечитана после внешнего изменения: графов {Count}")]
    partial void LogReloadedExternally(int count);

    [LoggerMessage(LogLevel.Warning, "Не удалось перечитать библиотеку макросов после внешнего изменения")]
    partial void LogReloadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning,
        "FileSystemWatcher не запустился на {Path}; перечитывание макросов на лету отключено")]
    partial void LogWatcherStartFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Перенесено графов макросов: {Count} из {LegacyPath} в {Path}")]
    partial void LogMigrated(int count, string legacyPath, string path);

    [LoggerMessage(LogLevel.Warning, "При переносе отброшено старых макросов без исполнимых действий: {Count}")]
    partial void LogMigrationSkipped(int count);

    [LoggerMessage(LogLevel.Information,
        "Перенос переложил привязок из hotkeys.json в сами макросы триггерами: {Count}")]
    partial void LogMigrationHotkeysAttached(int count);

    [LoggerMessage(LogLevel.Warning,
        "Перенос не нашёл места привязкам из hotkeys.json: {Count} — они были на старые встроенные рассылки (immunity/assist/cursor-click/identify), а теперь это макросы-примеры pw-*. Привяжите их заново там; старый файл сохранён как hotkeys.json.migrated")]
    partial void LogMigrationHotkeysOrphaned(int count);

    [LoggerMessage(LogLevel.Error, "Не удалось прочитать старый файл макросов {Path}; перенос пропущен")]
    partial void LogMigrationReadFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Error, "Не удалось разобрать старый файл макросов {Path}; перенос пропущен")]
    partial void LogMigrationParseFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Error, "Не удалось записать перенесённый макрос '{Name}'")]
    partial void LogMigrationWriteFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Warning,
        "Макросы перенесены, но переименовать {Path} не вышло — при следующем запуске он будет пропущен, потому что папка macros уже есть")]
    partial void LogMigrationRenameFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Записано макросов-примеров: {Count} в {Path}")]
    partial void LogSeeded(int count, string path);

    [LoggerMessage(LogLevel.Error, "Не удалось записать макрос-пример '{Name}'")]
    partial void LogSeedFailed(Exception ex, string name);
}
