using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SmartMacro.Contracts.Settings;
using SmartMacro.Native.Diagnostics;
using SmartMacro.Resources;

namespace SmartMacro.Settings;

/// <summary>
/// Единственная часть настроек, которая применяется НЕ в процессе: две галочки «запускать при
/// входе» и «работать с правами администратора» превращаются в записи в реестре и в Планировщике
/// заданий.
///
/// Галочек две, а механизмов три, и выбирает между ними приложение — потому что пользователю
/// незачем знать, что ключ <c>Run</c> не умеет поднимать процесс с повышением:
///
/// <list type="table">
///   <listheader><term>вход / права</term><description>что регистрируется</description></listheader>
///   <item><term>— / —</term><description>ничего; обе записи снимаются</description></item>
///   <item><term>✓ / —</term><description>ключ <c>Run</c> в <c>HKCU</c>; задача снимается</description></item>
///   <item><term>— / ✓</term><description>ничего не регистрируется; повышение — забота запуска (см. <see cref="NeedsElevationRelaunch"/>)</description></item>
///   <item><term>✓ / ✓</term><description>задача в Планировщике с наивысшими правами; ключ <c>Run</c> снимается</description></item>
/// </list>
///
/// <b>Согласование всегда полное, а не «добавить нужное».</b> Каждый вызов
/// <see cref="ApplyAsync"/> приводит В ОБА места в состояние, отвечающее галочкам, — в том числе
/// удаляет то, чего быть не должно. Иначе переключение с ✓/— на ✓/✓ оставило бы и ключ, и задачу,
/// и демон поднимался бы дважды: одна из копий сразу упёрлась бы в мьютекс единственного
/// экземпляра и молча вышла, а пользователь получил бы лишний диалог UAC при каждом входе и
/// никакого объяснения.
///
/// Планировщик дёргается через <c>schtasks.exe</c>, а не через COM-интерфейс: COM потребовал бы
/// пакета или ручного interop ради четырёх команд, которые тут нужны, а CLI на месте в любой
/// Windows и разбирается по коду возврата.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AutoStartManager
{
    /// <summary>Имя значения в ключе <c>Run</c> и имя задачи в Планировщике — одно и то же.</summary>
    public const string EntryName = "SmartMacro";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Планировщик ждёт ответа своей утилиты; десять секунд — заведомо с запасом для локальной
    // операции, но не «навсегда», если служба задач больна.
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<AutoStartManager> _logger;

    public AutoStartManager(ILogger<AutoStartManager> logger) => _logger = logger;

    /// <summary>
    /// Надо ли перезапустить себя с повышением прямо сейчас: настройка требует прав, а процесс их
    /// не имеет.
    ///
    /// Отдельно от <see cref="ApplyAsync"/> потому, что это вопрос СТАРТА, а не согласования
    /// записей: повышение нельзя получить на лету, поэтому смена галочки прав вступает в силу
    /// только со следующего запуска демона — и интерфейс обязан это сказать, иначе пользователь
    /// снимет галочку, увидит чистую панель и будет неделю искать, почему макросы перестали
    /// доходить до игры.
    /// </summary>
    /// <param name="settings">Текущие настройки.</param>
    public static bool NeedsElevationRelaunch(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Startup.RunElevated && !EnvironmentProbe.IsElevated();
    }

    /// <summary>
    /// Приводит реестр и Планировщик в соответствие галочкам.
    /// </summary>
    /// <param name="settings">Настройки, чей раздел <c>Startup</c> надо применить.</param>
    /// <param name="executablePath">Полный путь к исполняемому файлу демона.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>
    /// <c>null</c> — согласовано. Строка — человекочитаемая причина, по которой не вышло; сюда же
    /// попадает отказ Планировщика. НЕ бросает: автозапуск не настроился — это неприятность, а не
    /// повод не поднять движок.
    /// </returns>
    public async Task<string?> ApplyAsync(AppSettings settings, string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var wantsTask = settings.Startup is { RunAtLogon: true, RunElevated: true };
        var wantsRunKey = settings.Startup is { RunAtLogon: true, RunElevated: false };

        string? failure = null;

        // Сначала ставим то, что нужно, и только потом снимаем лишнее: если постановка не
        // удалась, у пользователя хотя бы остаётся прежний рабочий автозапуск, а не ни одного.
        if (wantsRunKey)
        {
            failure ??= TrySetRunKey(executablePath);
        }

        if (wantsTask)
        {
            failure ??= await TryCreateTaskAsync(executablePath, cancellationToken).ConfigureAwait(false);
        }

        if (!wantsRunKey)
        {
            failure ??= TryRemoveRunKey();
        }

        if (!wantsTask)
        {
            // Отсутствие задачи — не ошибка: удаление несуществующей возвращает ненулевой код, и
            // разбирать его как отказ значило бы ругаться на каждый старт со снятой галочкой.
            await TryDeleteTaskAsync(cancellationToken).ConfigureAwait(false);
        }

        LogApplied(settings.Startup.RunAtLogon, settings.Startup.RunElevated, Describe(wantsRunKey, wantsTask));
        return failure;
    }

    /// <summary>
    /// Совпадает ли то, что зарегистрировано в системе, с тем, что просят галочки.
    ///
    /// <b>Нужно потому, что регистрация может не удаться, а панель об этом не узнает.</b>
    /// Задачу в Планировщике с наивысшими правами создаёт только администратор, и на
    /// непривилегированном демоне <c>schtasks</c> отвечает отказом — раньше это уходило строкой в
    /// журнал, которого никто не читает, и получалось ровно то, что волна D4 чинила у горячих
    /// клавиш: пользователь ставит галочку, видит чистый интерфейс, и не происходит ничего.
    /// Поэтому расхождение стало проверкой среды.
    ///
    /// Обе проверки читающие и прав не требуют — в отличие от самой регистрации.
    /// </summary>
    /// <param name="settings">Настройки, чей раздел <c>Startup</c> проверяется.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<bool> IsRegisteredAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var wantsTask = settings.Startup is { RunAtLogon: true, RunElevated: true };
        var wantsRunKey = settings.Startup is { RunAtLogon: true, RunElevated: false };

        if (wantsRunKey != HasRunKey())
        {
            return false;
        }

        var (code, _) = await RunSchtasksAsync(["/Query", "/TN", EntryName], cancellationToken).ConfigureAwait(false);
        return wantsTask == (code == 0);
    }

    private static bool HasRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(EntryName) is not null;
        }
        catch (Exception ex) when
            (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string Describe(bool runKey, bool task) => (runKey, task) switch
    {
        (true, _) => "ключ Run в HKCU",
        (_, true) => "задача в Планировщике, наивысшие права",
        _ => "ничего не регистрируется",
    };

    // ---- реестр -----------------------------------------------------------------------------

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

    // ---- Планировщик ------------------------------------------------------------------------

    private async Task<string?> TryCreateTaskAsync(string executablePath, CancellationToken cancellationToken)
    {
        // /RL HIGHEST — то самое, чего не умеет ключ Run: задача поднимает процесс с повышением
        // БЕЗ диалога UAC. /F перезаписывает существующую, чтобы смена пути exe (портативная
        // раскладку переехала в другую папку) подхватывалась сама.
        var (code, output) = await RunSchtasksAsync(
            ["/Create", "/TN", EntryName, "/TR", $"\"{executablePath}\"", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"],
            cancellationToken).ConfigureAwait(false);

        if (code == 0)
        {
            return null;
        }

        LogTaskFailed(code, output);
        return "Не удалось создать задачу в Планировщике "
               + $"(schtasks вернул {code}). Права администратора нужны и для самой регистрации.";
    }

    private async Task TryDeleteTaskAsync(CancellationToken cancellationToken) =>
        await RunSchtasksAsync(["/Delete", "/TN", EntryName, "/F"], cancellationToken).ConfigureAwait(false);

    private async Task<(int Code, string Output)> RunSchtasksAsync(string[] arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return (-1, "schtasks.exe не запустился");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SchtasksTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return (process.ExitCode, string.Concat(stdout, stderr).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or OperationCanceledException)
        {
            return (-1, ex.Message);
        }
    }

    [LoggerMessage(LogLevel.Information,
        "Автозапуск согласован: при входе={AtLogon} с правами={Elevated} → {Mechanism}")]
    partial void LogApplied(bool atLogon, bool elevated, string mechanism);

    [LoggerMessage(LogLevel.Warning, "Не удалось согласовать ключ автозапуска в реестре")]
    partial void LogRunKeyFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "schtasks вернул {Code}: {Output}")]
    partial void LogTaskFailed(int code, string output);
}
