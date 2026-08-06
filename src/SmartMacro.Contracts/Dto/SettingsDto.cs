using SmartMacro.Contracts.Settings;

namespace SmartMacro.Contracts.Dto;

/// <summary>
/// Настройки плюс то, что настройками не является, но показывается на том же экране.
///
/// Три поля рядом с <see cref="Settings"/> — не украшение, а разделение по владельцу:
/// <see cref="Settings"/> лежит в файле и переживает перезапуск, а <see cref="LogLevel"/> живёт
/// только в текущем процессе, и пути известны лишь демону. Слить их в одну запись значило бы
/// показать уровень журнала как сохраняемую настройку, каковой он намеренно НЕ является.
/// </summary>
/// <param name="Settings">Содержимое файла настроек — то, что панель правит.</param>
/// <param name="LogLevel">
/// Живой минимальный уровень Serilog у демона.
///
/// <b>Не из файла настроек и в него не попадёт.</b> Постоянный уровень остаётся в
/// <c>appsettings.json</c>: если демон падает на чтении настроек, отладить это можно только тем
/// уровнем, что был известен ДО. Экран настроек двигает <c>LoggingLevelSwitch</c> на лету, и
/// сдвиг живёт до перезапуска демона — интерфейс обязан это сказать.
/// </param>
/// <param name="SettingsFilePath">Полный путь к <c>settings.json</c> — для подписи и для «показать файл».</param>
/// <param name="FolderPath">Папка демона: её открывает кнопка «Открыть папку».</param>
public sealed record SettingsSnapshotDto(
    AppSettings Settings,
    LogLevelDto LogLevel,
    string SettingsFilePath,
    string FolderPath);
