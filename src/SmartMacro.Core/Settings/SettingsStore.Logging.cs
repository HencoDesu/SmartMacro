using Microsoft.Extensions.Logging;

namespace SmartMacro.Settings;

// Кодогенерируемые объявления LoggerMessage для SettingsStore. Вынесены отдельно, чтобы основной
// файл читался как логика хранения, а не как заготовки логгера.
public sealed partial class SettingsStore
{
    [LoggerMessage(LogLevel.Information, "Настройки загружены: профилей {Profiles} из {Path}")]
    partial void LogLoaded(int profiles, string path);

    [LoggerMessage(LogLevel.Information,
        "Файла настроек нет — создаём {Path} с умолчаниями (единственная запись, которую делает старт)")]
    partial void LogMissingFileSeeded(string path);

    [LoggerMessage(LogLevel.Warning,
        "Не удалось создать файл настроек {Path} — работаем на умолчаниях, правки сохранить будет некуда")]
    partial void LogSeedFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Error,
        "Файл настроек {Path} не читается — оставляем прежние значения и НЕ переписываем его: почините файл, он перечитается сам")]
    partial void LogUnreadable(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Настройки сохранены в {Path}")]
    partial void LogSaved(string path);

    [LoggerMessage(LogLevel.Warning, "Настройки отвергнуты: замечаний {Count}, первое — {First}")]
    partial void LogSaveRejected(int count, string first);

    [LoggerMessage(LogLevel.Information, "Настройки сброшены к умолчаниям ({Path})")]
    partial void LogReset(string path);

    [LoggerMessage(LogLevel.Information, "Настройки перечитаны после внешней правки {Path}")]
    partial void LogReloadedExternally(string path);

    [LoggerMessage(LogLevel.Warning, "Не удалось перечитать настройки после внешней правки")]
    partial void LogReloadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning,
        "FileSystemWatcher не запустился на {Path}; правки файла настроек руками подхватываться не будут")]
    partial void LogWatcherStartFailed(Exception ex, string path);
}
