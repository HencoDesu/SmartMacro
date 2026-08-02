using Avalonia;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartMacro.App.Interop;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App;

/// <summary>
/// The panel's entry point.
///
/// Stage 3 turned this file from a composition root into a bootstrapper. There is no engine
/// here any more — no process monitor, no hotkeys, no vision, no macro store — only a
/// connection to <c>SmartMacro.Daemon</c>, which owns all of it. That is the whole point of
/// the split: OpenCV, Tesseract and their native blobs no longer load into the process the
/// user opens and closes all day.
///
/// Startup, in order:
///
///   1. <b>Single instance.</b> A second launch must not open a second window. It connects,
///      asks the daemon to broadcast <c>ActivateWindow</c> at the panel already running, and
///      exits — so re-running the shortcut reads as "show me the panel".
///   2. <b>Daemon or bust.</b> If nothing is listening we start the daemon ourselves and
///      keep retrying the connect (it has a whole engine to build). Still nothing ⇒ an error
///      box and exit 1, because a panel with no daemon can display nothing and change nothing.
///   3. <b>Avalonia.</b> Closing the window now exits the process — the tray moved to the
///      daemon, and automation keeps running without us.
/// </summary>
internal static class Program
{
    // Enough for the daemon to be already up (it either answers at once or isn't there).
    private static readonly TimeSpan ExistingDaemonWindow = TimeSpan.FromSeconds(2);

    // Enough for a cold start: composition root, macro library load, hotkey registration.
    private static readonly TimeSpan LaunchedDaemonWindow = TimeSpan.FromSeconds(20);

    // A second instance is talking to a daemon that is provably running (the first instance
    // needed it too), so this only has to cover a busy pipe.
    private static readonly TimeSpan ActivateWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The process-wide service holder. Set once before Avalonia starts, cleared on exit.
    /// Windows and dialogs reach their dependencies through it.
    /// </summary>
    public static AppServices? Services { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "SMARTMACRO_")
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .CreateLogger();

        try
        {
            using var instance = SingleInstanceLock.TryAcquire(SingleInstanceLock.AppMutexName);
            if (instance is null)
            {
                Log.Information("Панель уже запущена — просим её выйти на передний план");
                return ActivateRunningPanel();
            }

            Log.Information("SmartMacro UI starting");

            var client = new IpcClient();
            if (!ConnectToDaemon(client))
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Win32MessageBox.Error(
                    "SmartMacro",
                    "Не удалось подключиться к службе SmartMacro.\n\n" +
                    "Запустите SmartMacro.Daemon.exe вручную и откройте панель ещё раз.\n" +
                    "Подробности — в logs/smartmacro-ui-*.log.");
                return 1;
            }

            Services = new AppServices(client, ResolveMacroFolder());
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                Log.Information("Avalonia exited, closing the daemon connection");
                var services = Services;
                Services = null;
                services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "SmartMacro UI terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Second-instance path: poke the daemon and leave. Never opens a window, and never
    /// fails the launch — if the daemon is unreachable there is nothing useful to say to a
    /// user who already has a panel on screen.
    /// </summary>
    private static int ActivateRunningPanel()
    {
        var client = new IpcClient();
        try
        {
            if (!client.StartAsync(ActivateWindow).GetAwaiter().GetResult())
            {
                Log.Warning("Демон недоступен — активировать существующую панель нечем");
                return 0;
            }

            client.RequestAsync(IpcMessageTypes.RequestActivate).GetAwaiter().GetResult();
            Log.Information("Запрошена активация уже запущенной панели");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось запросить активацию панели");
        }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return 0;
    }

    private static bool ConnectToDaemon(IpcClient client)
    {
        if (client.StartAsync(ExistingDaemonWindow).GetAwaiter().GetResult())
        {
            return true;
        }

        Log.Information("Демон не отвечает — запускаем его");
        if (!DaemonLauncher.TryStart(AppContext.BaseDirectory))
        {
            return false;
        }

        return client.StartAsync(LaunchedDaemonWindow).GetAwaiter().GetResult();
    }

    // The macro folder belongs to the daemon, so it is resolved relative to the daemon's
    // executable — which in the dev tree is a sibling bin directory, not ours.
    private static string ResolveMacroFolder()
    {
        var daemon = DaemonLauncher.Resolve(AppContext.BaseDirectory);
        var directory = daemon is null
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(daemon) ?? AppContext.BaseDirectory;
        return Path.Combine(directory, "macros");
    }

    // Used by the Avalonia previewer/designer; must be parameterless and named exactly this way.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
