using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Vision;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// Набор макросов-примеров, который при первом запуске записывается в пустую папку
/// <c>macros/</c>.
///
/// Это НЕ встроенное поведение — это обычные, полностью редактируемые файлы макросов, которые
/// просто воспроизводят то, что SmartMacro держал зашитым в коде до появления графа нод (вход в
/// мир Perfect World, имун на пати, помощь, рассылка клика по курсору, опознание). Каждая
/// координата, область, клавиша и тег ниже — это то самое значение, которое раньше было зашито
/// в конфиг, так что игрок PW получает прежнее поведение из коробки и теперь может подкрутить
/// его в макросе, а не в <c>appsettings.json</c>, — ради этого переезд и затевался.
///
/// Кто в PW не играет, просто удаляет эти файлы.
/// </summary>
public static class DefaultMacroGraphs
{
    /// <summary>Процесс, которого ждёт пример загрузки. Совпадает с поставляемой записью в ProcessProfiles.</summary>
    private const string GameProcessName = "elementclient_64";

    /// <summary>Тег того персонажа, которому помогают все остальные, — сам себе он не помогает.</summary>
    private const string MasterTag = "Лучник";

    /// <summary>Вспомогательный персонаж (мул под склад): в мире он есть, но общепатийные команды его не касаются.</summary>
    private const string IgnoredTag = "Шаман";

    /// <summary>Обрезка панели характеристик, в которой лежит текст значения «Класс: &lt;имя&gt;».</summary>
    private static readonly ScreenRect ClassNameRegion = new(3200, 1060, 160, 35);

    /// <summary>Иконки панели чата: по их наличию мы и понимаем, что клиент добрался до мира.</summary>
    private static readonly ScreenRect ChatPanelRegion = new(0, 2100, 160, 60);

    /// <summary>Щедрый бюджет на фазу — загрузка PW после лаунчера занимает секунд тридцать.</summary>
    private const int PhaseTimeoutMs = 60_000;

    /// <summary>Игровая клавиша, переключающая панель характеристик.</summary>
    private const VirtualKey StatsHotkey = VirtualKey.C;

    /// <summary>Пауза по реальному времени, чтобы PW действительно отрисовал панель характеристик до захвата кадра.</summary>
    private const int StatsOpenDelayMs = 500;

    /// <summary>Все графы-примеры, порядок значения не имеет.</summary>
    public static IReadOnlyList<MacroGraph> Build() =>
    [
        Boot(),
        Immunity(),
        Assist(),
        CursorClick(),
        Identify(),
        IdentifyOne(),
    ];

    /// <summary>
    /// Проводит только что запущенный клиент от выбора сервера до входа в мир, а потом опознаёт
    /// персонажа. Витринный граф: триггер по процессу, ветка на каждый исход, переменная,
    /// которую пишет условная нода и читает обратно интерполяция <c>{tag}</c>. Любая ветка по
    /// таймауту просто заканчивает прогон — оператор перезапускает вручную.
    /// </summary>
    private static MacroGraph Boot() => new()
    {
        Name = "pw-boot",
        Triggers = [new ProcessAppearedTrigger(GameProcessName)],
        StartNodeId = "wait-server-select",
        Nodes =
        [
            // Область оставлена пустой = искать по всей клиентской области: положение кнопки
            // едет вместе с разрешением, так что поиск по всему кадру — переносимое значение
            // по умолчанию.
            new WaitForElementNode
            {
                Id = "wait-server-select",
                Template = "ServerSelectButton",
                TimeoutMs = PhaseTimeoutMs,
                Found = "click-server-select",
                Timeout = null,
            },
            new ClickNode
                { Id = "click-server-select", Point = new ScreenPoint(1192, 1805), Next = "wait-character-select" },
            new WaitForElementNode
            {
                Id = "wait-character-select",
                Template = "CharacterSelectButton",
                TimeoutMs = PhaseTimeoutMs,
                Found = "click-character-select",
                Timeout = null,
            },
            new ClickNode
                { Id = "click-character-select", Point = new ScreenPoint(1958, 2053), Next = "wait-in-world" },
            new WaitForElementNode
            {
                Id = "wait-in-world",
                Template = "ChatPanelButtons",
                Region = ChatPanelRegion,
                TimeoutMs = PhaseTimeoutMs,
                Found = "open-stats",
                Timeout = null,
            },
            // Хвост с опознанием выписан целиком, а не делегирован в pw-identify-one, чтобы
            // этот файл оставался читаемым и самодостаточным как пример.
            new KeyPressNode { Id = "open-stats", Key = StatsHotkey, Next = "await-stats" },
            new DelayNode { Id = "await-stats", Ms = StatsOpenDelayMs, Next = "recognize-class" },
            new RecognizeTagNode
            {
                Id = "recognize-class",
                TemplateSet = TemplateSetProvider.ClassesSetName,
                Region = ClassNameRegion,
                ApplyTag = true,
                ResultVar = "tag",
                Matched = "set-icon",
                // Даже когда не совпало, панель всё равно закрываем: оставить её открытой —
                // сломать последующие макросы.
                NotMatched = "close-stats",
            },
            new SetIconNode { Id = "set-icon", IconPath = "Assets/ClassIcons/{tag}.png", Next = "close-stats" },
            new KeyPressNode { Id = "close-stats", Key = StatsHotkey, Next = null },
        ],
    };

    /// <summary>Кнопка паники: разом жмёт общую клавишу имуна к урону на всю пати.</summary>
    private static MacroGraph Immunity() => new()
    {
        Name = "pw-immunity",
        Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)],
        StartNodeId = "immunity",
        Nodes =
        [
            new KeyPressNode
            {
                Id = "immunity",
                Key = VirtualKey.F8,
                Target = new TargetSelector { ExcludeTags = [IgnoredTag] },
            },
        ],
    };

    /// <summary>
    /// Каждый ведомый выбирает портрет ведущего в пати, а потом жмёт свой игровой макрос
    /// <c>/assist</c> — и вся пати оказывается на цели ведущего. Сам ведущий из выборки
    /// исключён (себе никто не помогает) вместе со вспомогательным персонажем.
    /// </summary>
    private static MacroGraph Assist()
    {
        var followers = new TargetSelector { ExcludeTags = [MasterTag, IgnoredTag] };
        return new MacroGraph
        {
            Name = "pw-assist",
            Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F19)],
            StartNodeId = "select-master",
            Nodes =
            [
                new ClickNode
                    { Id = "select-master", Point = new ScreenPoint(285, 456), Target = followers, Next = "settle" },
                // PW нужно реальное время, чтобы применить выбор до того, как отработает
                // /assist, иначе макрос поможет по предыдущей цели.
                new DelayNode { Id = "settle", Ms = 200, Next = "assist-key" },
                new KeyPressNode { Id = "assist-key", Key = VirtualKey.F2, Target = followers },
            ],
        };
    }

    /// <summary>
    /// Повторяет позицию курсора кликом сразу во всех клиентах. Читает переменную
    /// <c>cursor</c>, которую слой триггеров засевает в каждый прогон.
    /// </summary>
    private static MacroGraph CursorClick() => new()
    {
        Name = "pw-cursor-click",
        Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F22)],
        StartNodeId = "click",
        Nodes =
        [
            new ClickNode
            {
                Id = "click",
                PointVar = MacroVariables.CursorVariableName,
                Target = new TargetSelector { ExcludeTags = [IgnoredTag] },
            },
        ],
    };

    /// <summary>
    /// Опознание веером: по одному под-прогону <c>pw-identify-one</c> на окно, и у каждого
    /// контекстом служит его окно. Именно так хоткей (у которого собственного контекст-окна
    /// нет) вообще добирается до условных нод.
    /// </summary>
    private static MacroGraph Identify() => new()
    {
        Name = "pw-identify",
        Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F20)],
        StartNodeId = "identify-all",
        Nodes =
        [
            new RunMacroNode
            {
                Id = "identify-all",
                MacroName = "pw-identify-one",
                Target = new TargetSelector { ExcludeTags = [IgnoredTag] },
                Await = true,
            },
        ],
    };

    /// <summary>
    /// Опознаёт персонажа в ОДНОМ окне: открыть характеристики, распознать имя класса,
    /// поставить окну тег, применить иконку на панели задач, закрыть характеристики. Без
    /// триггера намеренно — это библиотечная подпрограмма, которую зовёт <c>pw-identify</c>
    /// (или UI по конкретному окну).
    /// </summary>
    private static MacroGraph IdentifyOne() => new()
    {
        Name = "pw-identify-one",
        StartNodeId = "open-stats",
        Nodes =
        [
            new KeyPressNode { Id = "open-stats", Key = StatsHotkey, Next = "await-stats" },
            new DelayNode { Id = "await-stats", Ms = StatsOpenDelayMs, Next = "recognize-class" },
            new RecognizeTagNode
            {
                Id = "recognize-class",
                TemplateSet = TemplateSetProvider.ClassesSetName,
                Region = ClassNameRegion,
                ApplyTag = true,
                ResultVar = "tag",
                Matched = "set-icon",
                NotMatched = "close-stats",
            },
            new SetIconNode { Id = "set-icon", IconPath = "Assets/ClassIcons/{tag}.png", Next = "close-stats" },
            new KeyPressNode { Id = "close-stats", Key = StatsHotkey, Next = null },
        ],
    };
}
