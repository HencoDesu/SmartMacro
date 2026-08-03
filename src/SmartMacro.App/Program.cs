using Avalonia;
using Microsoft.Extensions.Configuration;
using Serilog;
using SmartMacro.App.Interop;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App;

/// <summary>
/// Точка входа панели.
///
/// Стадия 3 превратила этот файл из корня композиции в загрузчик. Движка здесь больше нет —
/// ни монитора процессов, ни хоткеев, ни vision, ни хранилища макросов, — только соединение с
/// <c>SmartMacro.Daemon</c>, которому всё это и принадлежит. В этом весь смысл разделения:
/// OpenCV, Tesseract и их нативные блобы больше не грузятся в процесс, который пользователь
/// открывает и закрывает по десять раз на дню.
///
/// Запуск, по порядку:
///
///   1. <b>Единственный экземпляр.</b> Второй запуск не имеет права открыть второе окно. Он
///      подключается, просит демона разослать <c>ActivateWindow</c> уже работающей панели и
///      выходит, — так что повторный клик по ярлыку читается как «покажи мне панель».
///   2. <b>Или демон, или ничего.</b> Если никто не слушает, мы запускаем демона сами и
///      продолжаем стучаться (ему целый движок собирать). По-прежнему пусто ⇒ окно с ошибкой
///      и выход с кодом 1: панель без демона ничего не покажет и ничего не изменит.
///   3. <b>Avalonia.</b> Закрытие окна теперь завершает процесс: трей переехал к демону, и
///      автоматизация продолжает работать без нас.
/// </summary>
internal static class Program
{
    // Хватает на случай, когда демон уже поднят (он либо отвечает сразу, либо его нет).
    private static readonly TimeSpan ExistingDaemonWindow = TimeSpan.FromSeconds(2);

    // Хватает на холодный старт: корень композиции, загрузка библиотеки макросов, регистрация
    // хоткеев.
    private static readonly TimeSpan LaunchedDaemonWindow = TimeSpan.FromSeconds(20);

    // Второй экземпляр разговаривает с демоном, который заведомо работает (первому экземпляру
    // он тоже был нужен), так что здесь надо перекрыть разве что занятую трубу.
    private static readonly TimeSpan ActivateWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Держатель служб на весь процесс. Ставится один раз до старта Avalonia, очищается на
    /// выходе. Окна и диалоги добираются до своих зависимостей через него.
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
    /// Путь второго экземпляра: толкнуть демона и уйти. Окно не открывается никогда, и запуск
    /// никогда не считается неудачным: если до демона не достучаться, пользователю, у которого
    /// панель и так на экране, сказать нечего.
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

    // Папка с макросами принадлежит демону, поэтому её ищут относительно его исполняемого
    // файла, — а в дереве разработки это соседний каталог bin, а не наш.
    private static string ResolveMacroFolder()
    {
        var daemon = DaemonLauncher.Resolve(AppContext.BaseDirectory);
        var directory = daemon is null
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(daemon) ?? AppContext.BaseDirectory;
        return Path.Combine(directory, "macros");
    }

    // Используется предпросмотром и дизайнером Avalonia; обязан быть без параметров и
    // называться ровно так.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
