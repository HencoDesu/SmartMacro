using Microsoft.Extensions.Logging;

namespace SmartMacro.Macros.Storage;

// Кодогенерируемые объявления LoggerMessage для MacroGraphStore. Вынесены отдельно, чтобы
// основной файл читался как логика хранения, а не как заготовки логгера.
public sealed partial class MacroGraphStore
{
    [LoggerMessage(LogLevel.Information,
        "Библиотека макросов загружена: макросов {Count} (пропущено {Skipped}) из {Path}")]
    partial void LogLoaded(int count, int skipped, string path);

    [LoggerMessage(LogLevel.Error,
        "Бандл {Path} не прочитан ({Fault}): {Message} — файл оставлен на месте, остальная библиотека загрузится")]
    partial void LogBundleSkipped(string path, string fault, string message);

    [LoggerMessage(LogLevel.Warning, "Имя файла важнее поля 'Name': '{JsonName}' загружен как '{FileName}'")]
    partial void LogNameMismatch(string jsonName, string fileName);

    [LoggerMessage(LogLevel.Error, "Не удалось перечислить папку макросов {Path} — считаем библиотеку пустой")]
    partial void LogEnumerationFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Макрос '{Name}' сохранён в {Path} (шаблонов {Templates})")]
    partial void LogSaved(string name, string path, int templates);

    [LoggerMessage(LogLevel.Information, "Макрос '{Name}' удалён ({Path})")]
    partial void LogDeleted(string name, string path);

    [LoggerMessage(LogLevel.Error,
        "Ошибка валидации макроса '{Name}' на ноде {NodeName}: {Message} — сохранён всё равно, прогон на ней оборвётся")]
    partial void LogValidationError(string name, string nodeName, string message);

    [LoggerMessage(LogLevel.Information, "Макрос '{Name}': шаблон '{Template}' добавлен ({Bytes} Б)")]
    partial void LogTemplateAdded(string name, string template, int bytes);

    [LoggerMessage(LogLevel.Information, "Макрос '{Name}': шаблон '{Template}' удалён")]
    partial void LogTemplateDeleted(string name, string template);

    [LoggerMessage(LogLevel.Warning,
        "Макрос '{Name}': бандл не перезаписан — прочитать его целиком не удалось, правка отброшена")]
    partial void LogBundleNotRewritable(string name);

    [LoggerMessage(LogLevel.Information, "Библиотека перечитана после внешнего изменения: макросов {Count}")]
    partial void LogReloadedExternally(int count);

    [LoggerMessage(LogLevel.Warning, "Не удалось перечитать библиотеку макросов после внешнего изменения")]
    partial void LogReloadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning,
        "FileSystemWatcher не запустился на {Path}; перечитывание макросов на лету отключено")]
    partial void LogWatcherStartFailed(Exception ex, string path);
}
