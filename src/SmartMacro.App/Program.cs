using System.Globalization;
using Avalonia;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Settings.Configuration;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Native.Dialogs;

using SmartMacro.Resources;

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
    /// <summary>
    /// Имя необязательного файла конфигурации панели. Суффикс «panel» остался с тех пор, когда
    /// оба exe публиковались в одну папку и одноимённые конфиги затирали бы друг друга; теперь
    /// это просто устоявшееся имя, которое пользователь мог уже знать. Слоя окружения
    /// (вроде <c>appsettings.Development.json</c>) у панели нет вовсе.
    /// </summary>
    private const string PanelConfigurationFileName = "appsettings.panel.json";

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
        // Корень установки нужен ДО журнала: в него же пишется logs\. В поставке панель лежит в
        // корне, так что это её собственная папка; в дереве разработки — папка демона, у которой
        // спрашивают через локатор. Одно правило на оба процесса живёт в InstallationLayout.
        var root = InstallationLayout.RootFromPanelDirectory(
            AppContext.BaseDirectory,
            DaemonLauncher.ResolveDirectory(AppContext.BaseDirectory));

        // Относительные пути (например, в конфиге, который пользователь положил сам) должны
        // разрешаться в корне установки, а не там, откуда процесс случайно запустили. Демон
        // делает ровно то же самое своей папкой; см. комментарий в его Program.
        Directory.SetCurrentDirectory(root);

        // Обёрнуто в try, потому что это ЕДИНСТВЕННОЕ место, падение в котором некуда записать:
        // журнала ещё нет, а у WinExe нет и консоли — до этой правки отказ здесь выходил
        // безмолвным крахом процесса. Портативная раскладка сделала такой отказ достижимым:
        // logs\ лежит в корне установки, и распакованный в C:\Program Files архив роняет сток
        // File прямо на CreateLogger. Демон ту же беду ловит пробой пера (см.
        // BaseDirectoryWriteProbe) — здесь пробы нет намеренно: общей сборки для неё у двух
        // процессов не нашлось (в Contracts файловый ввод-вывод не заезжает по жёсткому правилу,
        // а Native — только P/Invoke), а заводить вторую копию ради того, что и так ловится
        // одним catch, незачем. Заодно сюда же попадает битый appsettings.panel.json.
        try
        {
            Log.Logger = BuildLogger(root);
        }
        catch (Exception ex)
        {
            Win32MessageBox.Error(
                "SmartMacro",
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Dialog_Startup_LoggerFailed,
                    ex.Message));
            return 1;
        }

        try
        {
            // Та же строка, что и у демона, и по той же причине: перепутанный корень — сбой без
            // единого сообщения об ошибке (панель смотрит в один macros\, демон пишет в другой,
            // библиотека кажется пустой). Пусть он будет виден в первых строках журнала.
            Log.Information(
                "Корень установки: {Root} (каталог панели: {PanelDirectory}, раскладка: {Layout})",
                root,
                AppContext.BaseDirectory,
                InstallationLayout.IsShippedLayout ? "поставка" : "дерево разработки");

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
                Win32MessageBox.Error("SmartMacro", Strings.Dialog_Startup_NoDaemon);
                return 1;
            }

            Services = new AppServices(client, root);
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                Log.Information("Avalonia exited, closing the daemon connection");
                var services = Services;
                Services = null;
                services.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    /// <summary>
    /// Журнал панели. Умолчания ЖИВУТ ЗДЕСЬ, а не в файле рядом с exe, потому что панель
    /// публикуется одним файлом: содержимое из бандла не распаковывается, а положить конфиг
    /// рядом — это второй файл в корне поставки, ровно то, чего вся раскладка избегает.
    ///
    /// Внешний <c>appsettings.panel.json</c> при этом читается, ЕСЛИ пользователь положил его
    /// сам, и тогда полностью заменяет умолчания: пришедший чинить свой журнал не должен
    /// разбираться, что из написанного им сложится с зашитым, а что перекроет.
    ///
    /// ⚠️ ЯВНЫЙ список сборок в <see cref="ConfigurationReaderOptions"/> остаётся обязательным, и
    /// теперь по другой причине, чем раньше. Когда оба exe лежали в одной папке,
    /// <c>Serilog.Settings.Configuration</c> перебирал <c>Serilog*.dll</c> рядом с собой, находил
    /// расширения ДЕМОНА и падал на их зависимостях. Общей папки больше нет — зато нет и dll на
    /// диске: в single-file перебирать ему нечего, и <c>WriteTo.File</c> он бы просто не нашёл.
    /// Перечисление снимает вопрос в обоих случаях.
    /// </summary>
    /// <param name="root">Корень установки: в него пишется <c>logs\</c>.</param>
    private static ILogger BuildLogger(string root)
    {
        var configurationPath = Path.Combine(root, PanelConfigurationFileName);
        var logger = new LoggerConfiguration();

        if (File.Exists(configurationPath))
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(root)
                .AddJsonFile(PanelConfigurationFileName, optional: true, reloadOnChange: true)
                .AddEnvironmentVariables(prefix: "SMARTMACRO_")
                .Build();

            logger.ReadFrom.Configuration(
                configuration,
                new ConfigurationReaderOptions(
                    typeof(ConsoleLoggerConfigurationExtensions).Assembly,
                    typeof(FileLoggerConfigurationExtensions).Assembly));
        }
        else
        {
            // Путь к файлу АБСОЛЮТНЫЙ: сток File разрешает относительный по текущему каталогу, а
            // не по своему, и «журнал панели уехал туда, откуда её запустили» — беда, которую
            // потом ищут глазами. Текущий каталог мы, правда, и так прибили к корню, но
            // полагаться на это ради пути, который знаем точно, незачем.
            logger
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .WriteTo.Console(
                    outputTemplate:
                    "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    Path.Combine(root, "logs", "smartmacro-ui-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        return logger.Enrich.FromLogContext().CreateLogger();
    }

    // Используется предпросмотром и дизайнером Avalonia; обязан быть без параметров и
    // называться ровно так.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
