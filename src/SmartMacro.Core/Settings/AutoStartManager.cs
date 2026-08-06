using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SmartMacro.Contracts.Settings;
using SmartMacro.Native.Diagnostics;

namespace SmartMacro.Settings;

/// <summary>
/// Единственная часть настроек, которая применяется НЕ в процессе: галочка «запускать при входе»
/// превращается в значение ключа <c>Run</c> в <c>HKCU</c>.
///
/// <b>Одна галочка — один механизм.</b> Так было не всегда: галочек две, и раньше над ними стояло
/// ТРИ механизма, между которыми выбирало приложение, — потому что ключ <c>Run</c> не умеет
/// поднимать процесс с повышением, и пару «✓ вход / ✓ права» приходилось регистрировать задачей в
/// Планировщике с наивысшими правами. Планировщик убран целиком: он порождал <c>schtasks.exe</c>
/// на каждом старте демона, подвешивал старт на больной службе задач и требовал согласовывать три
/// механизма вместо одного. Права теперь запрашивает сам демон при старте
/// (<see cref="NeedsElevationRelaunch"/>), а цена названа вслух: с обеими галочками пользователь
/// увидит окно UAC при каждом входе в систему.
///
/// <b>Согласование всегда полное, а не «добавить нужное».</b> Каждый вызов <see cref="Apply"/>
/// приводит ключ в состояние, отвечающее галочке, — в том числе УДАЛЯЕТ запись, когда галочку
/// сняли. Иначе программа продолжала бы запускаться после того, как её об этом перестали просить,
/// а виноватой выглядела бы Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AutoStartManager
{
    /// <summary>Имя значения в ключе <c>Run</c>.</summary>
    public const string EntryName = "SmartMacro";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ILogger<AutoStartManager> _logger;

    public AutoStartManager(ILogger<AutoStartManager> logger) => _logger = logger;

    /// <summary>
    /// Надо ли перезапустить себя с повышением прямо сейчас: настройка требует прав, а процесс их
    /// не имеет.
    ///
    /// Отдельно от <see cref="Apply"/> потому, что это вопрос СТАРТА, а не согласования записей:
    /// повышение нельзя получить на лету, поэтому смена галочки прав вступает в силу только со
    /// следующего запуска демона — и интерфейс обязан это сказать, иначе пользователь снимет
    /// галочку, увидит чистую панель и будет неделю искать, почему макросы перестали доходить до
    /// игры.
    ///
    /// Вопрос стал осмысленным только с манифестом <c>asInvoker</c>: у процесса, помеченного
    /// <c>requireAdministrator</c>, ответ всегда «нет», потому что без повышения он не стартует.
    /// </summary>
    /// <param name="settings">Текущие настройки.</param>
    public static bool NeedsElevationRelaunch(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Startup.RunElevated && !EnvironmentProbe.IsElevated();
    }

    /// <summary>
    /// Приводит ключ <c>Run</c> в соответствие галочке автозапуска.
    /// </summary>
    /// <param name="settings">Настройки, чей раздел <c>Startup</c> надо применить.</param>
    /// <param name="executablePath">Полный путь к исполняемому файлу демона.</param>
    /// <returns>
    /// <c>null</c> — согласовано. Строка — человекочитаемая причина, по которой не вышло. НЕ
    /// бросает: автозапуск не настроился — это неприятность, а не повод не поднять движок.
    /// </returns>
    /// <remarks>
    /// Синхронный, и это стало возможным вместе с уходом Планировщика: запись в свою ветку
    /// реестра занимает микросекунды, тогда как запуск <c>schtasks.exe</c> занимал сотни
    /// миллисекунд и его приходилось отцеплять от сохранения настроек, чтобы панель не ждала
    /// службу задач ради ответа «число в поле принято».
    /// </remarks>
    public string? Apply(AppSettings settings, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var wantsRunKey = settings.Startup.RunAtLogon;
        var failure = wantsRunKey ? TrySetRunKey(executablePath) : TryRemoveRunKey();

        LogApplied(settings.Startup.RunAtLogon, settings.Startup.RunElevated);
        return failure;
    }

    private string? TrySetRunKey(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            // Кавычки обязательны: путь почти наверняка содержит пробел, а Run исполняет строку
            // как командную, и «C:\Program Files\…» без них разбирается как «C:\Program».
            key.SetValue(EntryName, $"\"{executablePath}\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when
            (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            LogRunKeyFailed(ex);
            return $"Не удалось записать автозапуск в реестр: {ex.Message}";
        }
    }

    private string? TryRemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            // throwOnMissingValue: false — снять то, чего нет, это пустая операция, а не отказ.
            key?.DeleteValue(EntryName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) when
            (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            LogRunKeyFailed(ex);
            return $"Не удалось убрать автозапуск из реестра: {ex.Message}";
        }
    }

    [LoggerMessage(LogLevel.Information,
        "Автозапуск согласован: при входе={AtLogon} (ключ Run в HKCU), с правами={Elevated}")]
    partial void LogApplied(bool atLogon, bool elevated);

    [LoggerMessage(LogLevel.Warning, "Не удалось согласовать ключ автозапуска в реестре")]
    partial void LogRunKeyFailed(Exception ex);
}
