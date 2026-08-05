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

    [LoggerMessage(LogLevel.Warning,
        "Макрос '{Name}' не прошёл валидацию при загрузке: {Message} — его триггеры НЕ вооружены")]
    partial void LogNotArmed(string name, string message);

    [LoggerMessage(LogLevel.Information, "Библиотека перечитана после изменения на диске: макросов {Count}")]
    partial void LogReloadedExternally(int count);

    [LoggerMessage(LogLevel.Warning, "Не удалось перечитать библиотеку макросов после изменения на диске")]
    partial void LogReloadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning,
        "FileSystemWatcher не запустился на {Path}; перечитывание макросов на лету отключено")]
    partial void LogWatcherStartFailed(Exception ex, string path);
}
