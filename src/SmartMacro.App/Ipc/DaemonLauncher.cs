using System.Diagnostics;
using Serilog;

namespace SmartMacro.App.Ipc;

/// <summary>
/// Finds and starts <c>SmartMacro.Daemon.exe</c> when the panel is opened without one
/// running.
///
/// The mirror image of the daemon's <c>UiExecutableLocator</c>, and for the same reason:
/// either process may be the one the user launches first, so each has to be able to bring
/// the other up. Deployed, the two executables sit in one directory and "next to me" is the
/// whole search; in the dev tree they are in sibling <c>bin</c> folders, which is what the
/// second candidate covers.
/// </summary>
public static class DaemonLauncher
{
    /// <summary>File name of the daemon process, as produced by <c>SmartMacro.Daemon.csproj</c>.</summary>
    public const string DaemonExecutableName = "SmartMacro.Daemon.exe";

    private const string AppProjectFolder = "SmartMacro.App";
    private const string DaemonProjectFolder = "SmartMacro.Daemon";

    /// <summary>
    /// Paths that are checked, in order. Exposed so a failed <see cref="Resolve"/> can be
    /// logged with what was actually looked at.
    /// </summary>
    public static IReadOnlyList<string> ProbePaths(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var candidates = new List<string>(2) { Path.Combine(baseDirectory, DaemonExecutableName) };

        // Dev layout: .../src/SmartMacro.App/bin/Debug/net10.0-windows/ has a twin one
        // directory up the project tree. Swapping the project folder name is enough — the
        // configuration and TFM segments are identical for both projects.
        var sibling = SwapProjectFolder(baseDirectory);
        if (sibling is not null)
        {
            candidates.Add(Path.Combine(sibling, DaemonExecutableName));
        }

        return candidates;
    }

    /// <summary>Full path of the daemon executable, or <c>null</c> when it isn't anywhere we look.</summary>
    /// <param name="baseDirectory">Normally <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="fileExists">Existence probe; defaults to <see cref="File.Exists(string)"/>.</param>
    public static string? Resolve(string baseDirectory, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        return ProbePaths(baseDirectory).FirstOrDefault(exists);
    }

    /// <summary>
    /// Starts the daemon and returns <c>true</c> when the process was created. Says nothing
    /// about readiness — the caller then retries the IPC connect until the pipe appears.
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
            // UseShellExecute so the shell honours the daemon's requireAdministrator
            // manifest instead of us hand-rolling an elevated token.
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
            // A refused UAC prompt lands here as Win32Exception(ERROR_CANCELLED).
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
