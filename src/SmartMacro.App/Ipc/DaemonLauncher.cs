using System.Diagnostics;
using Serilog;

namespace SmartMacro.App.Ipc;

/// <summary>
/// Находит и запускает <c>SmartMacro.Daemon.exe</c>, когда панель открыли, а работающего демона
/// нет.
///
/// Зеркальное отражение демонского <c>UiExecutableLocator</c>, и по той же причине: первым
/// пользователь может запустить любой из двух процессов, значит, каждый обязан уметь поднять
/// другой. В развёрнутом виде оба исполняемых файла лежат в одном каталоге, и весь поиск — это
/// «рядом со мной»; в дереве разработки они разъехались по соседним папкам <c>bin</c>, и это как
/// раз то, что закрывает второй кандидат.
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
    public static IReadOnlyList<string> ProbePaths(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var candidates = new List<string>(2) { Path.Combine(baseDirectory, DaemonExecutableName) };

        // Раскладка разработки: у .../src/SmartMacro.App/bin/Debug/net10.0-windows/ есть
        // близнец на уровень выше по дереву проектов. Подменить имя папки проекта достаточно:
        // сегменты конфигурации и TFM у обоих проектов одинаковые.
        var sibling = SwapProjectFolder(baseDirectory);
        if (sibling is not null)
        {
            candidates.Add(Path.Combine(sibling, DaemonExecutableName));
        }

        return candidates;
    }

    /// <summary>Полный путь к исполняемому файлу демона или <c>null</c>, если его нет ни в одном из мест, куда мы смотрим.</summary>
    /// <param name="baseDirectory">Обычно <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="fileExists">Проба на существование; по умолчанию <see cref="File.Exists(string)"/>.</param>
    public static string? Resolve(string baseDirectory, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        return ProbePaths(baseDirectory).FirstOrDefault(exists);
    }

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

    private static string? SwapProjectFolder(string baseDirectory)
    {
        var normalized = baseDirectory.Replace('/', Path.DirectorySeparatorChar);
        var needle = $"{Path.DirectorySeparatorChar}{AppProjectFolder}{Path.DirectorySeparatorChar}";
        var index = normalized.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        return string.Concat(
            normalized.AsSpan(0, index + 1),
            DaemonProjectFolder,
            normalized.AsSpan(index + needle.Length - 1));
    }
}
