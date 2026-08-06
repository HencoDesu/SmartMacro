using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Serilog;
using SmartMacro.Contracts.Settings;
using SmartMacro.Settings;

namespace SmartMacro.Daemon;

/// <summary>
/// Вторая галочка экрана «Запуск и права» целиком: если настройка просит работать с правами
/// администратора, а процесс их не имеет, демон перезапускает САМ СЕБЯ через <c>runas</c> и
/// уступает место повышенной копии.
///
/// Появилось вместе со сменой манифеста демона на <c>asInvoker</c>. До неё вопрос «есть ли у нас
/// права» не имел смысла — <c>requireAdministrator</c> означает, что без повышения процесс просто
/// не стартует, — а пару «✓ вход / ✓ права» приходилось регистрировать задачей в Планировщике с
/// наивысшими правами, потому что ключ <c>Run</c> так не умеет. Планировщик убран; цена принята
/// вслух: <b>с обеими галочками окно UAC будет появляться при каждом входе в систему</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Место вызова — <c>Program.Main</c>, до построения хоста, и это не вкусовщина.</b>
/// Размещённая служба сделать это не может: <c>StopApplication</c> из <c>StartAsync</c> первой же
/// службы не отменяет запуск остальных, то есть мы успели бы зарегистрировать горячие клавиши,
/// повесить иконку в трее и открыть трубу — ровно в ту секунду, когда то же самое делает
/// повышенная копия. До хоста не построено НИЧЕГО, и уступить место — это <c>return 0</c>.
/// </para>
/// <para>
/// <b>Настройки читаются здесь во второй раз, и это осознанно.</b> Владелец файла —
/// <c>SettingsStore</c>, но он живёт в контейнере, а решение нужно принять раньше. Опасность
/// «двух читателей» в этом проекте — это два РАЗБОРА, которые разъезжаются; здесь разбор один и
/// тот же (<see cref="AppSettingsJson"/>), вызванный дважды с интервалом в доли секунды.
/// Отсутствующий файл даёт умолчания, а в умолчаниях права включены — свежая установка
/// спрашивает UAC, ровно как спрашивала при <c>requireAdministrator</c>.
/// </para>
/// <para>
/// Диагностика идёт через статический <c>Serilog.Log</c>, а не через <c>[LoggerMessage]</c>:
/// контейнера ещё нет, и это тот же канал, которым в этой точке пользуется сам <c>Main</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ElevationRelaunch
{
    /// <summary>
    /// Сколько ждём, пока повышенная копия действительно возьмёт мьютекс. С запасом: запрос UAC
    /// к этому моменту уже отвечен (<c>ShellExecuteEx</c> ждёт согласия сам), остаётся только
    /// холодный старт процесса.
    /// </summary>
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(20);

    private const int PollIntervalMs = 50;

    /// <summary>Код ошибки, которым оболочка сообщает «пользователь нажал „Нет“ в UAC».</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>Чем кончилась попытка уступить место повышенной копии.</summary>
    internal enum Outcome
    {
        /// <summary>Повышение не требуется — либо галочка снята, либо права уже есть.</summary>
        NotRequested,

        /// <summary>Копия поднялась и забрала мьютекс. Этому процессу пора выходить.</summary>
        HandedOver,

        /// <summary>Пользователь отклонил запрос UAC.</summary>
        Refused,

        /// <summary>Запуск копии не удался или копия не поднялась.</summary>
        Failed,
    }

    /// <summary>
    /// Перезапускает демон с повышением, если об этом просит настройка.
    /// </summary>
    /// <param name="installationRoot">Корень установки — там лежит <c>settings.json</c>.</param>
    /// <param name="instance">Захваченный замок единственного экземпляра.</param>
    /// <param name="mutexName">Имя того же замка — по нему проверяем, что копия его забрала.</param>
    /// <param name="reason">Причина отказа для показа человеку; <c>null</c>, кроме <see cref="Outcome.Failed"/>.</param>
    /// <returns>Что произошло; <see cref="Outcome.HandedOver"/> означает «выходим».</returns>
    /// <remarks>
    /// <b>Порядок здесь — и есть содержание класса.</b>
    /// <list type="number">
    ///   <item>
    ///     Мьютекс УЖЕ захвачен вызывающим. Захват идёт первым, чтобы «демон уже работает»
    ///     стоило нам выхода с кодом 0, а не запроса UAC: иначе повторный запуск при живом
    ///     демоне спрашивал бы права, чтобы копия тут же упёрлась в замок и вышла.
    ///   </item>
    ///   <item>
    ///     Мьютекс ОТПУСКАЕТСЯ ДО запуска копии. Копия берёт то же имя, пока мы ещё живы; не
    ///     отпустив, мы получили бы «уже запущено» от собственной копии и остались бы вообще без
    ///     демона.
    ///   </item>
    ///   <item>
    ///     <c>Process.Start</c> с <c>runas</c>. Он блокируется на запросе UAC и приносит отказ
    ///     исключением, так что к этой строке отказ либо уже случился, либо копия уже создана.
    ///   </item>
    ///   <item>
    ///     Ждём, пока имя мьютекса не окажется занято, ИЛИ пока копия не умрёт. Выйти сразу
    ///     после <c>Start</c> нельзя: копия могла упасть на пробе пера или на чужом замке, и
    ///     тогда единственный, кто может об этом сказать, — мы.
    ///   </item>
    ///   <item>
    ///     Не получилось — <b>забираем мьютекс обратно</b> и продолжаем без прав. Продолжать без
    ///     замка нельзя: рядом поднялся бы второй демон.
    ///   </item>
    /// </list>
    /// </remarks>
    public static Outcome TryHandOver(string installationRoot, SingleInstanceGuard instance,
        string mutexName, out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        reason = null;

        if (!AutoStartManager.NeedsElevationRelaunch(ReadSettings(installationRoot)))
        {
            return Outcome.NotRequested;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            reason = "не удалось определить путь к собственному исполняемому файлу";
            Log.Warning("Перезапуск с повышением невозможен: {Reason}", reason);
            return Outcome.Failed;
        }

        Log.Information("Настройки требуют прав администратора, а процесс не повышен — " +
                        "перезапускаем себя через runas");

        // (2) Отпускаем ЗАМОК, и только потом порождаем копию.
        instance.Release();

        Process? copy;
        try
        {
            // (3) UseShellExecute + Verb=runas — единственный способ получить повышение без
            // ручной возни с токенами; оболочка сама покажет запрос и дождётся ответа.
            copy = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Reclaim(mutexName, instance);
            Log.Warning("Запрос прав администратора отклонён пользователем");
            return Outcome.Refused;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or System.IO.FileNotFoundException)
        {
            Reclaim(mutexName, instance);
            reason = ex.Message;
            Log.Warning(ex, "Не удалось запустить повышенную копию демона");
            return Outcome.Failed;
        }

        if (copy is null)
        {
            Reclaim(mutexName, instance);
            reason = "оболочка не вернула процесс";
            Log.Warning("Перезапуск с повышением не удался: {Reason}", reason);
            return Outcome.Failed;
        }

        using (copy)
        {
            // (4) Копия «поднялась» = взяла мьютекс. Опрос, а не повторный захват: захват отобрал
            // бы имя у копии, и та честно решила бы, что демон уже работает.
            if (WaitUntilTaken(copy, mutexName))
            {
                Log.Information("Повышенная копия поднялась (pid {Pid}) — уступаем ей место", copy.Id);
                return Outcome.HandedOver;
            }

            reason = copy.HasExited
                ? $"повышенная копия завершилась с кодом {copy.ExitCode}"
                : "повышенная копия не заявила о себе за отведённое время";
        }

        // (5) Копии нет — замок наш обратно.
        Reclaim(mutexName, instance);
        Log.Warning("Перезапуск с повышением не удался: {Reason}", reason);
        return Outcome.Failed;
    }

    private static bool WaitUntilTaken(Process copy, string mutexName)
    {
        var deadline = DateTime.UtcNow + HandOverTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (SingleInstanceGuard.IsTaken(mutexName))
            {
                return true;
            }

            if (copy.HasExited)
            {
                // Последняя проверка после смерти: копия могла успеть отдать мьютекс третьему —
                // маловероятно, но «умерла» и «не взяла» это разные вещи, и путать их незачем.
                return SingleInstanceGuard.IsTaken(mutexName);
            }

            Thread.Sleep(PollIntervalMs);
        }

        return false;
    }

    // Возврат замка себе. Не удалось — значит, за эти миллисекунды имя занял кто-то ещё; тогда
    // единственных экземпляров два, и продолжать нельзя. Случай теоретический, но молчать о нём
    // хуже, чем написать строчку.
    private static void Reclaim(string mutexName, SingleInstanceGuard instance)
    {
        if (!instance.TryReacquire())
        {
            Log.Warning("Замок единственного экземпляра '{Mutex}' перехвачен другим процессом, " +
                        "пока мы пытались перезапуститься с повышением", mutexName);
        }
    }

    // Один вопрос к файлу настроек до построения контейнера — см. замечание у класса.
    private static AppSettings ReadSettings(string installationRoot)
    {
        var path = Path.Combine(installationRoot, SettingsStore.FileName);
        try
        {
            return File.Exists(path)
                ? AppSettingsJson.Deserialize(File.ReadAllText(path), out _)
                : AppSettings.Default;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException
                                       or UnauthorizedAccessException)
        {
            // Нечитаемый файл — не повод спрашивать или не спрашивать права молча: берём
            // умолчания и говорим об этом. Настоящий разбор с настоящей жалобой сделает
            // SettingsStore через полсекунды.
            Log.Warning(ex, "Файл настроек не читается — права проверяем по умолчаниям: {Path}", path);
            return AppSettings.Default;
        }
    }
}
