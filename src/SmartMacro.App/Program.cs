using Avalonia;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Settings.Configuration;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Native.Dialogs;

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
        // «panel», а не «appsettings.json», потому что в портативной поставке оба exe лежат в
        // ОДНОЙ папке, и одноимённые конфиги двух проектов затирали бы друг друга — кто
        // опубликуется вторым, тот и победил. Победа демона была бы особенно тихой: у него
        // сток File пишет в logs/smartmacro-.log, так что панель начала бы подмешивать свой
        // журнал в журнал движка (у обоих "shared": true, то есть даже не упала бы), и режим
        // «Лог» показывал бы UI-строки как строки демона.
        //
        // Это НЕ файл-оверрайд по окружению вроде appsettings.Development.json: слоя окружения
        // у панели нет вовсе, суффикс здесь просто говорит, чей это конфиг.
        //
        // Обёрнуто в try, потому что это ЕДИНСТВЕННОЕ место, падение в котором некуда записать:
        // журнала ещё нет, а у WinExe нет и консоли — до этой правки отказ здесь выходил
        // безмолвным крахом процесса. Портативная раскладка сделала такой отказ достижимым:
        // logs\ панели лежит рядом с её exe, и распакованный в C:\Program Files архив роняет
        // сток File прямо на CreateLogger. Демон ту же беду ловит пробой пера (см.
        // BaseDirectoryWriteProbe) — здесь пробы нет намеренно: общей сборки для неё у двух
        // процессов не нашлось (в Contracts файловый ввод-вывод не заезжает по жёсткому правилу,
        // а Native — только P/Invoke), а заводить вторую копию ради того, что и так ловится
        // одним catch, незачем. Заодно сюда же попадает битый appsettings.panel.json.
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.panel.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.panel.local.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables(prefix: "SMARTMACRO_")
                .Build();

            // ЯВНЫЙ список сборок вместо поиска по папке — требование портативной раскладки, а
            // не вкусовщина. Serilog.Settings.Configuration, когда ему не сказали, где искать
            // методы вроде WriteTo.File, перебирает Serilog*.dll РЯДОМ С СОБОЙ. Пока у панели
            // была своя папка, это было безобидно. В общей папке он находит серилоговские
            // расширения ДЕМОНА (Serilog.Extensions.Hosting, Serilog.Extensions.Logging),
            // грузит их и спотыкается: их зависимости есть у демона и отсутствуют в
            // SmartMacro.App.deps.json, а значит, для этого процесса недоступны. Панель падала
            // ровно здесь, на CreateLogger, ещё до первой своей строки в журнале.
            //
            // Перечисление убирает перебор целиком: читаются только те две сборки, чьи методы
            // реально названы в appsettings.panel.json. Демону зеркальная защита не нужна — его
            // набор Serilog'а надмножество панельного, так что перебор не находит у него ничего
            // нового.
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(
                    configuration,
                    new ConfigurationReaderOptions(
                        typeof(ConsoleLoggerConfigurationExtensions).Assembly,
                        typeof(FileLoggerConfigurationExtensions).Assembly))
                .Enrich.FromLogContext()
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Win32MessageBox.Error(
                "SmartMacro",
                "Не удалось поднять журнал панели.\n\n" +
                $"{ex.Message}\n\n" +
                "Чаще всего это каталог программы, недоступный для записи: SmartMacro хранит " +
                "журналы и макросы рядом со своими исполняемыми файлами. Распакуйте папку туда, " +
                "куда можно писать, и запустите ещё раз.");
            return 1;
        }

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
