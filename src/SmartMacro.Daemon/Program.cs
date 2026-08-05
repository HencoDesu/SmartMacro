using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Daemon.Logging;
using SmartMacro.GameWindows;
using SmartMacro.Hotkeys;
using SmartMacro.Input;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Storage;
using SmartMacro.Native.Dialogs;
using SmartMacro.Native.Hotkey;
using SmartMacro.Native.Tray;
using SmartMacro.Orchestration;
using SmartMacro.Presentation;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Settings;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Daemon;

// Резидентная половина SmartMacro: WinExe без окон, в котором живёт весь движок — слежение за
// процессами, библиотека макросов, регистрация горячих клавиш, машинное зрение, отправка
// ввода — плюс иконка в трее на голом Win32. Ни Avalonia, ни XAML, ни цикла отрисовки;
// интерфейс — отдельный процесс, который трей запускает по требованию и который пользователь
// может закрыть, не останавливая автоматизацию.
//
// Это ЕДИНСТВЕННЫЙ процесс, в котором живёт движок. Стадия 3 ужала SmartMacro.App до
// IPC-клиента вообще без ссылки на SmartMacro.Core, чем сняла опасность стадии 2A — два корня
// композиции, дерущиеся за RegisterHotKey, хук мыши и окна игры.
//
// Управляющая точка и есть весь интерфейс: IpcServer слушает named pipe «smartmacro-control»
// (JSON Lines, много клиентов) и открывает движок панели — снимки окон и тегов и пуши о них,
// CRUD и запуск макросов, приостановка и возобновление горячих клавиш, сброс снимков экрана,
// «выведи панель на передний план», завершение работы. Всё, что нужно интерфейсу, — это тип
// сообщения здесь, а не вторая копия движка там.
internal static class Program
{
    public static int Main(string[] args)
    {
        // Заявляем права единственного экземпляра ДО всего дорогого: второй демон удвоил бы
        // каждый глобальный побочный эффект этого процесса (горячие клавиши, хуки, ввод).
        using var instance = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.DaemonMutexName);

        // Проба пера сразу за мьютексом и ДО конфигурации с логгером — иначе сообщать не через
        // что. Раскладка портативная: macros\, settings.json и debug\ живут в корне установки,
        // logs\ — в папке самого демона, так что нераспакованный в пишущееся место архив
        // (C:\Program Files, сетевая шара) ломает всё сразу и молча. Молча — потому что сток File
        // у Serilog глотает отказ, а строить логгер раньше пробы нельзя ещё и технически: он сам
        // первым делом полезет создавать logs\.
        //
        // Каталогов ДВА, и проверяются оба: корень — потому что в нём данные пользователя, своя
        // папка — потому что в ней журнал. Один вместо двух означал бы «проверили права там, где
        // ничего не пишем». В дереве разработки это один и тот же путь, поэтому вторая проба
        // отсеивается сравнением, а не делается впустую.
        //
        // Единственный доступный в этой точке канал — нативное окно: консоли у WinExe нет,
        // журнала ещё нет. Строка в stderr — не дубликат, а подстраховка для `dotnet run`, где
        // консоль как раз есть.
        //
        // Отвергнутый второй экземпляр тоже проходит пробу: пара файловых операций дешевле
        // условия, которое пришлось бы объяснять. Имена проб разведены по pid, так что
        // одновременный запуск двух демонов за одно имя не спорит.
        var installationRoot = InstallationLayout.RootFromDaemonDirectory(AppContext.BaseDirectory);
        foreach (var directory in Writable(installationRoot, AppContext.BaseDirectory))
        {
            if (BaseDirectoryWriteProbe.TryVerifyWritable(directory, out var writeFailure))
            {
                continue;
            }

            var message = BaseDirectoryWriteProbe.DescribeFailure(directory, writeFailure);
            Console.Error.WriteLine(message);
            Win32MessageBox.Error("SmartMacro", message);
            return 2;
        }

        // Относительные пути становятся предсказуемыми: у стока File в appsettings.json путь
        // относительный (logs/smartmacro-.log), а разрешает он его по ТЕКУЩЕМУ каталогу, а не по
        // своему. При автозапуске через ключ Run текущим каталогом оказывается system32, и журнал
        // молча уезжает туда (или не пишется вовсе — сток File глотает отказ). Один вызов
        // прибивает его к папке демона, то есть logs\ у демона всегда СВОЙ, внутри daemon\.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        var bootstrapConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "SMARTMACRO_")
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(bootstrapConfiguration)
            .CreateBootstrapLogger();

        // Рубильник уровня журнала. Заводится ЗДЕСЬ, из appsettings.json, и это принципиально:
        // уровень намеренно не переехал в settings.json вместе с остальными ручками. Если демон
        // споткнётся на чтении файла настроек, расследовать это можно будет только тем уровнем,
        // который был известен ДО чтения; настройка, ломающая диагностику собственной поломки,
        // не нужна ни в каком виде. Экран настроек двигает этот объект на лету (SetLogLevel),
        // ничего не переписывая, — и обязан сказать, что до перезапуска демона.
        var levelSwitch = new LoggingLevelSwitch(ReadConfiguredLevel(bootstrapConfiguration));

        if (instance is null)
        {
            Log.Information(
                "SmartMacro daemon already running (mutex {Mutex} held) — exiting",
                SingleInstanceGuard.DaemonMutexName);
            Log.CloseAndFlush();
            return 0;
        }

        try
        {
            Log.Information("SmartMacro daemon starting");

            // ОБЯЗАТЕЛЬНАЯ строка, а не украшение диагностики. Ошибка в вычислении корня —
            // молчаливая и дорогая: демон заведёт себе macros\ рядом с собой, панель будет
            // смотреть в другие, библиотека окажется пустой, и ни одного сообщения об ошибке при
            // этом не появится. Проба пера этого не ловит по построению — она про права, а не про
            // адрес. Печатаем обе величины: по каталогу демона видно, какая раскладка опознана.
            Log.Information(
                "Корень установки: {Root} (каталог демона: {DaemonDirectory}, раскладка: {Layout})",
                installationRoot,
                AppContext.BaseDirectory,
                InstallationLayout.IsShippedLayout ? "поставка" : "дерево разработки");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddConfiguration(bootstrapConfiguration);

            builder.Services.AddSerilog((sp, lc) => lc
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(sp)
                .Enrich.FromLogContext()
                // Третий адресат журнала, рядом с консолью и файлом: лента режима «Лог» у
                // панели. Разрешается отсюда, а не описывается в appsettings.json, потому что
                // стоку нужен синглтон из этого же контейнера.
                //
                // Цикла в DI тут нет ровно потому, что ни у стока, ни у публикатора НЕТ логгера
                // — иначе построение логгера потребовало бы логгера. Это же и есть первый срез
                // защиты от рекурсии; см. IpcLogSink и LogEventPublisher.
                //
                // Записи, сделанные до сборки хоста (строка «daemon starting» и отказ второго
                // экземпляра), уходят в bootstrap-логгер, у которого этого стока ещё нет, и в
                // ленту не попадают — проверено глазами, лента начинается со следующей строки,
                // «Composition root built». Их видно в консоли и в файле, где им и место: панель
                // не может быть подключена к демону, который ещё не поднял канал.
                .WriteTo.Sink(new IpcLogSink(sp.GetRequiredService<LogEventPublisher>()))
                // ПОСЛЕ ReadFrom.Configuration, и порядок здесь значим: тот вызов уже выставил
                // MinimumLevel из appsettings.json, а этот подменяет его управляемым рубильником,
                // засеянным тем же значением. Наоборот — и настройка уровня на лету молча
                // перестала бы работать.
                //
                // MinimumLevel:Override из конфига при этом остаются в силе и рубильнику НЕ
                // подчиняются (у каждого свой переключатель). Так и надо: «Microsoft: Warning»
                // — это про чужой шум, и опускать его вместе со своим уровнем незачем.
                .MinimumLevel.ControlledBy(levelSwitch));

            ConfigureServices(builder.Services, levelSwitch);

            using var host = builder.Build();

            Log.Information("Composition root built, running host");
            host.RunAsync().GetAwaiter().GetResult();
            Log.Information("SmartMacro daemon stopped");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "SmartMacro daemon terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Каталоги, которые обязаны быть доступны на запись, без повторов: в дереве разработки
    /// корень установки и папка демона — одно и то же место.
    /// </summary>
    /// <remarks>
    /// Хвостовой разделитель снимается перед сравнением: <see cref="AppContext.BaseDirectory"/>
    /// приходит с ним, а корень его уже не имеет, и без нормализации «одно и то же место»
    /// сравнением не поймалось бы никогда.
    /// </remarks>
    private static IEnumerable<string> Writable(string root, string daemonDirectory) =>
        new[] { root, daemonDirectory }
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Начальный уровень для рубильника — тот, что записан в <c>appsettings.json</c>.
    ///
    /// Читается строкой, а не через <c>ReadFrom.Configuration</c>, потому что рубильник нужен
    /// ДО сборки конвейера: он в него и вставляется. Незнакомое или отсутствующее значение даёт
    /// <see cref="LogEventLevel.Information"/> — то же, что подставил бы Serilog.
    /// </summary>
    private static LogEventLevel ReadConfiguredLevel(IConfiguration configuration) =>
        Enum.TryParse<LogEventLevel>(configuration["Serilog:MinimumLevel:Default"], ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Information;

    private static void ConfigureServices(IServiceCollection services, LoggingLevelSwitch levelSwitch)
    {
        // --- Настройки -----------------------------------------------------------------
        //
        // Пришли на смену четырём Configure<T> из appsettings.json. Разница не в том, ГДЕ лежат
        // значения, а в том, что IOptions<T> вычислялся один раз на старте: любая правка требовала
        // перезапуска демона, и это стояло в CLAUDE.md отдельной ловушкой. Теперь файлом владеет
        // демон, панель правит его по IPC, наблюдатель подхватывает правку блокнотом, а
        // потребители читают ISettingsSource.Current В МОМЕНТ ИСПОЛЬЗОВАНИЯ.
        //
        // Хранилище зарегистрировано дважды — собой и как ISettingsSource — намеренно: писать
        // вправе только диспетчер, которому нужен конкретный тип, а читателям достаётся интерфейс
        // без записи. (Отсюда же двойной Dispose, к которому хранилище готово.)
        //
        // В appsettings.json остался ОДИН Serilog. Уровень журнала не переехал сюда сознательно —
        // см. рубильник в Main.
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ISettingsSource>(sp => sp.GetRequiredService<SettingsStore>());
        services.AddSingleton<ILogLevelSwitch>(_ => new SerilogLevelSwitch(levelSwitch));
        services.AddSingleton<SettingsSnapshotProvider>();
        services.AddSingleton<AutoStartManager>();
        services.AddSingleton<EnvironmentDiagnostics>();

        // Выбор способа доставки нажатия по настройкам, на каждое нажатие. Синглтон, потому что
        // держит обе реализации ввода полями: переключение способа не должно ничего выделять.
        services.AddSingleton<KeyboardInputResolver>();

        // GameWindowFactory зашивает в себя пробуждение через WM_ACTIVATEAPP и то, что мышь
        // всегда идёт PostMessage, чтобы ни DI, ни оркестратору не нужно было знать о типах из
        // Native. Win32NativeWindowSystem — статический класс, регистрировать в DI нечего.
        services.AddSingleton<IGameWindowFactory, GameWindowFactory>();

        // Единственный владелец тегов окон И поиска hwnd → IGameWindow: всё остальное —
        // оркестратор, примитивы макросов, слежение за временем жизни окон, а со стадии 2B и
        // слой IPC — читает состояние окон отсюда.
        services.AddSingleton<WindowRegistry>();

        services.AddSingleton<IClassMatcher, ClassMatcher>();
        // ВНИМАНИЕ: ICoordinateReader/TesseractCoordinateReader намеренно НЕ регистрируется
        // (стадия 4B). В демоне его никто не внедряет, а конструктор жадно открывает
        // TesseractEngine — то есть регистрация стоила бы первому же запросившему компоненту
        // загрузки нативных leptonica и tesseract плюс чтения 4 МБ eng.traineddata. Сам
        // читатель отложен до будущей работы над детектом застреваний и по-прежнему
        // запускается в tools/VisionSampleRunner; расконсервировать его — значит вернуть эту
        // строку И снять защиту PrivateAssets с пакета Tesseract в SmartMacro.Core.csproj.
        services.AddSingleton<WindowIconService>();
        services.AddSingleton<AgentInputDispatcher>();
        services.AddSingleton<CursorPositionProvider>();
        services.AddSingleton<Win32HotkeyMonitor>();
        services.AddSingleton<Win32MouseHookMonitor>();

        // Движок макросов. Хранилище — библиотека-первоисточник: оно снабжает HotkeyListener
        // привязками, отдаёт оркестратору запись целиком (граф, шаблоны, под-макросы) и
        // подсказывает, какие графы должен «загрузить» новый процесс. Создание объекта при этом
        // не пишет на диск ни одного файла — инвариант вместе с причинами записан на самом
        // MacroGraphStore. Регистрации IMacroGraphResolver здесь больше нет: с волны F4
        // межмакросных вызовов не осталось, и разрешать по имени нечего.
        services.AddSingleton<MacroGraphStore>();
        // Волна F2: шаблоны зрения переехали внутрь бандла макроса, поэтому разрешение имён стало
        // порунным. Кэш живёт между прогонами (иначе каждый прогон открывал бы zip), ключуется
        // именем макроса и сбрасывается по MacrosChanged — подписку на неё он берёт у хранилища,
        // отсюда и порядок регистрации.
        services.AddSingleton<MacroTemplateCache>();
        services.AddSingleton<IMacroPrimitives, MacroPrimitives>();
        services.AddSingleton<MacroExecutor>();
        services.AddSingleton<MacroRunRegistry>();

        // Волна D3b: канал прогресса исполнителя. Регистрируется до Orchestrator, потому что
        // тот берёт его необязательной зависимостью и кладёт в каждый контекст прогона. Он
        // инертен, пока панель не подпишется, — почему это важно демону, который бо́льшую часть
        // жизни проводит без подключённого интерфейса, см. в комментарии к классу.
        services.AddSingleton<RunEventPublisher>();
        services.AddSingleton<IMacroRunObserver>(sp => sp.GetRequiredService<RunEventPublisher>());

        // Вторая подписка на том же русле: журнал самого демона, выведенный в трубу. Сток,
        // который его сюда отводит, вставлен в конвейер Serilog выше по файлу; здесь только
        // объект, у которого кольцо, очередь и склейка.
        //
        // Регистрируется ДО IpcServer, потому что тот берёт его конструктором (соединение
        // щёлкает своей подпиской) и вручает ему себя как рассылку.
        services.AddSingleton<LogEventPublisher>();

        // Волна D5: управляющий канал отладчика — брат-близнец отчётного канала публикатора.
        // Тоже инертен, пока не подключится панель, — а подключение здесь ТО ЖЕ САМОЕ событие,
        // что и подписка на события прогона (см. IpcServer.ClientConnection), и именно это не
        // даёт приостановленному обходу пережить единственный процесс, способный его отпустить.
        services.AddSingleton<MacroDebugSession>();
        services.AddSingleton<IMacroDebugger>(sp => sp.GetRequiredService<MacroDebugSession>());

        // Автозапуск: единственная часть настроек, которая применяется НЕ в процессе. Первой
        // среди размещённых служб — она ничего не ждёт, зато чинит установку, у которой файл
        // настроек говорит «запускать при входе», а записи в системе нет (перенесли папку,
        // прибрался чистильщик, файл принесли с другой машины). Демон обязан уметь это без
        // панели: панель по требованию, её может не быть сутками.
        services.AddHostedService<StartupReconciler>();

        // ProcessMonitor и HotkeyListener зарегистрированы первыми, потому что Orchestrator
        // подписывается на их события прямо в конструкторе. DI разрешит их раньше
        // Orchestrator при любом порядке регистрации, но перечислить их первыми просто
        // естественнее читается.
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());

        // Привязки горячих клавиш берутся из самой библиотеки макросов (HotkeyTrigger'ы каждого
        // графа), поэтому слушатель перерегистрируется при любом изменении библиотеки. Никакого
        // hotkeys.json.
        services.AddSingleton<HotkeyListener>();
        services.AddHostedService(sp => sp.GetRequiredService<HotkeyListener>());

        // Одна служба на все окна вместо агента на клиента (W0.4): по общему таймеру обходит
        // реестр и снимает с регистрации окна, которых больше нет.
        //
        // Зарегистрирована ДО оркестратора намеренно: hosted-сервисы останавливаются в обратном
        // порядке, поэтому она свернётся ПОСЛЕ него — то есть окна уходят из реестра уже после
        // того, как Orchestrator.StopAsync отменил прогоны на лету. Наоборот было бы плохо:
        // прогон, брошенный посреди активации, оставил бы клиент разбуженным.
        services.AddHostedService<WindowLifetimeMonitor>();

        // Единственный экземпляр Orchestrator; ролью IHostedService он остаётся ради StopAsync,
        // который гасит прогоны на выключении.
        services.AddSingleton<Orchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<Orchestrator>());

        // --- IPC (стадия 2B) ----------------------------------------------------------
        //
        // Два узких стыка, которые нужны диспетчеру. Оба разрешаются в синглтоны выше:
        // интерфейсы существуют затем, чтобы обработчики запросов можно было покрыть
        // модульными тестами без живого движка, а не потому, что есть вторая реализация.
        services.AddSingleton<IMacroRunner>(sp => sp.GetRequiredService<Orchestrator>());
        services.AddSingleton<IHotkeyRegistration>(sp => sp.GetRequiredService<HotkeyListener>());
        services.AddSingleton<CaptureDumpService>();
        services.AddSingleton<IpcRequestDispatcher>();
        services.AddSingleton<IpcServer>();
        // Рассылка событий как отдельная возможность — ради трейного сценария «панель уже
        // открыта, выведи её вперёд». (Диспетчеру тот же объект вручает конструктор сервера,
        // потому что сервер → диспетчер → сервер было бы циклом в DI.)
        services.AddSingleton<IIpcBroadcaster>(sp => sp.GetRequiredService<IpcServer>());

        // Зарегистрировано ПОСЛЕ движка и ДО трея, и это закрепляет оба конца его жизни:
        // hosted-сервисы стартуют в порядке регистрации, поэтому канал появляется только
        // когда ProcessMonitor/HotkeyListener/Orchestrator уже подняты, и клиент, подключившийся
        // в ту же секунду, как увидел канал, получает живой реестр; останавливаются же они в
        // ОБРАТНОМ порядке, поэтому канал сворачивается рано — сразу за иконкой в трее, до того
        // как начнут разбираться прогоны, окна и горячие клавиши, — и панель узнаёт, что демон
        // уходит, а не висит на полумёртвом движке.
        // Насос пакетирования запускается до канала, чтобы у клиента, подписавшегося сразу
        // после подключения, было кому разбирать его очередь. У ленты журнала насос свой, и
        // поднимается он там же и по той же причине. Кольцо предыстории при этом наполняется с
        // того момента, как Serilog собрал конвейер, — то есть задолго до обоих насосов.
        services.AddHostedService(sp => sp.GetRequiredService<RunEventPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<LogEventPublisher>());

        services.AddHostedService(sp => sp.GetRequiredService<IpcServer>());

        // Трей последним: hosted-сервисы останавливаются в обратном порядке регистрации,
        // поэтому иконка исчезает первой, когда пользователь выбирает «Выход», — никакой
        // мёртвой иконки, висящей в трее, пока доигрывают прогоны и горячие клавиши.
        services.AddSingleton<Win32TrayIcon>();
        services.AddHostedService<TrayController>();
    }
}
