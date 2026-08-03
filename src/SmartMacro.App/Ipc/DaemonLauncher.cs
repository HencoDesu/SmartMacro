using System.Diagnostics;
using Serilog;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Ipc;

/// <summary>
/// Находит и запускает <c>SmartMacro.Daemon.exe</c>, когда панель открыли, а работающего демона
/// нет.
///
/// Зеркальное отражение демонского <c>UiExecutableLocator</c>, и по той же причине: первым
/// пользователь может запустить любой из двух процессов, значит, каждый обязан уметь поднять
/// другой. Сам поиск живёт в <see cref="PeerExecutableLocator"/> — здесь только имена и запуск.
/// </summary>
public static class DaemonLauncher
{
    /// <summary>Имя файла процесса демона в том виде, в каком его выдаёт <c>SmartMacro.Daemon.csproj</c>.</summary>
    public const string DaemonExecutableName = "SmartMacro.Daemon.exe";

    private const string AppProjectFolder = "SmartMacro.App";
    private const string DaemonProjectFolder = "SmartMacro.Daemon";

    /// <summary>
    /// Пути, которые проверяются, по порядку. Выставлено наружу, чтобы неудачу <see cref="Resolve"/>
    /// можно было записать в лог вместе с тем, куда на самом деле смотрели.
    /// </summary>
    public static IReadOnlyList<string> ProbePaths(string baseDirectory) =>
        PeerExecutableLocator.ProbePaths(baseDirectory, DaemonExecutableName, AppProjectFolder, DaemonProjectFolder);

    /// <summary>Полный путь к исполняемому файлу демона или <c>null</c>, если его нет ни в одном из мест, куда мы смотрим.</summary>
    /// <param name="baseDirectory">Обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="fileExists">Проба на существование; по умолчанию <see cref="File.Exists(string)"/>.</param>
    public static string? Resolve(string baseDirectory, Func<string, bool>? fileExists = null) =>
        PeerExecutableLocator.Resolve(
            baseDirectory, DaemonExecutableName, AppProjectFolder, DaemonProjectFolder, fileExists ?? File.Exists);

    /// <summary>
    /// Запускает демона и возвращает <c>true</c>, когда процесс создан. О готовности не говорит
    /// ничего: дальше вызывающий повторяет попытки подключиться по IPC, пока не появится труба.
    /// </summary>
    public static bool TryStart(string baseDirectory)
    {
        var path = Resolve(baseDirectory);
        if (path is null)
        {
            Log.Error(
                "Не найден исполняемый файл демона — искали: {Paths}",
                string.Join(", ", ProbePaths(baseDirectory)));
            return false;
        }

        try
        {
            // UseShellExecute — чтобы оболочка уважила манифест демона с requireAdministrator,
            // а нам не пришлось вручную мастерить токен с повышенными правами.
            var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? baseDirectory,
            });

            if (process is null)
            {
                Log.Warning("Process.Start вернул null для '{Path}' — демон не запущен", path);
                return false;
            }

            Log.Information("Демон запущен: '{Path}' (pid {Pid})", path, process.Id);
            return true;
        }
        catch (Exception ex)
        {
            // Отклонённый запрос UAC прилетает сюда как Win32Exception(ERROR_CANCELLED).
            Log.Error(ex, "Не удалось запустить демона '{Path}'", path);
            return false;
        }
    }
}
