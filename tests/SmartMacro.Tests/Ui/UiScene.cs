using Avalonia.Media;
using Avalonia.Threading;
using SmartMacro.App;
using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Contracts.Settings;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;
using SmartMacro.Tests.ViewModels;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Настоящее окно панели с настоящими view-model'ями над <see cref="FakeIpcClient"/> и настоящей
/// временной папкой <c>macros/</c>.
///
/// <b>Почему не заглушечный DataContext.</b> Пустое состояние доказывает ровно пустое состояние.
/// Каждый второй дефект раскладки в этом проекте появлялся ИМЕННО при наполнении: кнопка
/// извлечения отъедала ширину у строки состояния только когда что-то отмечено, а подпись типа
/// въезжала в подсказку инспектора только когда выбрана нода определённого вида. Обход по пустой
/// панели прошёл бы мимо всех трёх.
///
/// <b>Что здесь наполнено и зачем.</b> Окна с тегами (сводка тегов в рейке, бейджи целей),
/// идущие прогоны (полоса прогона, счётчик рейки, панель отладчика), лента лога с
/// предупреждениями (счётчик проблем), отказ регистрации хоткея (⚠ на строке библиотеки) и
/// макрос со всеми видами нод, триггерами, под-макросом и отметками для извлечения.
///
/// ⚠️ Создавать и трогать ТОЛЬКО внутри <see cref="Ui.RunAsync(Action)"/>: здесь настоящее
/// <see cref="Window"/>, а у него есть поток.
/// </summary>
internal sealed class UiScene : IDisposable
{
    private const long HwndArcher = 0x140804;
    private const long HwndPriest = 0x140808;
    private const long HwndUnknown = 0x1408A0;

    private static readonly Guid Submacro = Ids.Of("под-макрос-1");

    private static readonly Dictionary<(double Width, double Height), UiScene> SharedScenes = [];

    private UiScene(TempLibrary library, FakeIpcClient client, ShellViewModel shell, MainWindow window)
    {
        Library = library;
        Client = client;
        Shell = shell;
        Window = window;
    }

    public TempLibrary Library { get; }

    public FakeIpcClient Client { get; }

    public ShellViewModel Shell { get; }

    public MainWindow Window { get; }

    public MacroEditorViewModel Editor => Shell.Editor;

    /// <summary>
    /// Общая сцена этого размера — для правил, которые только СМОТРЯТ.
    ///
    /// Поднять панель стоит примерно полсекунды (разбор пяти видов вместе с MacrosView в 2200
    /// строк), и платить её десять раз за десять обходов, которые ничего не меняют, незачем.
    /// Тесты идут через <see cref="Ui.RunAsync(Action)"/>, то есть по одному на потоке
    /// диспетчера, так что делить объект между ними безопасно.
    ///
    /// ⚠️ Тесту, который что-то ПРАВИТ (как замер чернил, вписывающий слова в поля), положен
    /// свой экземпляр через <see cref="Create"/> — тот же довод, что у общей библиотеки в
    /// <c>TempLibrary.Shared</c>.
    /// </summary>
    public static UiScene Shared(double width, double height)
    {
        var key = (width, height);
        if (!SharedScenes.TryGetValue(key, out var scene))
        {
            scene = Create(width, height);
            SharedScenes[key] = scene;
        }

        return scene;
    }

    /// <summary>
    /// Поднимает панель и показывает её. Окно уже разложено к возврату: <c>Window.Show</c>
    /// прогоняет очередь диспетчера досуха, так что ждать здесь нечего и нечем.
    /// </summary>
    /// <param name="width">Ширина окна в пикселях — обходы гоняют сцену на нескольких размерах.</param>
    /// <param name="height">Высота окна в пикселях.</param>
    /// <param name="openMacro">
    /// Открыть макрос сразу. <c>false</c> оставляет редактор ПУСТЫМ — то состояние, из которого
    /// «создать макрос» впервые переводит <c>HasOpenMacro</c> в <c>true</c>. Дефект F4 («раздел
    /// «Триггеры» остался мёртвым») жил ровно на этом переходе и на панели с уже открытым
    /// макросом не воспроизводится вовсе.
    /// </param>
    public static UiScene Create(double width = 1520, double height = 840, bool openMacro = true)
    {
        var library = new TempLibrary();
        library.WriteExternally(TopLevelGraph(), [new MacroSubmacro(Submacro, SubmacroGraph())]);
        library.WriteExternally(BareGraph("pw-immunity"));

        // ⚠️ ХУДШИЙ СЛУЧАЙ СТРОКИ БИБЛИОТЕКИ, и он здесь не для полноты. Обход раскладки один раз
        // уже промолчал ровно на нём: правило «имени должно хватить хотя бы на одно многоточие»
        // работало, но в сцене не было ни длинного имени, ни длинного аккорда — а на живой панели
        // «Ctrl+Shift+Alt+F12» съедал строку целиком, и имя пропадало НАСОВСЕМ. Обход хорош ровно
        // настолько, насколько наполнена его сцена; дефекты ширины ловятся не правилом, а
        // правилом ПЛЮС содержимым, на котором ширины не хватает.
        library.WriteExternally(new MacroGraph
        {
            Name = "pw-boot-and-identify",
            Triggers =
            [
                new HotkeyTrigger(
                    HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt,
                    VirtualKey.F12),
            ],
            StartNodeId = Ids.Of("длинный-1"),
            Nodes = [new DelayNode { Id = Ids.Of("длинный-1"), DisplayName = "delay-1", Ms = 200 }],
        });

        // Строка с красным «!»: граф читается, но валидатор нашёл ошибку (ребро в удалённую ноду),
        // а хоткея нет — то есть отметка приходит от IsUnhealthy, а не от нечитаемого бандла. В
        // сцене она по той же причине, что и длинный аккорд выше: отметка занимает колонку слева
        // от имени, и без строки, на которой она зажигается, ни одно правило ширины её не мерит.
        library.WriteExternally(new MacroGraph
        {
            Name = "чужой-макрос-с-ошибкой",
            StartNodeId = Ids.Of("битый-1"),
            Nodes =
            [
                new DelayNode
                {
                    Id = Ids.Of("битый-1"), DisplayName = "пауза-1", Ms = 150, Next = Ids.Of("удалённая"),
                },
            ],
        });

        var client = SeededClient();
        var shell = new ShellViewModel(
            new WorkspaceViewModel(client, ImmediateUiDispatcher.Instance),
            new MacroEditorViewModel(client, library.Library, null, null, ImmediateUiDispatcher.Instance),
            new LogViewModel(client, ImmediateUiDispatcher.Instance),
            new SettingsViewModel(client, ImmediateUiDispatcher.Instance));

        var window = new MainWindow(shell) { Width = width, Height = height };

        // Сглаживание текста — СЕРОЕ, а не субпиксельное. Skia по умолчанию раскладывает края
        // глифа по каналам R, G и B, и в растре появляются синие и оранжевые каёмки — цвета,
        // которых в кисти нет вовсе (замерено: #1E4285 на глифе с Foreground #8B8FA2). Правилу о
        // цвете глифа они мешают, а ловушке, ради которой оно написано, — нет: цветной эмодзи
        // рисуется битмапом при любом сглаживании. Замер строк чернил это меняет на пиксель
        // по краю, что для «есть хвост или нет» несущественно.
        RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);

        var scene = new UiScene(library, client, shell, window);

        window.Show();
        client.RaiseConnected();
        PushLiveState(client);

        if (openMacro)
        {
            scene.OpenTheMacro();
        }

        Settle();

        return scene;
    }

    /// <summary>Переключает режим и доводит раскладку до конца.</summary>
    public void Select(ShellMode mode)
    {
        Shell.SelectMode(mode);
        Settle();
    }

    /// <summary>Меняет размер окна и доводит раскладку до конца.</summary>
    public void Resize(double width, double height)
    {
        Window.Width = width;
        Window.Height = height;
        Settle();
    }

    /// <summary>
    /// Прогоняет очередь диспетчера и раскладку до состояния покоя.
    ///
    /// <b>Детерминированная точка, а не ожидание.</b> <c>Dispatcher.RunJobs()</c>
    /// выполняет ВСЮ отложенную работу этого потока (привязки, раскладку, невидимую отрисовку) и
    /// возвращается, когда очередь пуста, — то есть это «дождаться», выраженное без времени.
    /// Второй прогон нужен потому, что раскладка законно ставит в очередь новую работу: смена
    /// <c>IsVisible</c> у вида запускает первый проход измерения, а привязки внутри него —
    /// следующий.
    /// </summary>
    public static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose()
    {
        if (SharedScenes.ContainsValue(this))
        {
            // Общую сцену закрывает конец прогона, а не тот, кто ею воспользовался.
            return;
        }

        Close();
    }

    /// <summary>Закрывает все общие сцены — вызывается один раз, в конце прогона.</summary>
    public static void DisposeShared()
    {
        foreach (var scene in SharedScenes.Values)
        {
            scene.Close();
        }

        SharedScenes.Clear();
    }

    private void Close()
    {
        Window.Close();
        Settle();
        Library.Dispose();
    }

    // ---- наполнение -------------------------------------------------------------------------

    /// <summary>Открывает главный макрос, выбирает ноду вызова и отмечает две ноды для извлечения.</summary>
    private void OpenTheMacro()
    {
        Shell.SelectMode(ShellMode.Macros);
        Editor.SelectedMacro = Editor.Macros.First(row => row.Name == "pw-boot");

        // Выбранная нода кормит инспектор. Именно «Под-макрос» — та самая, чья подпись типа
        // въезжала в подсказку «двойной клик — правка на месте».
        Editor.SelectedNode = Editor.Nodes.First(node => node.DisplayName == "вызов-7");

        // Развёрнутая коробка ноды распознавания: только в ней (и в инспекторе, где сейчас другая
        // нода) появляется кнопка «Выделить на снимке…», а коробка узкая — 210 px, и подпись в
        // ней с отступом в 64 px под самой длинной кнопкой блока параметров. Правило «когда
        // добавляешь правило, добавляй содержимое, которое его нарушает» работает и в обратную
        // сторону: новая подпись, которой в сцене нет, не проверяется ничем.
        Editor.ExpandNode(Editor.Nodes.First(node => node.DisplayName == "найти-4"));

        // Две отметки: кнопка «Выделить в под-макрос (2)» появляется только так, а появившись,
        // отъедает ширину у строки состояния справа от себя.
        Editor.ToggleMark(Editor.Nodes.First(node => node.DisplayName == "пауза-3"));
        Editor.ToggleMark(Editor.Nodes.First(node => node.DisplayName == "клик-2"));
        Editor.StatusMessage = "Сохранено: pw-boot · 8 нод · 2 шаблона";
    }

    private static FakeIpcClient SeededClient()
    {
        var client = new FakeIpcClient();
        client
            .Respond(IpcMessageTypes.GetWindows, new[]
            {
                new WindowDto(HwndArcher, "elementclient_64", ["Лучник", "мастер"]),
                new WindowDto(HwndPriest, "elementclient_64", ["Жрец"]),
                new WindowDto(HwndUnknown, "elementclient_64", []),
            })
            .Respond(IpcMessageTypes.GetRunningMacros, new[]
            {
                new RunningMacroDto(Ids.Of("прогон-1"), "pw-boot", DateTimeOffset.UtcNow.AddSeconds(-73), "найти-4"),
                new RunningMacroDto(Ids.Of("прогон-2"), "pw-immunity", DateTimeOffset.UtcNow.AddSeconds(-4), "пауза-1"),
            })
            .Respond(IpcMessageTypes.GetHotkeyFailures, new[]
            {
                new HotkeyFailureDto("pw-immunity", HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F1),
            })
            .Respond(IpcMessageTypes.GetBreakpoints, Array.Empty<BreakpointSetDto>())
            .Respond(IpcMessageTypes.SubscribeRunEvents, Array.Empty<RunWalkDto>())
            .Respond(IpcMessageTypes.SubscribeLog, new[]
            {
                Log(1, LogLevelDto.Information, "Демон запущен, корень установки D:\\SmartMacro"),
                Log(2, LogLevelDto.Warning, "Окно 0x1408A0 не опознано ни одним тегом"),
                Log(3, LogLevelDto.Error, "RegisterHotKey отказал: Ctrl+Shift+F1 занят другим приложением"),
                Log(4, LogLevelDto.Debug, "Опрос процессов: найдено 3 окна elementclient_64"),
            })
            // ⚠️ FileFault непустой НАМЕРЕННО: полоса «файл не прочитан» видна только в этом
            // состоянии, а замер по пустому экрану доказывает лишь пустой экран. Заодно это
            // самая длинная строка на экране настроек — её и надо мерить на перенос и на
            // столкновения.
            .Respond(IpcMessageTypes.GetSettings, new SettingsSnapshotDto(
                AppSettings.Default,
                LogLevelDto.Information,
                @"D:\SmartMacro\settings.json",
                @"D:\SmartMacro",
                "Файл настроек не прочитан: ')' is invalid after a value. Expected either ',', '}'."))
            .Respond(IpcMessageTypes.SaveSettings, Array.Empty<SettingsIssue>());

        return client;
    }

    /// <summary>Пуши, которые демон шлёт сам: без них рейка и полоса прогона остались бы пустыми.</summary>
    private static void PushLiveState(FakeIpcClient client)
    {
        client.RaiseEvent(IpcMessageTypes.WindowTagsChanged,
            new WindowDto(HwndArcher, "elementclient_64", ["Лучник", "мастер"]));
        client.RaiseEvent(IpcMessageTypes.LogEntries, new LogEntryBatch(
            [Log(5, LogLevelDto.Warning, "Шаблон classes/Лучник.png не найден в бандле «pw-boot»")], 0));
    }

    private static LogEntryDto Log(long seq, LogLevelDto level, string message) =>
        new(seq, DateTimeOffset.Now, level, "SmartMacro.Orchestration.Orchestrator", message, null);

    // ---- содержимое библиотеки ---------------------------------------------------------------

    /// <summary>
    /// Макрос со всеми семействами нод сразу. Виды нод рисуют разные коробки и разные инспекторы,
    /// а обход меряет то, что на экране, — поэтому граф собран не «типичным», а ПОЛНЫМ.
    /// </summary>
    private static MacroGraph TopLevelGraph() => new()
    {
        Name = "pw-boot",
        Triggers =
        [
            new HotkeyTrigger(HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey.F9),
            new ProcessAppearedTrigger("elementclient_64"),
        ],
        StartNodeId = Ids.Of("клавиша-1"),
        Nodes =
        [
            new KeyPressNode
            {
                Id = Ids.Of("клавиша-1"), DisplayName = "клавиша-1", Key = VirtualKey.C,
                Target = new TargetSelector { RequireTags = ["Лучник"], ExcludeTags = ["мастер"] },
                Next = Ids.Of("клик-2"),
            },
            new ClickNode
            {
                Id = Ids.Of("клик-2"), DisplayName = "клик-2", Point = new ScreenPoint(1920, 1080),
                Next = Ids.Of("пауза-3"),
            },
            new DelayNode { Id = Ids.Of("пауза-3"), DisplayName = "пауза-3", Ms = 1500, Next = Ids.Of("найти-4") },
            new FindElementNode
            {
                Id = Ids.Of("найти-4"), DisplayName = "найти-4", Template = "Find.png",
                Region = new ScreenRect(0, 0, 3840, 2160), FoundPointVar = "точка",
                Found = Ids.Of("тег-5"), NotFound = Ids.Of("ждать-6"),
            },
            new AddTagNode { Id = Ids.Of("тег-5"), DisplayName = "тег-5", Tag = "в мире", Next = Ids.Of("вызов-7") },
            new WaitForElementNode
            {
                Id = Ids.Of("ждать-6"), DisplayName = "ждать-6", Template = "Loading.png", TimeoutMs = 30000,
                Found = Ids.Of("вызов-7"),
            },
            new RunSubmacroNode
            {
                Id = Ids.Of("вызов-7"), DisplayName = "вызов-7", SubmacroId = Submacro,
                Target = new TargetSelector { RequireTags = ["Лучник", "Жрец"] },
                Next = Ids.Of("иконка-8"),
            },
            new SetIconNode
            {
                Id = Ids.Of("иконка-8"), DisplayName = "иконка-8",
                IconPath = "Assets/ClassIcons/Лучник.png",
            },
        ],
    };

    /// <summary>Функция внутри того же бандла — то, что рисует вложенную строку в дереве библиотеки.</summary>
    private static MacroGraph SubmacroGraph() => new()
    {
        Name = "опознать класс",
        StartNodeId = Ids.Of("распознать-1"),
        Nodes =
        [
            new MatchTemplateSetNode
            {
                Id = Ids.Of("распознать-1"), DisplayName = "распознать-1", TemplateSet = "classes",
                Region = new ScreenRect(1200, 400, 900, 600), ResultVar = "класс",
            },
        ],
    };

    /// <summary>Второй макрос библиотеки: нужен, чтобы дерево было деревом, а не одной строкой.</summary>
    private static MacroGraph BareGraph(string name) => new()
    {
        Name = name,
        Triggers = [new HotkeyTrigger(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F1)],
        StartNodeId = Ids.Of($"{name}-1"),
        Nodes = [new DelayNode { Id = Ids.Of($"{name}-1"), DisplayName = "пауза-1", Ms = 250 }],
    };
}
