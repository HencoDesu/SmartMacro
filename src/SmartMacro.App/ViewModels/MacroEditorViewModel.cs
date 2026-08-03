using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels;

/// <summary>Один макрос в левом списке библиотеки редактора.</summary>
public sealed class MacroListItemViewModel : ObservableObject
{
    private bool _isRunning;
    private bool _isCurrent;
    private string? _hotkeyProblem;

    public MacroListItemViewModel(MacroGraph macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        Name = macro.Name;
        Summary = Describe(macro);
        TriggerBadge = Badge(macro);
    }

    /// <summary>Имя макроса = основа имени файла = его личность.</summary>
    public string Name { get; }

    /// <summary>Триггеры и число нод — текст подсказки.</summary>
    public string Summary { get; }

    /// <summary>
    /// Односложный чип рядом с именем: сочетание (<c>F23</c>), <c>процесс</c> либо
    /// <c>null</c>, когда триггера у макроса нет вовсе. Показывается только ПЕРВЫЙ триггер —
    /// строка высотой 28px, а макрос с тремя триггерами достаточно редок, чтобы оставить его
    /// инспектору.
    /// </summary>
    public string? TriggerBadge { get; }

    /// <summary><c>true</c>, когда есть бейдж, который надо нарисовать.</summary>
    public bool HasTriggerBadge => TriggerBadge is not null;

    /// <summary>Управляет состояниями кнопок «Запустить» и «Стоп».</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsNotRunning));
            }
        }
    }

    public bool IsNotRunning => !_isRunning;

    /// <summary>
    /// Это тот макрос, что открыт в редакторе. Библиотека — дерево групп, а не один плоский
    /// <c>ListBox</c>, поэтому выделение не может ехать на <c>:selected</c> у
    /// <c>ListBoxItem</c> и переносится сюда.
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetField(ref _isCurrent, value);
    }

    /// <summary>
    /// «Ctrl+F1 не зарегистрирован — занят другим приложением» либо <c>null</c>.
    ///
    /// Строка библиотеки — единственное место, где тихо умерший хоткей можно заметить, НЕ
    /// открывая макрос, а это важно, потому что случается такое обычно при старте демона, с
    /// макросом, который никто не правит.
    /// </summary>
    public string? HotkeyProblem
    {
        get => _hotkeyProblem;
        internal set
        {
            if (SetField(ref _hotkeyProblem, value))
            {
                OnPropertyChanged(nameof(HasHotkeyProblem));
            }
        }
    }

    /// <summary><c>true</c>, когда строке следует нести свою предупреждающую отметку.</summary>
    public bool HasHotkeyProblem => _hotkeyProblem is not null;

    private static string Describe(MacroGraph macro)
    {
        var triggers = macro.Triggers.Select(DescribeTrigger).ToList();
        var triggerText = triggers.Count > 0
            ? string.Join(", ", triggers)
            : "без триггеров";
        return string.Create(CultureInfo.CurrentCulture, $"{triggerText} · нод: {macro.Nodes.Count}");
    }

    private static string? Badge(MacroGraph macro) => macro.Triggers.Count switch
    {
        0 => null,
        _ => macro.Triggers[0] switch
        {
            HotkeyTrigger { IsMouse: true } hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.MouseButton.ToString()),
            HotkeyTrigger hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.Key.ToString()),
            ProcessAppearedTrigger => "процесс",
            var other => other.GetType().Name,
        },
    };

    private static string DescribeTrigger(MacroTrigger trigger) => trigger switch
    {
        HotkeyTrigger { IsMouse: true } hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.MouseButton.ToString()),
        HotkeyTrigger hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.Key.ToString()),
        ProcessAppearedTrigger process => $"процесс {process.ProcessName}",
        _ => trigger.GetType().Name,
    };

    private static string Chord(string modifiers, string key) =>
        string.Equals(modifiers, "None", StringComparison.Ordinal) ? key : $"{modifiers}+{key}";
}

/// <summary>Одна строка панели валидации.</summary>
public sealed class ValidationIssueViewModel
{
    public ValidationIssueViewModel(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        IsError = issue.Severity == ValidationSeverity.Error;
        NodeId = issue.NodeId;
        Display = issue.NodeId is null
            ? issue.Message
            : $"[{issue.NodeId}] {issue.Message}";
    }

    /// <summary>Произвольное сообщение (для ошибок ввода, которых валидатор не видит никогда).</summary>
    public ValidationIssueViewModel(string message, bool isError)
    {
        IsError = isError;
        Display = message;
    }

    /// <summary>Ошибки блокируют сохранение; предупреждения — нет.</summary>
    public bool IsError { get; }

    /// <summary>Нода, к которой относится замечание, если валидатор её назвал.</summary>
    public string? NodeId { get; }

    /// <summary>Нарисованный текст.</summary>
    public string Display { get; }
}

/// <summary>
/// Редактор макросов: библиотека слева, выбранный граф на canvas, лог прогона под ним и
/// инспектор справа.
///
/// <b>За ним стоит canvas (волна D3a), а не тот строчный редактор, что жил здесь раньше.</b>
/// Строчный редактор завернули на разборе дизайна и удалили, а не оставили рядом, — поэтому
/// исходящие рёбра ноды рисуются связями, а не выбираются из выпадающих списков id нод. Что
/// пережило подмену, так это форма самого класса: отображение «граф ↔ VM», протокол
/// сохранения и обращение с библиотекой ниже — те же самые, какими пользовался строчный
/// редактор, и потому canvas удалось построить, не трогая их.
///
/// <b>Стадия 3: библиотека удалённая.</b> Там, где эта VM держала <c>MacroGraphStore</c>, она
/// теперь держит снимок, полученный по IPC и обновляемый на <c>MacrosChanged</c> и на каждом
/// переподключении. Три следствия, которые стоит знать, прежде чем править этот класс:
///
///   * <b>Валидатор по документам — демон.</b> <c>SaveMacro</c> отвечает списком замечаний;
///     пустой означает, что граф записан. Предупреждения при УДАЧНОМ сохранении не
///     возвращаются (протокол даёт этому полю ровно один смысл — причины отказа), поэтому их
///     заново выводят на месте тем же <see cref="MacroGraphValidator"/>.
///   * <b>Эхо собственного сохранения может прийти раньше ответа на него.</b> Демон
///     рассылает <c>MacrosChanged</c> изнутри своего сохранения, другим путём записи, нежели
///     ответ, — поэтому опорные значения для вопроса «а не внешняя ли это правка?»
///     выставляются ДО того, как запрос уйдёт, и откатываются, если его отвергли.
///   * <b>Удачная запись сливается в локальную библиотеку сразу</b>, не дожидаясь пуша, —
///     так список и выделение устаканиваются синхронно.
///
/// Всё, что имеет форму Avalonia, держится снаружи намеренно, чтобы класс целиком можно было
/// гонять headless против поддельного <see cref="IIpcClient"/>, — а это важно, потому что
/// отображение «граф ↔ VM» и есть то место, где завелась бы тихая потеря данных.
/// </summary>
public sealed class MacroEditorViewModel : ObservableObject, IDisposable
{
    private const string DraftName = "новый-макрос";

    /// <summary>Папка, в которой демон держит файлы макросов, относительно его собственного каталога.</summary>
    private const string MacroFolderName = "macros";

    private readonly IIpcClient _client;
    private readonly IMacroLauncher? _launcher;
    private readonly IHotkeySuspension? _hotkeys;
    private readonly IUiDispatcher _dispatcher;

    private IReadOnlyList<MacroGraph> _library = [];
    private IReadOnlyList<RunningMacroDto> _runningMacros = [];
    private IReadOnlyList<HotkeyFailureDto> _hotkeyFailures = [];

    private MacroListItemViewModel? _selectedMacro;
    private NodeRowViewModel? _selectedNode;
    private bool _suppressSelectionReload;

    // Имя открытого сейчас макроса в том виде, в каком оно есть в библиотеке. null = черновик,
    // который ещё не сохраняли.
    private string? _loadedName;

    // Сериализованное состояние редактора на момент последней загрузки или сохранения — опора
    // для «есть ли несохранённые правки».
    private string _loadedJson = string.Empty;

    // Сериализованная форма того, что, по нашему мнению, лежит у демона, — опора для «изменили
    // ли снаружи». Хранится отдельно от _loadedJson, потому что загрузка нормализует
    // (вырожденная область, скажем, становится null), а нормализация не должна читаться как
    // «файл изменился у нас под руками».
    private string _diskJson = string.Empty;

    private string _macroName = string.Empty;
    private string _startNodeId = string.Empty;
    private bool _hasOpenMacro;
    private bool _changedOnDisk;
    private string? _errorMessage;
    private string? _statusMessage;

    private readonly MacroRunTracker _runs = new();

    private string _librarySearch = string.Empty;
    private double _zoom = 1;
    private double _panX = MinPan;
    private double _panY = MinPan;
    private string? _executingNodeId;
    private MacroRunViewModel? _selectedRun;
    private bool _wantsRunEvents;

    private int _droppedRunEvents;

    // Геометрия рёбер пересобирается по нодам; пока в полёте пачка структурных правок
    // (загрузка, удаление, перенацеливающее рёбра), пересборка откладывается до конца, чтобы
    // canvas не прокладывали по наполовину обновлённому графу.
    private int _edgeRebuildSuspended;

    /// <param name="client">Соединение с демоном — библиотека, прогоны и записи.</param>
    /// <param name="launcher">Шов для ручного «Запустить»; <c>null</c> гасит кнопку.</param>
    /// <param name="hotkeys">Приостановка и возобновление вокруг ловушки сочетаний; <c>null</c> ничего не делает.</param>
    /// <param name="dispatcher">Перекладывание пушей демона в поток UI.</param>
    /// <param name="macroFolderPath">
    /// Абсолютный путь, стоящий за кнопкой «открыть папку». Его подаёт хост, потому что только
    /// ОН знает, где живёт демон; по умолчанию это <c>macros/</c> рядом с этим исполняемым
    /// файлом, что верно для развёрнутой раскладки «бок о бок».
    /// </param>
    public MacroEditorViewModel(
        IIpcClient client,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null,
        IUiDispatcher? dispatcher = null,
        string? macroFolderPath = null)
    {
        _client = client;
        _launcher = launcher;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;
        FolderPath = macroFolderPath ?? Path.Combine(AppContext.BaseDirectory, MacroFolderName);

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;
        // Одна подписка на весь список триггеров, а не крючок в каждом из
        // AddTrigger / RemoveTrigger / LoadGraph / CloseEditor: это четыре места, каждое из
        // которых должно было бы помнить, а забытое оставляет ловушку, чьё состояние конфликта
        // не обновляется никогда.
        Triggers.CollectionChanged += OnTriggersCollectionChanged;

        if (_client.IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    // ---- библиотека (левая панель) ----------------------------------------------------

    /// <summary>Макросы библиотеки в том порядке, в каком их отдаёт демон (по имени).</summary>
    public ObservableCollection<MacroListItemViewModel> Macros { get; } = [];

    /// <summary>
    /// Выбранная запись библиотеки. Присвоение загружает этот граф в редактор; несохранённые
    /// правки открытого до того графа отбрасываются (с сообщением — модального подтверждения в
    /// этом заходе нет).
    /// </summary>
    public MacroListItemViewModel? SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (!SetField(ref _selectedMacro, value) || _suppressSelectionReload)
            {
                return;
            }

            if (value is null)
            {
                return;
            }

            if (TryGet(value.Name) is { } graph)
            {
                var discarded = _hasOpenMacro && IsDirty() ? _loadedName ?? _macroName : null;
                LoadGraph(graph);
                ErrorMessage = discarded is null
                    ? null
                    : $"Несохранённые изменения в «{discarded}» отброшены.";
            }
        }
    }

    /// <summary>
    /// Библиотека в том виде, в каком её рисует панель: разделы с заголовками <c>pw · 6</c> и
    /// <c>прочее · 11</c>. Выводится из <see cref="Macros"/> и <see cref="LibrarySearch"/>; само
    /// правило живёт в <see cref="MacroLibraryGrouping"/>.
    /// </summary>
    public ObservableCollection<MacroLibraryGroupViewModel> MacroGroups { get; } = [];

    /// <summary>Поле фильтра библиотеки. Подстрока в имени макроса, без учёта регистра.</summary>
    [AllowNull]
    public string LibrarySearch
    {
        get => _librarySearch;
        set
        {
            if (SetField(ref _librarySearch, value ?? string.Empty))
            {
                RebuildGroups();
            }
        }
    }

    /// <summary>Абсолютный путь к папке макросов демона — то, что стоит за кнопкой «открыть папку».</summary>
    public string FolderPath { get; }

    /// <summary>
    /// Живой снимок окон у редактора, стоящий за бейджем целей каждой ноды (D4). Засевается
    /// <see cref="RefreshAsync"/> и держится свежим оконными пушами демона — ровно так же, как
    /// библиотеку держит свежей <c>MacrosChanged</c>.
    ///
    /// Публичный, потому что им управляют тесты и потому что бейдж инспектора читает его
    /// количество; никто снаружи этого класса его не меняет.
    /// </summary>
    public WindowCatalog Windows { get; } = new();

    // ---- открытый граф (правая панель) ------------------------------------------------

    /// <summary><c>true</c>, когда в правой панели открыт граф — сохранённый или черновик.</summary>
    public bool HasOpenMacro
    {
        get => _hasOpenMacro;
        private set => SetField(ref _hasOpenMacro, value);
    }

    /// <summary>
    /// Правимое имя открытого графа. Сохранение под другим именем переименовывает макрос: демон
    /// ключуется по основе имени файла, поэтому переименование — это «записать новый файл,
    /// удалить старый», чем эта VM и занимается, ведь операции переименования в протоколе нет.
    /// </summary>
    [AllowNull]
    public string MacroName
    {
        get => _macroName;
        set => SetField(ref _macroName, value ?? string.Empty);
    }

    /// <summary>Строки триггеров открытого графа.</summary>
    public ObservableCollection<TriggerRowViewModel> Triggers { get; } = [];

    /// <summary>Строки нод открытого графа, в том порядке, в каком они сохранены в списке.</summary>
    public ObservableCollection<NodeRowViewModel> Nodes { get; } = [];

    /// <summary>
    /// Выбираемые цели рёбер: пустая строка (= конец прогона), а за ней все id нод. Один общий
    /// экземпляр, к которому привязан каждый выпадающий список ребра, — так переименование или
    /// новая нода появляются везде разом.
    /// </summary>
    public ObservableCollection<string> NodeIdChoices { get; } = [];

    /// <summary>Id нод для выбора стартовой. Тот же список без пустой записи — стартовая нода обязательна.</summary>
    public ObservableCollection<string> StartNodeChoices { get; } = [];

    /// <summary>Имена из библиотеки, которые предлагают выпадающие списки <c>RunMacroNode</c>.</summary>
    public ObservableCollection<string> MacroChoices { get; } = [];

    /// <summary>С чего начинается исполнение. Должна называть одну из <see cref="Nodes"/>.</summary>
    public string StartNodeId
    {
        get => _startNodeId;
        set
        {
            // ComboBox проталкивает null, пока перетряхивается его ItemsSource; игнорируя это,
            // мы не даём посторонней пересборке списка стереть вполне живую стартовую ноду.
            if (!string.IsNullOrEmpty(value))
            {
                SetField(ref _startNodeId, value);
            }
        }
    }

    /// <summary>Виды нод для всплывающего меню «добавить ноду».</summary>
    public IReadOnlyList<MacroNodeKindOption> NodeKinds => NodeRowViewModel.Kinds;

    /// <summary>Строка, подсвеченная кликом по замечанию валидатора.</summary>
    public NodeRowViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            var previous = _selectedNode;
            if (!SetField(ref _selectedNode, value))
            {
                return;
            }

            if (previous is not null)
            {
                previous.IsSelected = false;
            }

            if (value is not null)
            {
                value.IsSelected = true;
            }

            OnPropertyChanged(nameof(HasSelectedNode));
            OnPropertyChanged(nameof(InspectorTitle));
            // «До курсора» целится в то, что выделено, а значит, оживает и гаснет вместе с ним.
            OnPropertyChanged(nameof(CanRunToCursor));
        }
    }

    /// <summary>Управляет двумя состояниями инспектора: нода либо сам макрос.</summary>
    public bool HasSelectedNode => _selectedNode is not null;

    /// <summary>Заголовок инспектора — тип выделенной ноды либо «Макрос».</summary>
    public string InspectorTitle => _selectedNode?.TypeLabel ?? "Макрос";

    // ---- canvas ---------------------------------------------------------------------

    /// <summary>
    /// Все нарисованные рёбра открытого графа; пересобираются всякий раз, когда меняется форма
    /// графа или положение ноды. Исход без цели не даёт ничего — см.
    /// <see cref="CanvasEdgeRouter"/>.
    /// </summary>
    public ObservableCollection<CanvasEdgeViewModel> CanvasEdges { get; } = [];

    /// <summary>Наименьший масштаб, который допускает canvas.</summary>
    public const double MinZoom = 0.35;

    /// <summary>Наибольший масштаб, который допускает canvas.</summary>
    public const double MaxZoom = 2.0;

    // В покое поверхность приколота настолько внутрь области просмотра, чтобы левая верхняя
    // коробка не упиралась в кромку панели. Мало намеренно: три колонки переносимой раскладки —
    // это 780px, а панель canvas при размере окна по умолчанию около 810, так что щедрое поле
    // отделяет «граф помещается на 100%» от «третью колонку обрезает, пока не подвинешь
    // панораму».
    private const double MinPan = 12;

    /// <summary>Масштаб canvas. С ограничением: граф, ужатый в ничто, — потерянный граф.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetField(ref _zoom, Math.Clamp(value, MinZoom, MaxZoom)))
            {
                OnPropertyChanged(nameof(ZoomText));
            }
        }
    }

    /// <summary>Масштаб в том виде, в каком его рисует чип: «100%».</summary>
    public string ZoomText => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(_zoom * 100)}%");

    /// <summary>Горизонтальная панорама поверхности, в экранных пикселях.</summary>
    public double PanX
    {
        get => _panX;
        set => SetField(ref _panX, value);
    }

    /// <summary>Вертикальная панорама поверхности, в экранных пикселях.</summary>
    public double PanY
    {
        get => _panY;
        set => SetField(ref _panY, value);
    }

    /// <summary>Обратно на 100% и в начало координат.</summary>
    public void ResetView()
    {
        Zoom = 1;
        PanX = MinPan;
        PanY = MinPan;
    }

    /// <summary>
    /// Нода, на которой стоит исполнитель, либо <c>null</c>. Следует за
    /// <see cref="SelectedRun"/> — ради этого переключатель прогонов и заведён: по графу могут
    /// идти десять окон разом, а зажечь коробку позволено лишь одному из них.
    ///
    /// Присваивается извне, потому что тесты canvas управляют этим напрямую; в живой панели
    /// пишет сюда только <see cref="SyncExecutingNode"/>.
    /// </summary>
    public string? ExecutingNodeId
    {
        get => _executingNodeId;
        set
        {
            if (!SetField(ref _executingNodeId, value))
            {
                return;
            }

            foreach (var node in Nodes)
            {
                node.IsExecuting = value is not null
                                   && string.Equals(node.NodeId, value, StringComparison.Ordinal);
            }

            // Рёбра берут свою «живость» у ноды-источника, а слой перерисовывается по изменению
            // коллекции, а не по изменению свойства одного ребра, — иначе подсветка отставала бы
            // от коробки на кадр.
            RebuildEdges();
        }
    }

    // ---- лог прогона ------------------------------------------------------------------

    /// <summary>
    /// Полоса лога прогона под canvas: строки <see cref="SelectedRun"/>.
    ///
    /// Это проекция, а не хранилище — свои строки каждый обход держит в
    /// <see cref="MacroRunViewModel.Log"/>, поэтому переключение чипа (или уход в другой макрос
    /// и возврат) показывает историю того обхода, а не выброшенный лог.
    /// </summary>
    public ObservableCollection<RunLogRowViewModel> RunLog { get; } = [];

    /// <summary><c>true</c>, как только в полосе появляется что показывать.</summary>
    public bool HasRunLog => RunLog.Count > 0;

    /// <summary>
    /// Что говорит полоса, когда строк в ней нет. Различает «ничего не запускалось» и «ничего
    /// не пишется», потому что реакции пользователя на это разные.
    /// </summary>
    public string RunLogEmptyText => _wantsRunEvents
        ? "прогонов ещё не было"
        : "лог пишется, пока открыт режим «Макросы»";

    /// <summary>
    /// Обходы ОТКРЫТОГО макроса, старые первыми, — то, что перелистывает чип прогона. Пусто
    /// всякий раз, когда открытый граф ни разу не прогоняли при смотрящей панели.
    /// </summary>
    public ObservableCollection<MacroRunViewModel> Runs { get; } = [];

    /// <summary><c>true</c>, когда есть чип прогона, который вообще стоит рисовать.</summary>
    public bool HasRuns => Runs.Count > 0;

    /// <summary>
    /// Обход, за которым следуют canvas и полоса лога. Присвоение перенацеливает оба.
    /// </summary>
    public MacroRunViewModel? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (ReferenceEquals(_selectedRun, value))
            {
                return;
            }

            _selectedRun = value;
            OnPropertyChanged(nameof(SelectedRun));
            OnPropertyChanged(nameof(RunChipText));
            OnPropertyChanged(nameof(RunPositionText));
            OnPropertyChanged(nameof(SelectedRunIsLive));
            RebuildRunLog();
            SyncExecutingNode();
        }
    }

    /// <summary>Обход, над которым работает панель отладчика. По построению тот же, за которым следует canvas.</summary>
    public MacroRunViewModel? DebugTarget => _selectedRun;

    /// <summary>Подпись чипа: контекстное окно как <c>0x140804</c> либо имя макроса, когда окна нет.</summary>
    public string RunChipText => _selectedRun?.Label ?? string.Empty;

    /// <summary>«2 / 10», пока в полёте веер; пусто, когда обход всего один.</summary>
    public string RunPositionText
    {
        get
        {
            if (_selectedRun is null || Runs.Count < 2)
            {
                return string.Empty;
            }

            return string.Create(CultureInfo.InvariantCulture, $"{Runs.IndexOf(_selectedRun) + 1} / {Runs.Count}");
        }
    }

    /// <summary>Управляет живой точкой рядом с чипом.</summary>
    public bool SelectedRunIsLive => _selectedRun?.IsLive == true;

    /// <summary>
    /// Почему полоса, возможно, говорит не всю правду: панель подключилась посреди прогона либо
    /// демону пришлось выбросить события. <c>null</c>, когда лог полон.
    ///
    /// Это существует потому, что альтернатива — рисовать неполный лог ровно так же, как
    /// полный, — превращает «клика не было» и «вы не смотрели, когда он был» в одну и ту же
    /// картинку.
    /// </summary>
    public string? RunLogNotice
    {
        get
        {
            var parts = new List<string>(2);
            if (_selectedRun is { FromStart: false })
            {
                parts.Add("начало прогона не записано");
            }

            if (_droppedRunEvents > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"пропущено событий: {_droppedRunEvents}"));
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary><c>true</c>, когда <see cref="RunLogNotice"/> есть что сказать.</summary>
    public bool HasRunLogNotice => RunLogNotice is not null;

    // ---- отладчик (D5) ----------------------------------------------------------------

    /// <summary>
    /// Панель отладчика вообще существует. Только когда обход этого графа уже зафиксирован —
    /// иначе паузу ставить нечему, а четыре навсегда мёртвые кнопки — верный способ добиться,
    /// чтобы панель инструментов перестали читать.
    /// </summary>
    public bool HasDebugTarget => _selectedRun is not null;

    /// <summary>«Пауза» доступна: обход идёт и ещё не припаркован.</summary>
    public bool CanPause => _selectedRun is { IsLive: true, IsPaused: false, PauseRequested: false };

    /// <summary>«Дальше», «Шаг» и «До курсора» доступны: обход припаркован и его можно отпустить.</summary>
    public bool CanResume => _selectedRun is { IsLive: true, IsPaused: true };

    /// <summary>«До курсора» вдобавок нужна нода, в которую целиться, — выделенная на canvas.</summary>
    public bool CanRunToCursor => CanResume && _selectedNode is not null;

    /// <summary>«Стоп» доступен, пока в прогоне этого обхода хоть что-то ещё идёт.</summary>
    public bool CanStop => _selectedRun is { IsLive: true };

    /// <summary>Выбранный обход припаркован прямо сейчас.</summary>
    public bool SelectedRunIsPaused => _selectedRun?.IsPaused == true;

    /// <summary>Припаркован точкой останова — красный оттенок пилюли в полосе лога.</summary>
    public bool SelectedRunAtBreakpoint => _selectedRun?.PausedAtBreakpoint == true;

    /// <summary>
    /// Пилюле в полосе лога есть что сказать: обход припаркован ЛИБО паузу запросили, а она ещё
    /// не сработала.
    ///
    /// Вторая половина — не косметика. «Пауза» исполняется на ближайшей границе нод, поэтому
    /// нажатие во время шестидесятисекундного <c>WaitForElement</c> гасит кнопку, а дальше
    /// внешне не происходит ничего, — и это неотличимо от сломанной кнопки. Нашли это, посмотрев
    /// на живое приложение.
    /// </summary>
    public bool SelectedRunPauseVisible => _selectedRun is { IsPaused: true } or { PauseRequested: true };

    /// <summary>
    /// «брейкпоинт: recognize-class» для пилюли в полосе лога — «шаг: step-4» и «пауза: step-4»
    /// для остальных причин, а пока запрос ещё в полёте — «пауза запрошена — ждём конца ноды».
    ///
    /// Единственное место, называющее И состояние, И ноду, на которой обход припаркован. Панель
    /// инструментов этого намеренно не повторяет: две копии стоили 260px из 1520px полосы и
    /// вытолкнули «Сохранить» за правый край.
    /// </summary>
    public string PauseNotice => _selectedRun switch
    {
        { IsPaused: true, CurrentNodeId: { } node } run => $"{run.PauseReason}: {node}",
        { PauseRequested: true } => "пауза запрошена — ждём конца ноды",
        _ => string.Empty,
    };

    /// <summary>
    /// Чем занят обход, одним словом для панели инструментов.
    ///
    /// «пауза…» — то состояние, без которого не обойтись: «Пауза» — это просьба, исполняемая на
    /// ближайшей границе нод, а <c>WaitForElement</c> способен продержать её минуту. Кнопка,
    /// прыгающая из «выполняется» сразу в «на паузе», всю эту минуту врала бы.
    /// </summary>
    public string DebugStateText => _selectedRun switch
    {
        null => string.Empty,
        { IsFinished: true } run => run.FinalOutcome ?? "завершён",
        { IsPaused: true } run => $"на паузе · {run.PauseReason}",
        { PauseRequested: true } => "пауза…",
        _ => "выполняется",
    };

    /// <summary>«3 / 11» — во сколько нод этот обход вошёл, из скольких состоит граф.</summary>
    public string RunProgressText => _selectedRun is { } run && Nodes.Count > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{run.NodesEntered} / {Nodes.Count}")
        : string.Empty;

    /// <summary>
    /// «0:12.4» — сколько времени прошло с начала обхода. Пока обход жив, экстраполируется от
    /// последнего события, так что часы идут и сквозь шестидесятисекундное ожидание; когда обход
    /// заканчивается, они замирают.
    /// </summary>
    public string RunElapsedText
    {
        get
        {
            if (_selectedRun is not { } run)
            {
                return string.Empty;
            }

            var extra = run.IsLive ? (int)(DateTimeOffset.UtcNow - run.ElapsedAtUtc).TotalMilliseconds : 0;
            return RunLogRowViewModel.FormatElapsed(run.ElapsedMs + Math.Max(extra, 0));
        }
    }

    /// <summary>
    /// «■ Стоп» либо «■ Стоп ×3».
    ///
    /// <b>Стоп останавливает ПРОГОН, а не обход</b>, и счётчик — это то, чем кнопка в этом
    /// признаётся. Веер — это N обходов, делящих один id прогона и один токен отмены; никто,
    /// нажимая стоп, пока ведут десять клиентов, не имеет в виду «останови один из них», да и
    /// отмены на отдельный обход предложить всё равно нечего. Пауза и шаг остаются на обход —
    /// ради этого переключатель обходов и существует, — так что асимметрия настоящая и её надо
    /// показывать.
    /// </summary>
    public string StopLabel => LiveSiblingCount() is > 1 and var n
        ? string.Create(CultureInfo.InvariantCulture, $"■ Стоп ×{n}")
        : "■ Стоп";

    /// <summary>Проговаривает, что именно ■ отменит на самом деле.</summary>
    public string StopTooltip => LiveSiblingCount() is > 1 and var n
        ? string.Create(CultureInfo.InvariantCulture, $"Остановить весь прогон целиком — все {n} обхода")
        : "Остановить прогон";

    /// <summary>Просит демон припарковать выбранный обход на его следующей ноде.</summary>
    public Task PauseAsync() => DebugAsync(DebugCommand.Pause);

    /// <summary>Отпускает выбранный обход.</summary>
    public Task ResumeAsync() => DebugAsync(DebugCommand.Resume);

    /// <summary>Отпускает выбранный обход и тут же паркует его на самой следующей ноде.</summary>
    public Task StepAsync() => DebugAsync(DebugCommand.Step);

    /// <summary>Гонит выбранный обход до ноды, выделенной на canvas.</summary>
    public Task RunToCursorAsync() => _selectedNode is { } node
        ? DebugAsync(DebugCommand.RunToNode, node.NodeId)
        : Task.CompletedTask;

    /// <summary>
    /// Отменяет целиком тот прогон, которому принадлежит выбранный обход, — см.
    /// <see cref="StopLabel"/>.
    /// </summary>
    public async Task StopSelectedRunAsync()
    {
        if (_selectedRun is not { } run)
        {
            return;
        }

        try
        {
            await _client
                .RequestAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(run.Walk.RunId))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось остановить прогон {RunId}", run.Walk.RunId);
        }
    }

    /// <summary>
    /// Перечитывает часы прошедшего времени. Вызывается видом по таймеру — своего
    /// dispatcher-таймера view-model не держит, чтобы оставаться пригодной для headless-прогона.
    /// </summary>
    public void TickElapsed()
    {
        if (_selectedRun is { IsLive: true })
        {
            OnPropertyChanged(nameof(RunElapsedText));
        }
    }

    private async Task DebugAsync(DebugCommand command, string? nodeId = null)
    {
        if (_selectedRun is not { } run)
        {
            return;
        }

        DebugAckDto? ack;
        try
        {
            ack = await _client
                .RequestAsync<DebugAckDto>(
                    IpcMessageTypes.DebugCommand,
                    new DebugCommandRequest(run.WalkId, command, nodeId))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            ErrorMessage = $"Отладчик: команда не прошла ({ex.Message}).";
            return;
        }

        if (ack is null)
        {
            return;
        }

        if (!ack.Accepted)
        {
            // Обход закончился между нажатием кнопки и запросом. Скажем об этом, а не оставим
            // гореть мёртвую «Паузу», — остальное уляжется событием WalkFinished.
            StatusMessage = "Обход уже завершился.";
            RefreshDebugState();
            return;
        }

        // Оптимистичная половина: пауза, которая ещё не сработала, — это «пауза…» на панели
        // инструментов, пока её не подтвердит событие Paused от демона.
        run.PauseRequested = ack is { Paused: false, PauseRequested: true };
        RefreshDebugState();
    }

    // Все производные свойства панели инструментов в одном месте: их восемь, меняются они
    // всегда вместе, и поднимать их поодиночке в каждой точке вызова — верный способ однажды
    // одно пропустить.
    private void RefreshDebugState()
    {
        OnPropertyChanged(nameof(HasDebugTarget));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRunToCursor));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(SelectedRunIsPaused));
        OnPropertyChanged(nameof(SelectedRunAtBreakpoint));
        OnPropertyChanged(nameof(SelectedRunPauseVisible));
        OnPropertyChanged(nameof(PauseNotice));
        OnPropertyChanged(nameof(DebugStateText));
        OnPropertyChanged(nameof(RunProgressText));
        OnPropertyChanged(nameof(RunElapsedText));
        OnPropertyChanged(nameof(StopLabel));
        OnPropertyChanged(nameof(StopTooltip));
    }

    // Обходы того же ПРОГОНА, которые ещё идут, — то, что вот-вот отменит «■ Стоп».
    private int LiveSiblingCount()
    {
        if (_selectedRun is not { } run)
        {
            return 0;
        }

        return _runs.All.Count(other => other.Walk.RunId == run.Walk.RunId && other.IsLive);
    }

    /// <summary>Выбирает предыдущий обход этого макроса (◂ на чипе).</summary>
    public void SelectPreviousRun() => StepRun(-1);

    /// <summary>Выбирает следующий обход этого макроса (▸ на чипе).</summary>
    public void SelectNextRun() => StepRun(+1);

    /// <summary>
    /// Выбрасывает все записанные обходы («очистить» в полосе). Живые появятся снова, как
    /// только сообщат о своей следующей ноде, — демон-то шлёт по-прежнему: это чистит историю
    /// ПАНЕЛИ, а не останавливает что-либо.
    /// </summary>
    public void ClearRunLog()
    {
        _runs.Clear();
        _droppedRunEvents = 0;
        SelectedRun = null;
        RebuildRuns();
        OnPropertyChanged(nameof(RunLogNotice));
        OnPropertyChanged(nameof(HasRunLogNotice));
    }

    /// <summary>Заново расставляет все ноды по сетке (кнопка «Авто-раскладка»).</summary>
    public void AutoLayout()
    {
        if (!HasOpenMacro)
        {
            return;
        }

        MacroGraphLayout.Apply(Nodes, _startNodeId);
        RebuildEdges();
    }

    /// <summary>Двигает одну коробку. Вызывается непрерывно, пока ноду тащат.</summary>
    public void MoveNode(NodeRowViewModel row, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.SetPosition(x, y);
        RebuildEdges();
    }

    /// <summary>
    /// Перенацеливает исход — именно это и делает брошенная связь. <c>null</c> либо <c>""</c> в
    /// <paramref name="targetId"/> значит «конец прогона»: законное неподключённое состояние, а
    /// не удаление исхода.
    /// </summary>
    public void RewireEdge(NodeEdgeViewModel edge, string? targetId)
    {
        ArgumentNullException.ThrowIfNull(edge);
        // До ноды не добраться из её же исхода иначе как бесконечным циклом, который canvas
        // нарисовал бы узлом; правила против этого у валидатора нет, поэтому редактор просто
        // отказывается создавать такое перетаскиванием.
        var owner = Nodes.FirstOrDefault(node => node.Edges.Contains(edge));
        if (owner is not null && string.Equals(owner.NodeId, targetId, StringComparison.Ordinal))
        {
            return;
        }

        edge.TargetId = targetId ?? string.Empty;
    }

    /// <summary>Раскрывает одну коробку редактором самой себя (1e) и закрывает все прочие.</summary>
    public void ExpandNode(NodeRowViewModel? row)
    {
        foreach (var node in Nodes)
        {
            node.IsExpanded = ReferenceEquals(node, row);
        }

        if (row is not null)
        {
            SelectedNode = row;
        }
    }

    /// <summary>Складывает ту коробку, что развёрнута (Esc).</summary>
    public void CollapseNodes() => ExpandNode(null);

    // ---- точки останова ----------------------------------------------------------------

    /// <summary>
    /// Переключает красную точку на ноде и сообщает об этом демону.
    ///
    /// Набор при каждом изменении заново выводится из СТРОК и уходит целиком, поэтому
    /// переименование ноды бесплатно переносит её точку останова, а порядка добавлений и
    /// удалений, который можно перепутать, тут попросту нет.
    /// </summary>
    public void ToggleBreakpoint(NodeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.HasBreakpoint = !row.HasBreakpoint;
    }

    /// <summary>Id тех нод открытого графа, на которых сейчас стоит точка останова, в порядке строк.</summary>
    public IReadOnlyList<string> BreakpointNodeIds =>
        [.. Nodes.Where(node => node.HasBreakpoint).Select(node => node.NodeId)];

    /// <summary><c>true</c>, когда у открытого графа есть хоть одна, — этим включается «снять все».</summary>
    public bool HasBreakpoints => Nodes.Any(node => node.HasBreakpoint);

    /// <summary>Снимает все точки останова открытого графа.</summary>
    public void ClearBreakpoints()
    {
        foreach (var node in Nodes)
        {
            node.HasBreakpoint = false;
        }
    }

    // ---- панель переменных --------------------------------------------------------------

    /// <summary>
    /// Карточки «переменные макроса» в инспекторе: статическая структура из графа, живые
    /// значения из выбранного обхода.
    /// </summary>
    public ObservableCollection<MacroVariableRowViewModel> Variables { get; } = [];

    /// <summary><c>true</c>, когда есть что перечислять (а есть всегда — <c>cursor</c>).</summary>
    public bool HasVariables => Variables.Count > 0;

    /// <summary>Бейдж с числом рядом с заголовком раздела.</summary>
    public string VariableCountText => Variables.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Пунктирные связи «кто пишет → кто читает», которые рисуются на canvas при наведении на
    /// карточку переменной. Всё остальное время пусты: это подсказка на наведение, а не часть
    /// графа, и оставленные включёнными они спорили бы за внимание читателя с настоящими
    /// рёбрами.
    /// </summary>
    public ObservableCollection<CanvasLinkViewModel> VariableLinks { get; } = [];

    /// <summary>
    /// Зажигает на canvas того, кто пишет переменную, и тех, кто её читает, либо снимает
    /// подсветку, когда <paramref name="row"/> равен <c>null</c>.
    /// </summary>
    public void HighlightVariable(MacroVariableRowViewModel? row)
    {
        foreach (var card in Variables)
        {
            card.IsHighlighted = ReferenceEquals(card, row);
        }

        var writers = row is null
            ? []
            : row.Info.Writes.Select(w => w.NodeId).ToHashSet(StringComparer.Ordinal);
        var readers = row is null
            ? []
            : row.Info.Reads.Select(r => r.NodeId).ToHashSet(StringComparer.Ordinal);

        foreach (var node in Nodes)
        {
            node.IsVariableSource = writers.Contains(node.NodeId);
            node.IsVariableConsumer = readers.Contains(node.NodeId);
        }

        VariableLinks.Clear();
        if (row is null)
        {
            return;
        }

        var byId = Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        foreach (var write in row.Info.Writes)
        {
            if (!byId.TryGetValue(write.NodeId, out var from))
            {
                continue;
            }

            foreach (var read in row.Info.Reads)
            {
                if (byId.TryGetValue(read.NodeId, out var to) && !ReferenceEquals(from, to))
                {
                    VariableLinks.Add(CanvasLinkViewModel.Between(from, to));
                }
            }
        }
    }

    /// <summary>Что нашлось при последней попытке сохранения (ошибки и предупреждения).</summary>
    public ObservableCollection<ValidationIssueViewModel> Issues { get; } = [];

    /// <summary><c>true</c>, когда панели есть что показать.</summary>
    public bool HasIssues => Issues.Count > 0;

    /// <summary>
    /// Открытый граф изменили на диске, пока здесь были несохранённые правки. Редактор
    /// отказывается затирать любую из сторон и вместо этого показывает подсказку; чистый
    /// редактор просто молча перечитывает.
    /// </summary>
    public bool ChangedOnDisk
    {
        get => _changedOnDisk;
        private set => SetField(ref _changedOnDisk, value);
    }

    /// <summary>Блокирующая беда (провалившееся сохранение, плохое имя). Красным.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    /// <summary>Неблокирующая обратная связь («сохранено»). Приглушённо.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    // ---- команды библиотеки -----------------------------------------------------------

    /// <summary>Пересевает библиотеку и состояние прогонов от демона.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var macros = await _client.RequestAsync<MacroGraph[]>(IpcMessageTypes.GetMacros).ConfigureAwait(false);
            var runs = await _client.RequestAsync<RunningMacroDto[]>(IpcMessageTypes.GetRunningMacros)
                .ConfigureAwait(false);
            // Бейджу целей нужен список окон, и эта VM держит собственный, а не лезет в
            // «Окна», — см. WindowCatalog.
            var windows = await _client.RequestAsync<WindowDto[]>(IpcMessageTypes.GetWindows).ConfigureAwait(false);
            var failures = await _client.RequestAsync<HotkeyFailureDto[]>(IpcMessageTypes.GetHotkeyFailures)
                .ConfigureAwait(false);
            // Демон переживает панель, поэтому именно так возвращается точка останова,
            // поставленная до её закрытия, — в этом вся эргономическая правота хранения,
            // привязанного к сеансу.
            var breakpoints = await _client.RequestAsync<BreakpointSetDto[]>(IpcMessageTypes.GetBreakpoints)
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                _runningMacros = runs ?? [];
                _hotkeyFailures = failures ?? [];
                Windows.Reset(windows ?? []);
                _breakpoints.Clear();
                foreach (var set in breakpoints ?? [])
                {
                    _breakpoints[set.MacroName] = set.NodeIds;
                }

                ApplyLibrary(macros ?? []);
                ApplyBreakpointsToRows();
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось получить библиотеку макросов");
        }
    }

    /// <summary>
    /// Открывает свежий черновик: одна нода Delay, чтобы граф сразу был валиден и сохраняем, а
    /// не начинал жизнь с нарушения правила «стартовая нода должна существовать». До
    /// <see cref="SaveAsync"/> на диск не пишется ничего.
    /// </summary>
    public void NewMacro()
    {
        LoadGraph(new MacroGraph
        {
            Name = UniqueDraftName(),
            StartNodeId = "n1",
            Nodes = [new DelayNode { Id = "n1", Ms = 1000 }],
        });

        // У черновика ещё нет файла: сбрасываем дисковую личность, чтобы горячая перезагрузка
        // его не трогала, а «Сохранить» создавало, а не переименовывало.
        _loadedName = null;
        _diskJson = string.Empty;
        _suppressSelectionReload = true;
        SelectedMacro = null;
        _suppressSelectionReload = false;
        StatusMessage = "Черновик — не сохранён.";
    }

    /// <summary>Удаляет макрос из библиотеки (и закрывает его, если он был открыт).</summary>
    public async Task<bool> DeleteMacroAsync(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;

        try
        {
            // Удаление несуществующего макроса по протоколу — пустая операция, так что сюда
            // доходит только беда транспорта или файлового ввода-вывода.
            await _client
                .RequestAsync(IpcMessageTypes.DeleteMacro, new DeleteMacroRequest(item.Name))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            ErrorMessage = $"Не удалось удалить «{item.Name}».";
            Log.Warning(ex, "DeleteMacro '{Macro}' не выполнен", item.Name);
            return false;
        }

        if (string.Equals(_loadedName, item.Name, StringComparison.Ordinal))
        {
            CloseEditor();
        }

        // Применяем на месте, не дожидаясь пуша MacrosChanged, — так к моменту возврата отсюда
        // список уже устаканился.
        SetLibrary([.. _library.Where(macro => !string.Equals(macro.Name, item.Name, StringComparison.Ordinal))]);
        StatusMessage = $"Макрос «{item.Name}» удалён.";
        return true;
    }

    /// <summary>Запускает макрос без контекстного окна — ручной эквивалент его хоткея.</summary>
    public void Run(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;
        if (_launcher is null)
        {
            ErrorMessage = "Запуск недоступен.";
            return;
        }

        _launcher.RunMacro(item.Name);
    }

    /// <summary>Отменяет все отслеживаемые прогоны этого макроса (обычно их не больше одного).</summary>
    public async Task StopAsync(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;

        var runIds = _runningMacros
            .Where(run => string.Equals(run.MacroName, item.Name, StringComparison.Ordinal))
            .Select(run => run.RunId)
            .ToList();

        foreach (var runId in runIds)
        {
            try
            {
                await _client
                    .RequestAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(runId))
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
            {
                Log.Warning(ex, "Не удалось остановить запуск {RunId} макроса '{Macro}'", runId, item.Name);
            }
        }
    }

    // ---- команды правки ---------------------------------------------------------------

    /// <summary>Дописывает строку триггера заданного вида.</summary>
    public TriggerRowViewModel AddTrigger(MacroTriggerKind kind)
    {
        var row = TriggerRowViewModel.Create(kind);
        Triggers.Add(row);
        return row;
    }

    /// <summary>Убирает строку триггера.</summary>
    public void RemoveTrigger(TriggerRowViewModel row) => Triggers.Remove(row);

    /// <summary>Дописывает ноду заданного вида со свежесгенерированным id.</summary>
    public NodeRowViewModel AddNode(MacroNodeKind kind)
    {
        var row = NodeRowViewModel.Create(kind, NextNodeId());
        // Размещаем до того, как она попадёт в список, — так поиск свободного места не увидит
        // саму себя.
        var (x, y) = MacroGraphLayout.NextFreeSlot(Nodes);
        row.SetPosition(x, y);
        AttachNode(row);
        Nodes.Add(row);

        // Первая нода пустого графа становится стартовой — иначе самое первое, что пользователь
        // увидит после её добавления, будет ошибка валидации об отсутствующем старте.
        if (Nodes.Count == 1)
        {
            _startNodeId = row.NodeId;
            OnPropertyChanged(nameof(StartNodeId));
        }

        RebuildChoices();
        RebuildEdges();
        RebuildVariables();
        SelectedNode = row;
        return row;
    }

    /// <summary>
    /// Убирает ноду и чинит граф вокруг неё: каждое ребро, указывавшее на неё, становится
    /// «концом прогона», а стартовая нода, если она указывала туда же, переезжает на любую
    /// оставшуюся (ни на какую, если граф теперь пуст).
    /// </summary>
    public void DeleteNode(NodeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!Nodes.Remove(row))
        {
            return;
        }

        DetachNode(row);

        using (SuspendEdgeRebuild())
        {
            var removedId = row.NodeId;
            foreach (var edge in AllEdges())
            {
                if (string.Equals(edge.TargetId, removedId, StringComparison.Ordinal))
                {
                    edge.TargetId = string.Empty;
                }
            }

            if (string.Equals(_startNodeId, removedId, StringComparison.Ordinal))
            {
                _startNodeId = Nodes.Count > 0 ? Nodes[0].NodeId : string.Empty;
            }

            if (ReferenceEquals(SelectedNode, row))
            {
                SelectedNode = null;
            }

            RebuildChoices();
        }

        OnPropertyChanged(nameof(StartNodeId));
        // На удалённой ноде могла стоять точка останова, а могла она быть единственным местом,
        // где переменную записывают.
        PushBreakpoints();
        RebuildVariables();
    }

    /// <summary>Подсвечивает ноду, о которой говорит замечание.</summary>
    public void SelectIssue(ValidationIssueViewModel issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.NodeId is null)
        {
            return;
        }

        SelectedNode = Nodes.FirstOrDefault(node => string.Equals(node.NodeId, issue.NodeId, StringComparison.Ordinal));
    }

    // ---- сохранение ---------------------------------------------------------------------

    /// <summary>Собирает граф модели из текущего состояния редактора. Снисходителен — на плохом вводе не бросает никогда.</summary>
    public MacroGraph BuildGraph() => new()
    {
        Name = _macroName.Trim(),
        Triggers = [.. Triggers.Select(row => row.ToTrigger())],
        StartNodeId = _startNodeId,
        Nodes = [.. Nodes.Select(row => row.ToNode())],
    };

    /// <summary><c>true</c>, когда состояние редактора отличается от последнего загруженного или сохранённого.</summary>
    public bool IsDirty() =>
        HasOpenMacro && !string.Equals(_loadedJson, SerializeCurrent(), StringComparison.Ordinal);

    /// <summary>
    /// Проверяет открытый граф и сохраняет его.
    ///
    /// Два заслона, по порядку: сперва должны разобраться собственные поля каждой строки (это
    /// проверяется здесь — демон недонабранного числа не видит никогда, только получившийся из
    /// него граф), а затем демонский <c>SaveMacro</c> должен вернуться с пустым списком
    /// замечаний. Непустой означает, что не записали ничего, и несёт причины, включая проверку
    /// имени файла.
    /// </summary>
    /// <returns><c>false</c>, когда ничего не записано; почему — объясняет <see cref="Issues"/>.</returns>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        ClearIssues();
        ErrorMessage = null;
        StatusMessage = null;

        if (!HasOpenMacro)
        {
            return false;
        }

        var inputErrors = Triggers.SelectMany(row => row.GetInputErrors())
            .Concat(Nodes.SelectMany(row => row.GetInputErrors()))
            .ToList();
        foreach (var error in inputErrors)
        {
            AddIssue(new ValidationIssueViewModel(error, isError: true));
        }

        if (inputErrors.Count > 0)
        {
            ErrorMessage = "Сохранение отменено: исправьте ошибки.";
            return false;
        }

        var graph = BuildGraph();
        var name = graph.Name;

        // Опорные значения сдвигаются ДО запроса: демон рассылает MacrosChanged изнутри своего
        // сохранения, по насосу событий, а не по пути ответа, — так что эхо способно дойти до
        // нас первым, и обработчик горячей перезагрузки обязан опознать его как наше.
        var previousName = _loadedName;
        var previousLoadedJson = _loadedJson;
        var previousDiskJson = _diskJson;
        var json = MacroGraphJson.Serialize(graph);
        _loadedName = name;
        _loadedJson = json;
        _diskJson = json;

        ValidationIssueDto[]? rejected;
        try
        {
            rejected = await _client
                .RequestAsync<ValidationIssueDto[]>(
                    IpcMessageTypes.SaveMacro,
                    new SaveMacroRequest(graph),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            _loadedName = previousName;
            _loadedJson = previousLoadedJson;
            _diskJson = previousDiskJson;
            ErrorMessage = $"Не удалось сохранить: {ex.Message}";
            return false;
        }

        if (rejected is { Length: > 0 })
        {
            // Отказали — записано ничего не было, так что несохранённых правок в редакторе
            // остаётся ровно столько же, сколько и было.
            _loadedName = previousName;
            _loadedJson = previousLoadedJson;
            _diskJson = previousDiskJson;
            foreach (var issue in rejected)
            {
                AddIssue(new ValidationIssueViewModel(issue.ToIssue()));
            }

            ErrorMessage = "Сохранение отменено: исправьте ошибки.";
            return false;
        }

        if (previousName is not null && !string.Equals(previousName, name, StringComparison.Ordinal))
        {
            // Переименование: имя И ЕСТЬ основа имени файла, поэтому старый файл должен уйти.
            // Порядок важен — сперва записать, потом удалить, чтобы сбой между этими шагами
            // оставил две копии, а не ноль.
            try
            {
                await _client
                    .RequestAsync(IpcMessageTypes.DeleteMacro, new DeleteMacroRequest(previousName),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
            {
                Log.Warning(ex, "Переименование: старый файл '{Macro}' не удалён", previousName);
            }
        }

        // Удачное сохранение по протоколу возвращает ПУСТОЙ список, предупреждения в том числе,
        // — поэтому их выводят здесь заново тем же валидатором, который гонял демон. Ошибок тут
        // появиться не может: демон бы отказал в записи.
        foreach (var issue in MacroGraphValidator.Validate(graph))
        {
            if (issue.Severity != ValidationSeverity.Error)
            {
                AddIssue(new ValidationIssueViewModel(issue));
            }
        }

        SetLibrary(MergeSaved(graph, previousName));
        ChangedOnDisk = false;
        SelectByName(name);
        StatusMessage = Issues.Count > 0
            ? $"Сохранено с предупреждениями ({Issues.Count})."
            : "Сохранено.";
        return true;
    }

    /// <summary>Отбрасывает локальные правки и перечитывает открытый макрос из снимка библиотеки.</summary>
    public void ReloadFromDisk()
    {
        if (_loadedName is null || TryGet(_loadedName) is not { } graph)
        {
            return;
        }

        LoadGraph(graph);
        StatusMessage = "Перезагружено с диска.";
    }

    /// <summary>Загружает граф в правую панель.</summary>
    public void LoadGraph(MacroGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var row in Nodes)
        {
            DetachNode(row);
        }

        Nodes.Clear();
        Triggers.Clear();
        ClearIssues();

        _loadedName = graph.Name;
        MacroName = graph.Name;
        foreach (var trigger in graph.Triggers)
        {
            Triggers.Add(TriggerRowViewModel.FromTrigger(trigger));
        }

        using (SuspendEdgeRebuild())
        {
            foreach (var node in graph.Nodes)
            {
                var row = NodeRowViewModel.FromNode(node);
                AttachNode(row);
                Nodes.Add(row);
            }

            _startNodeId = graph.StartNodeId;
            HasOpenMacro = true;
            RebuildChoices();

            // Графы, написанные до появления canvas, координат не несут. Раскладывая их ЗДЕСЬ,
            // а не при первой отрисовке, мы берём опорное значение ниже уже вместе с
            // положениями, — поэтому открытие старого макроса не читается как несохранённая
            // правка, но сохранение его по любому другому поводу раскладку записывает.
            MacroGraphLayout.EnsurePositions(Nodes, _startNodeId);
        }

        OnPropertyChanged(nameof(StartNodeId));
        ResetView();

        // Опора для «есть несохранённые правки» — собственный round trip редактора, а не файл:
        // загрузка нормализует пару-тройку форм, и эта нормализация правкой пользователя не
        // является.
        _loadedJson = SerializeCurrent();
        _diskJson = MacroGraphJson.Serialize(graph);
        ChangedOnDisk = false;
        SelectedNode = null;
        ErrorMessage = null;
        StatusMessage = null;
        SyncCurrentFlags();

        // Сперва точки — чтобы к моменту, когда переключатель ниже зажжёт коробку, она уже была
        // помечена.
        ApplyBreakpointsToRows();
        RebuildVariables();

        // Последним, потому что он способен зажечь коробку: переключатель пересобирается для
        // ЭТОГО графа, и если по нему прямо сейчас идут, canvas подхватит прогон на лету.
        RebuildRuns();
    }

    // ---- приостановка хоткеев -----------------------------------------------------------

    /// <summary>
    /// Выключает глобальные хоткеи демона. Без этого вызова ловушка хоткея не работает вовсе —
    /// см. <see cref="IHotkeySuspension"/>. КОГДА его делать, решает оболочка (D2 привязала это
    /// к тому, что режим «Макросы» на экране); эта VM лишь пробрасывает.
    /// </summary>
    public Task SuspendHotkeysAsync() => _hotkeys?.SuspendAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Возвращает глобальные хоткеи из (возможно, только что отредактированной) библиотеки, а
    /// затем спрашивает, в каких из них Windows отказала. Порядок важен:
    /// <c>ResumeHotkeys</c> отвечает лишь после того, как испробован каждый
    /// <c>RegisterHotKey</c>, — так что к этому моменту список отказов уже устоялся.
    /// </summary>
    public async Task ResumeHotkeysAsync()
    {
        if (_hotkeys is null)
        {
            return;
        }

        await _hotkeys.ResumeAsync().ConfigureAwait(false);
        await RefreshHotkeyFailuresAsync().ConfigureAwait(false);
    }

    // ---- подписка на события прогона -----------------------------------------------------

    /// <summary>
    /// Просит демон начать (или прекратить) слать события прогона в это соединение.
    ///
    /// Оболочка привязывает это к тому, что режим «Макросы» на экране, — ровно как и
    /// приостановку хоткеев: поток здесь единственное частое в протоколе, а пока никто не
    /// подписан, демон не производит вообще ничего. Панель, стоящая в «Окнах», не должна
    /// заставлять движок форматировать строку подробностей для каждой ноды каждого макроса.
    ///
    /// В ответ приходит набор обходов, УЖЕ идущих в полёте. Их принимают с
    /// <c>FromStart = false</c>, и именно это выводит на полосу «начало прогона не записано»,
    /// вместо того чтобы молча показать обезглавленный лог.
    /// </summary>
    public async Task SetRunEventSubscriptionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _wantsRunEvents = enabled;
        // Перекладываем в поток UI: путь переподключения зовёт это из потока пула у клиента, а
        // поднятое оттуда уведомление доходит до привязки мимо потока UI.
        _dispatcher.Post(() => OnPropertyChanged(nameof(RunLogEmptyText)));

        try
        {
            var live = await _client
                .RequestAsync<RunWalkDto[]>(
                    IpcMessageTypes.SubscribeRunEvents,
                    new SubscribeRunEventsRequest(enabled),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() => AdoptLiveWalks(live ?? []));
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // Canvas без живой подсветки — всё ещё пригодный редактор, поэтому здесь
            // предупреждение, а не видимый пользователю отказ.
            Log.Warning(ex, "Не удалось {Action} поток событий прогона", enabled ? "включить" : "выключить");
        }
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.EventReceived -= OnEventReceived;
        Triggers.CollectionChanged -= OnTriggersCollectionChanged;
        foreach (var row in _watchedTriggers)
        {
            row.PropertyChanged -= OnTriggerRowChanged;
        }

        _watchedTriggers.Clear();
        foreach (var row in Nodes)
        {
            DetachNode(row);
        }
    }

    // ---- конфликты хоткеев (макет 1f, четвёртое состояние) ------------------------------

    private readonly HashSet<HotkeyTriggerRowViewModel> _watchedTriggers = [];

    // Сверяем целиком, а не пляшем от Old/NewItems события: Clear() поднимает Reset без того и
    // другого, а LoadGraph очищает список перед тем, как заново его наполнить.
    private void OnTriggersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var current = Triggers.OfType<HotkeyTriggerRowViewModel>().ToHashSet();
        foreach (var row in _watchedTriggers.Except(current).ToList())
        {
            row.PropertyChanged -= OnTriggerRowChanged;
            _watchedTriggers.Remove(row);
        }

        foreach (var row in current.Except(_watchedTriggers).ToList())
        {
            row.PropertyChanged += OnTriggerRowChanged;
            _watchedTriggers.Add(row);
        }

        RefreshHotkeyConflicts();
    }

    private void OnTriggerRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Conflict и HasConflict — это то, что данный метод ПИШЕТ; реагировать на них значило бы
        // на каждое присваивание уходить в (конечный, но бессмысленный) круг по всей библиотеке.
        if (e.PropertyName is nameof(HotkeyTriggerRowViewModel.Conflict)
            or nameof(HotkeyTriggerRowViewModel.HasConflict))
        {
            return;
        }

        RefreshHotkeyConflicts();
    }

    /// <summary>
    /// Пересчитывает обе половины «этот хоткей не сработает»: столкновение внутри библиотеки,
    /// которое панель видит сама, и регистрацию, в которой отказала Windows.
    ///
    /// Порядок намеренный. Сочетание, забранное другим макросом, так и объявляется — даже когда
    /// демону ТАКЖЕ не удалось его зарегистрировать: это одно и то же событие (демон
    /// регистрирует первого претендента, а второго Windows отвергает), и «уже занят
    /// pw-immunity» называет ровно то, что пользователь в силах исправить.
    /// </summary>
    private void RefreshHotkeyConflicts()
    {
        // Кто ещё в библиотеке владеет сочетанием. Открытый макрос пропускаем: его триггеры —
        // это СТРОКИ, которые могут уже отличаться от того, что лежит на диске.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var macro in _library)
        {
            if (_loadedName is not null && string.Equals(macro.Name, _loadedName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var trigger in macro.Triggers.OfType<HotkeyTrigger>())
            {
                if (HotkeyTriggerRowViewModel.ChordKey(trigger) is { } key)
                {
                    owners.TryAdd(key, macro.Name);
                }
            }
        }

        // Отказы регистрации, относящиеся к правимому сейчас макросу. Сопоставление по ИМЕНИ
        // макроса, а не только по сочетанию, тут важно: когда сочетание делят два макроса,
        // демон регистрирует один и отвергает другой, и победителю нельзя говорить, что его
        // собственная клавиша занята.
        var refusedHere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var failure in _hotkeyFailures)
        {
            if (_loadedName is not null && string.Equals(failure.MacroName, _loadedName, StringComparison.Ordinal))
            {
                refusedHere.Add($"K:{(int)failure.Modifiers}:{(int)failure.Key}");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Triggers.OfType<HotkeyTriggerRowViewModel>())
        {
            var key = row.ChordKey();
            if (key is null)
            {
                // Пока ничего не привязано — пустая ловушка ни с чем не сталкивается.
                row.Conflict = null;
            }
            else if (owners.TryGetValue(key, out var other))
            {
                row.Conflict = $"уже занят {other}";
            }
            else if (!seen.Add(key))
            {
                row.Conflict = "уже задан в этом макросе";
            }
            else
            {
                row.Conflict = refusedHere.Contains(key) ? "занят другим приложением" : null;
            }
        }

        RefreshLibraryHotkeyProblems();
    }

    // Строки библиотеки несут ту же новость для макросов, которых никто не открывал, — на тот
    // случай, когда хоткей умер при старте демона и сказать об этом на экране больше нечему.
    private void RefreshLibraryHotkeyProblems()
    {
        var byMacro = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var failure in _hotkeyFailures)
        {
            var chord = failure.Modifiers == HotkeyModifiers.None
                ? failure.Key.ToString()
                : $"{failure.Modifiers}+{failure.Key}";
            byMacro[failure.MacroName] = $"{chord} не зарегистрирован — сочетание занято другим приложением";
        }

        foreach (var item in Macros)
        {
            item.HotkeyProblem = byMacro.TryGetValue(item.Name, out var text) ? text : null;
        }
    }

    // ---- внутренности --------------------------------------------------------------------

    private MacroGraph? TryGet(string name) =>
        _library.FirstOrDefault(macro => string.Equals(macro.Name, name, StringComparison.Ordinal));

    private void OnConnected()
    {
        _ = RefreshAsync();

        // Подписки переподключение НЕ переживают — флаг соединения демон забывает вместе с самим
        // соединением, — а то, что успело отработать за время разрыва, не восстановить. Поэтому
        // историю выбрасываем, а подписку отправляем заново.
        _dispatcher.Post(ClearRunLog);
        if (_wantsRunEvents)
        {
            _ = SetRunEventSubscriptionAsync(true);
        }
    }

    private void OnEventReceived(IpcEvent evt)
    {
        switch (evt.Type)
        {
            case IpcMessageTypes.RunEvents:
            {
                // Читаем в потоке чтения, применяем в потоке UI: нагрузка потому и приходит
                // пачкой, чтобы это случалось несколько раз за прогон, а не сотни.
                var batch = IpcJson.Read<RunEventBatch>(evt.Payload);
                if (batch is not null)
                {
                    _dispatcher.Post(() => ApplyRunEvents(batch));
                }

                break;
            }

            case IpcMessageTypes.MacrosChanged:
                // По протоколу без нагрузки — библиотека бывает большой, поэтому демон говорит
                // «что-то изменилось», а мы идём и забираем.
                _ = ReloadLibraryAsync();
                break;

            case IpcMessageTypes.RunningMacrosChanged:
                var runs = IpcJson.Read<RunningMacroDto[]>(evt.Payload) ?? [];
                _dispatcher.Post(() =>
                {
                    _runningMacros = runs;
                    RefreshRunState();
                });
                break;

            // Оба несут ПОЛНОЕ новое состояние окна, так что одна вставка-обновление годится для
            // обоих.
            case IpcMessageTypes.WindowAppeared:
            case IpcMessageTypes.WindowTagsChanged:
                if (IpcJson.Read<WindowDto>(evt.Payload) is { } window)
                {
                    _dispatcher.Post(() => Windows.Upsert(window));
                }

                break;

            case IpcMessageTypes.WindowClosed:
                if (IpcJson.Read<WindowClosedEvent>(evt.Payload) is { } closed)
                {
                    _dispatcher.Post(() => Windows.Remove(closed.Hwnd));
                }

                break;

            default:
                break;
        }
    }

    // ---- события прогона ------------------------------------------------------------------

    /// <summary>
    /// Применяет одну пачку. Всё здесь исполняется в потоке UI и трогает только обходы — сам
    /// граф прогон не меняет никогда.
    /// </summary>
    private void ApplyRunEvents(RunEventBatch batch)
    {
        if (batch.Dropped > 0)
        {
            _droppedRunEvents += batch.Dropped;
            OnPropertyChanged(nameof(RunLogNotice));
            OnPropertyChanged(nameof(HasRunLogNotice));
        }

        var listChanged = false;
        foreach (var evt in batch.Events)
        {
            var run = _runs.Apply(evt);
            if (run is null || !IsOpenMacro(run.MacroName))
            {
                // Обход ДРУГОГО графа — его отслеживают (чтобы при открытии того графа его лог
                // был на месте), но на экране не меняется ничего.
                continue;
            }

            if (evt.Kind == RunEventKind.WalkStarted)
            {
                listChanged = true;
            }

            if (ReferenceEquals(run, _selectedRun))
            {
                SyncSelectedRunState(evt);
            }
        }

        if (listChanged)
        {
            RebuildRuns();
        }
    }

    // Выбранный обход сдвинулся: отражаем его лог в полосу, а его положение — на canvas.
    private void SyncSelectedRunState(RunEventDto evt)
    {
        switch (evt.Kind)
        {
            case RunEventKind.NodeEntered:
                RebuildRunLog();
                SyncExecutingNode();
                break;
            case RunEventKind.NodeExited:
                // Объект строки меняется на месте, поэтому полоса это уже показывает, — но у
                // КОРОБКИ теперь галочка и время, а они живут на строке ноды.
                SyncNodeRunState();
                RefreshDebugState();
                break;
            case RunEventKind.WalkFinished:
                SyncExecutingNode();
                OnPropertyChanged(nameof(SelectedRunIsLive));
                break;
            case RunEventKind.Paused:
            case RunEventKind.BreakpointHit:
            case RunEventKind.Resumed:
                SyncExecutingNode();
                break;
            case RunEventKind.VariableSet:
                SyncVariableValues();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Принимает под опеку обходы, которые шли уже на момент подписки. Это ответ демона на
    /// <c>SubscribeRunEvents</c>, и лога они не несут вовсе, — ровно в этом и признаётся
    /// <c>FromStart = false</c>.
    /// </summary>
    private void AdoptLiveWalks(IReadOnlyList<RunWalkDto> live)
    {
        if (live.Count == 0)
        {
            return;
        }

        foreach (var walk in live)
        {
            _runs.Add(walk);
        }

        RebuildRuns();
    }

    /// <summary>
    /// Пересобирает переключатель для открытого макроса и заново применяет правило выбора.
    /// Вызывается всякий раз, когда меняется набор обходов или открывают другой граф.
    /// </summary>
    private void RebuildRuns()
    {
        var forMacro = _runs.For(_loadedName);

        Runs.Clear();
        foreach (var run in forMacro)
        {
            Runs.Add(run);
        }

        OnPropertyChanged(nameof(HasRuns));

        // Сохраняем выбор, пока он ещё в силе; иначе следуем за тем, что живо, — и никогда не
        // отбираем выбор у обхода, который ещё идёт.
        if (_selectedRun is not null && Runs.Contains(_selectedRun) && _selectedRun.IsLive)
        {
            OnPropertyChanged(nameof(RunPositionText));
            // Собрат по тому же прогону мог начаться или закончиться, а «■ Стоп ×N» их считает.
            RefreshDebugState();
            return;
        }

        var newest = Runs.LastOrDefault(run => run.IsLive) ?? Runs.LastOrDefault();
        if (!ReferenceEquals(newest, _selectedRun))
        {
            SelectedRun = newest;
            return;
        }

        OnPropertyChanged(nameof(RunPositionText));
        RefreshDebugState();
    }

    private void RebuildRunLog()
    {
        RunLog.Clear();
        if (_selectedRun is not null)
        {
            foreach (var row in _selectedRun.Log)
            {
                RunLog.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasRunLog));
        OnPropertyChanged(nameof(RunLogNotice));
        OnPropertyChanged(nameof(HasRunLogNotice));
    }

    private void SyncExecutingNode()
    {
        ExecutingNodeId = _selectedRun?.CurrentNodeId;
        SyncNodeRunState();
        RefreshDebugState();
    }

    /// <summary>
    /// Отражает выбранный обход на коробки: притушено с галочкой — там, где он прошёл,
    /// «припаркован» — там, где стоит.
    ///
    /// Целиком, а не по частям, потому что источник истины — один обход, а выбор способен
    /// смениться под ним: пошаговое обновление оставило бы на графе галочки предыдущего обхода,
    /// когда переключатель уедет.
    /// </summary>
    private void SyncNodeRunState()
    {
        var run = _selectedRun;
        var paused = run?.IsPaused == true;
        foreach (var node in Nodes)
        {
            node.IsPaused = paused && node.IsExecuting;
            if (run is not null && run.Passed.TryGetValue(node.NodeId, out var passed))
            {
                node.PassedTime = passed.Time;
                node.PassedOutcome = passed.Outcome;
            }
            else
            {
                node.PassedTime = null;
                node.PassedOutcome = null;
            }
        }

        SyncVariableValues();
    }

    // ---- переменные ---------------------------------------------------------------------

    /// <summary>
    /// Заново прогоняет статический анализ по открытому графу и подцепляет обратно те живые
    /// значения, о которых сообщил выбранный обход.
    ///
    /// Вызывается всякий раз, когда меняется ФОРМА графа или параметры ноды: набранный в пути к
    /// иконке <c>{tag}</c> добавляет читателя, и панель обязана показать его ещё до того, как
    /// макрос хоть раз запускали. Дёшево: в макросе десятки нод.
    /// </summary>
    private void RebuildVariables()
    {
        var hovered = Variables.FirstOrDefault(row => row.IsHighlighted)?.RawName;

        Variables.Clear();
        if (HasOpenMacro)
        {
            foreach (var info in MacroVariableAnalysis.Analyze(BuildGraph()))
            {
                Variables.Add(new MacroVariableRowViewModel(info));
            }
        }

        OnPropertyChanged(nameof(HasVariables));
        OnPropertyChanged(nameof(VariableCountText));
        SyncVariableValues();

        // Пересборка, запущенная нажатием клавиши, не имеет права уронить подсветку, на которой
        // указатель всё ещё стоит; вместо этого выводим её заново по новым карточкам.
        HighlightVariable(hovered is null
            ? null
            : Variables.FirstOrDefault(row => string.Equals(row.RawName, hovered, StringComparison.Ordinal)));
    }

    private void SyncVariableValues()
    {
        foreach (var row in Variables)
        {
            row.Value = _selectedRun is { } run && run.Variables.TryGetValue(row.RawName, out var value)
                ? value
                : null;
        }
    }

    // ---- точки останова ------------------------------------------------------------------

    // Что держит у себя демон, по макросам. Хранится, чтобы открытие графа возвращало его точки
    // без round trip и чтобы переименование не теряло наборы остальных макросов.
    private readonly Dictionary<string, IReadOnlyList<string>> _breakpoints = new(StringComparer.Ordinal);

    // Поднят, пока ответ демона переносится на строки, — чтобы его применение не отскочило тут
    // же обратно в виде SetBreakpoints.
    private bool _applyingBreakpoints;

    // Отдельного «перечитать точки останова» здесь нет намеренно, хотя от D5 такой метод
    // оставался. Точки живут в сеансе демона, но ставит их только панель, так что её копия и
    // есть источник правды всё время, пока соединение живо; единственный момент, когда она
    // может разойтись с демоном, — переподключение, и его закрывает RefreshAsync, читающий
    // GetBreakpoints вместе с остальным снимком.
    private void ApplyBreakpointsToRows()
    {
        var wanted = _loadedName is not null && _breakpoints.TryGetValue(_loadedName, out var ids)
            ? ids.ToHashSet(StringComparer.Ordinal)
            : [];

        _applyingBreakpoints = true;
        try
        {
            foreach (var node in Nodes)
            {
                node.HasBreakpoint = wanted.Contains(node.NodeId);
            }
        }
        finally
        {
            _applyingBreakpoints = false;
        }

        OnPropertyChanged(nameof(HasBreakpoints));
    }

    private void PushBreakpoints()
    {
        if (_applyingBreakpoints || _loadedName is null)
        {
            // У черновика ещё нет имени, по которому можно ключеваться. Его точки остаются
            // местными до сохранения, а тогда их отправит пуш из LoadGraph.
            return;
        }

        var ids = BreakpointNodeIds;
        _breakpoints[_loadedName] = ids;
        OnPropertyChanged(nameof(HasBreakpoints));
        _ = SendBreakpointsAsync(_loadedName, ids);
    }

    private async Task SendBreakpointsAsync(string macroName, IReadOnlyList<string> nodeIds)
    {
        try
        {
            await _client
                .RequestAsync(IpcMessageTypes.SetBreakpoints, new SetBreakpointsRequest(macroName, nodeIds))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось передать брейкпоинты макроса '{Macro}'", macroName);
        }
    }

    private void StepRun(int delta)
    {
        if (Runs.Count == 0 || _selectedRun is null)
        {
            return;
        }

        var index = Runs.IndexOf(_selectedRun);
        if (index < 0)
        {
            return;
        }

        // По кругу: когда в веере десять обходов, упереться в край и разворачиваться обратно —
        // не то ощущение, которого ждёшь от чипа с двумя стрелками.
        SelectedRun = Runs[((index + delta) % Runs.Count + Runs.Count) % Runs.Count];
    }

    private bool IsOpenMacro(string macroName) =>
        _loadedName is not null && string.Equals(_loadedName, macroName, StringComparison.Ordinal);

    private async Task ReloadLibraryAsync()
    {
        try
        {
            var macros = await _client.RequestAsync<MacroGraph[]>(IpcMessageTypes.GetMacros).ConfigureAwait(false);
            _dispatcher.Post(() => ApplyLibrary(macros ?? []));
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось перечитать библиотеку макросов");
        }

        await RefreshHotkeyFailuresAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Перечитывает, какие сочетания демону не удалось зарегистрировать.
    ///
    /// Это запрос по требованию, и моментов у него три: переподключение, изменение библиотеки и
    /// возврат из <see cref="ResumeHotkeysAsync"/>. Последний — несущий: пока открыт режим
    /// «Макросы», демон держит все сочетания незарегистрированными, так что хоткей, привязанный
    /// в редакторе, пробуют только на выходе из режима, и приговор приходит ровно в тот миг,
    /// когда отвечает <c>ResumeHotkeys</c>.
    /// </summary>
    private async Task RefreshHotkeyFailuresAsync()
    {
        try
        {
            var failures = await _client
                .RequestAsync<HotkeyFailureDto[]>(IpcMessageTypes.GetHotkeyFailures)
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                _hotkeyFailures = failures ?? [];
                RefreshHotkeyConflicts();
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось получить список незарегистрированных хоткеев");
        }
    }

    private void ApplyLibrary(IReadOnlyList<MacroGraph> macros)
    {
        SetLibrary(macros);

        if (!HasOpenMacro || _loadedName is null)
        {
            return; // ничего не открыто либо это несохранённый черновик, за файлом которого следить нечего
        }

        var onDisk = macros.FirstOrDefault(m => string.Equals(m.Name, _loadedName, StringComparison.Ordinal));
        if (onDisk is null)
        {
            ChangedOnDisk = true;
            StatusMessage = $"Файл «{_loadedName}» исчез с диска — сохранение создаст его заново.";
            return;
        }

        var json = MacroGraphJson.Serialize(onDisk);
        if (string.Equals(json, _diskJson, StringComparison.Ordinal))
        {
            return; // это отзвук нашей же записи либо изменился посторонний файл
        }

        if (IsDirty())
        {
            // Несохранённую работу не затираем никогда — поднимаем флаг и даём пользователю
            // выбрать сторону.
            _diskJson = json;
            ChangedOnDisk = true;
            return;
        }

        LoadGraph(onDisk);
        StatusMessage = "Макрос обновлён на диске — перечитан.";
    }

    private void SetLibrary(IReadOnlyList<MacroGraph> macros)
    {
        _library = macros;
        RebuildLibrary(macros);
        RefreshRunState();
        // Макрос, только что занявший (или отпустивший) сочетание, меняет то, с чем сталкивается
        // каждая открытая ловушка, а RebuildLibrary наделала новых объектов строк, которым нужны
        // их отметки.
        RefreshHotkeyConflicts();
    }

    // Записанный граф сразу же заменяет запись в снимке (или добавляется к ним), а
    // переименование выбрасывает старую, — поэтому список и выделение верны ещё до прихода
    // MacrosChanged.
    private IReadOnlyList<MacroGraph> MergeSaved(MacroGraph graph, string? renamedFrom)
    {
        var next = _library
            .Where(macro => !string.Equals(macro.Name, graph.Name, StringComparison.Ordinal)
                            && (renamedFrom is null ||
                                !string.Equals(macro.Name, renamedFrom, StringComparison.Ordinal)))
            .Append(graph)
            .OrderBy(macro => macro.Name, StringComparer.Ordinal)
            .ToList();
        return next;
    }

    private void RebuildLibrary(IReadOnlyList<MacroGraph> macros)
    {
        // ОТКРЫТЫЙ макрос предпочтительнее подсвеченной сейчас строки: после сохранения нового
        // макроса список пересобирается из отложенного события, и опора на прежнее выделение
        // сняла бы подсветку ровно с того макроса, который пользователь правит.
        var previous = _loadedName ?? _selectedMacro?.Name;
        _suppressSelectionReload = true;
        try
        {
            Macros.Clear();
            foreach (var macro in macros)
            {
                Macros.Add(new MacroListItemViewModel(macro));
            }

            SelectedMacro = previous is null
                ? null
                : Macros.FirstOrDefault(item => string.Equals(item.Name, previous, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelectionReload = false;
        }

        SyncCurrentFlags();
        RebuildGroups();
        RebuildMacroChoices(macros);
    }

    private void RebuildMacroChoices(IReadOnlyList<MacroGraph> macros)
    {
        var names = macros.Select(m => m.Name).ToList();
        // Ссылку на несуществующий больше макрос оставляем выбираемой, чтобы открытие графа,
        // чей вложенный макрос удалили, не обнуляло эту ссылку молча.
        foreach (var row in Nodes.OfType<RunMacroNodeRowViewModel>())
        {
            if (row.MacroName.Length > 0 && !names.Contains(row.MacroName, StringComparer.Ordinal))
            {
                names.Add(row.MacroName);
            }
        }

        Replace(MacroChoices, names);
    }

    private void RefreshRunState()
    {
        var running = _runningMacros
            .Select(run => run.MacroName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in Macros)
        {
            item.IsRunning = running.Contains(item.Name);
        }
    }

    private void SelectByName(string name)
    {
        _suppressSelectionReload = true;
        try
        {
            SelectedMacro = Macros.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelectionReload = false;
        }
    }

    private void CloseEditor()
    {
        foreach (var row in Nodes)
        {
            DetachNode(row);
        }

        Nodes.Clear();
        Triggers.Clear();
        CanvasEdges.Clear();
        ClearIssues();
        _loadedName = null;
        _loadedJson = string.Empty;
        _diskJson = string.Empty;
        MacroName = string.Empty;
        _startNodeId = string.Empty;
        OnPropertyChanged(nameof(StartNodeId));
        HasOpenMacro = false;
        ChangedOnDisk = false;
        SelectedNode = null;
        RebuildChoices();
        SyncCurrentFlags();
        RebuildVariables();
        // Граф не открыт ⇒ следовать не за чем. Сами обходы остаются отслеживаемыми, так что
        // повторное открытие макроса возвращает его лог.
        RebuildRuns();
    }

    private void AttachNode(NodeRowViewModel row)
    {
        row.IdChanged += OnNodeIdChanged;
        row.PropertyChanged += OnNodeRowChanged;
        foreach (var edge in row.Edges)
        {
            edge.Choices = NodeIdChoices;
            // Перенацеливание исхода двигает линию на canvas — сделали ли это выпадающим
            // списком в инспекторе или перетаскиванием порта.
            edge.PropertyChanged += OnEdgeChanged;
        }

        if (row is RunMacroNodeRowViewModel runMacro)
        {
            runMacro.MacroChoices = MacroChoices;
        }

        // Тот же приём с общим экземпляром, что и у списков выбора: каталог один, и каждый бейдж
        // на canvas пересчитывается, когда окно появляется или получает тег.
        if (row.Target is { } target)
        {
            target.Windows = Windows;
        }
    }

    private void DetachNode(NodeRowViewModel row)
    {
        row.IdChanged -= OnNodeIdChanged;
        row.PropertyChanged -= OnNodeRowChanged;
        foreach (var edge in row.Edges)
        {
            edge.PropertyChanged -= OnEdgeChanged;
        }

        if (row.Target is { } target)
        {
            // Снимает подписку селектора на каталог: строки закрытого графа не должны и дальше
            // пересчитывать бейджи, на которые никто не смотрит.
            target.Windows = null;
        }
    }

    private void OnEdgeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeEdgeViewModel.TargetId))
        {
            RebuildEdges();
        }
    }

    /// <summary>
    /// Изменилось собственное состояние ноды. Отсюда следуют две вещи, и обе достаточно дёшевы,
    /// чтобы делать их на каждое нажатие клавиши в графе из десятков нод.
    ///
    /// Сигналом «изменился параметр» служит <c>Summary</c>, а не прослушивание каждого из ~25
    /// свойств-параметров по имени: базовый класс уже переподнимает его ровно для этого набора
    /// и исключает чисто оформительские, — так что расходиться по мере добавления новых типов
    /// нод здесь нечему.
    /// </summary>
    private void OnNodeRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NodeRowViewModel.HasBreakpoint):
                PushBreakpoints();
                break;
            case nameof(NodeRowViewModel.Summary):
                // Набранный в пути к иконке {tag} добавляет читателя — панель обязана показать
                // его ещё до того, как макрос хоть раз запускали.
                RebuildVariables();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Пересчитывает маршрут каждого ребра. Дёшево (в макросе десятки нод, а не тысячи) и
    /// вызывается на каждом кадре перетаскивания ноды — именно это и держит линии приклеенными
    /// к коробке.
    /// </summary>
    private void RebuildEdges()
    {
        if (_edgeRebuildSuspended > 0)
        {
            return;
        }

        CanvasEdges.Clear();
        foreach (var edge in CanvasEdgeRouter.BuildAll(Nodes))
        {
            CanvasEdges.Add(edge);
        }
    }

    // Собирает структурную правку в пачку, чтобы canvas проложили один раз, в конце и по
    // согласованному графу.
    private IDisposable SuspendEdgeRebuild()
    {
        _edgeRebuildSuspended++;
        return new EdgeRebuildScope(this);
    }

    private sealed class EdgeRebuildScope(MacroEditorViewModel owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            owner._edgeRebuildSuspended--;
            owner.RebuildEdges();
        }
    }

    // Библиотека — дерево групп, поэтому «какая строка подсвечена» — это флаг на самой строке, а
    // не выделение в ListBox.
    private void SyncCurrentFlags()
    {
        var current = _loadedName ?? _selectedMacro?.Name;
        foreach (var item in Macros)
        {
            item.IsCurrent = current is not null
                             && string.Equals(item.Name, current, StringComparison.Ordinal);
        }
    }

    private void RebuildGroups()
    {
        var visible = _librarySearch.Trim().Length == 0
            ? Macros.AsEnumerable()
            : Macros.Where(item =>
                item.Name.Contains(_librarySearch.Trim(), StringComparison.CurrentCultureIgnoreCase));

        MacroGroups.Clear();
        foreach (var group in MacroLibraryGrouping.Build(visible))
        {
            MacroGroups.Add(group);
        }
    }

    // Переименование ноды обязано утащить за собой входящие в неё рёбра, иначе оно молча
    // перерубит каждую ведущую в неё связь.
    private void OnNodeIdChanged(NodeRowViewModel row, string previousId)
    {
        foreach (var edge in AllEdges())
        {
            if (string.Equals(edge.TargetId, previousId, StringComparison.Ordinal))
            {
                edge.TargetId = row.NodeId;
            }
        }

        if (string.Equals(_startNodeId, previousId, StringComparison.Ordinal))
        {
            _startNodeId = row.NodeId;
        }

        RebuildChoices();
        RebuildEdges();
        OnPropertyChanged(nameof(StartNodeId));
        // Набор точек останова ключуется по id ноды, поэтому переименование надо отправить
        // заново, — точка при этом остаётся на строке, и именно ради этого набор выводится из
        // строк, а не отслеживается отдельно.
        PushBreakpoints();
        RebuildVariables();
    }

    private IEnumerable<NodeEdgeViewModel> AllEdges() => Nodes.SelectMany(node => node.Edges);

    // Пересборка общих списков выбора заставляет каждый привязанный ComboBox переоценить своё
    // выделение, а SelectedItem, на миг выпавший из ItemsSource, возвращается как null. Снимок
    // задуманных значений вокруг пересборки — а не сравнение списков по разнице — не даёт этой
    // мимолётности молча переписать рёбра графа.
    private void RebuildChoices()
    {
        var edges = AllEdges().ToList();
        var targets = edges.Select(edge => edge.TargetId).ToArray();
        var start = _startNodeId;

        var ids = Nodes.Select(node => node.NodeId).ToList();

        var edgeChoices = new List<string>(ids.Count + 2) { string.Empty };
        edgeChoices.AddRange(ids);
        // Правленный руками файл способен направить ребро на несуществующую ноду. Оставляем это
        // значение выбираемым, чтобы редактор показывал правду, а валидатор мог на неё
        // пожаловаться, — вместо того чтобы тихо переписать её в «конец прогона».
        foreach (var target in targets)
        {
            if (target.Length > 0 && !edgeChoices.Contains(target, StringComparer.Ordinal))
            {
                edgeChoices.Add(target);
            }
        }

        var startChoices = new List<string>(ids);
        if (start.Length > 0 && !startChoices.Contains(start, StringComparer.Ordinal))
        {
            startChoices.Add(start);
        }

        Replace(NodeIdChoices, edgeChoices);
        Replace(StartNodeChoices, startChoices);

        for (var i = 0; i < edges.Count; i++)
        {
            edges[i].TargetId = targets[i];
        }

        _startNodeId = start;
        OnPropertyChanged(nameof(StartNodeId));
    }

    private string NextNodeId()
    {
        var used = Nodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        for (var i = 1;; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"n{i}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private string UniqueDraftName()
    {
        var used = Macros.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(DraftName))
        {
            return DraftName;
        }

        for (var i = 2;; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{DraftName}-{i}");
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private string SerializeCurrent()
    {
        try
        {
            return MacroGraphJson.Serialize(BuildGraph());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Достижимо, только если строка выдала нечто несериализуемое; считаем это «отличным
            // от чего угодно», чтобы состояние читалось как «есть несохранённые правки», а не
            // как чистое.
            return Guid.NewGuid().ToString();
        }
    }

    private void AddIssue(ValidationIssueViewModel issue)
    {
        Issues.Add(issue);
        OnPropertyChanged(nameof(HasIssues));
    }

    private void ClearIssues()
    {
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
    }

    /// <summary>
    /// Сверяет список выбора НА МЕСТЕ.
    ///
    /// Не <c>Clear()</c> с последующим наполнением, каким это было раньше. Очистка поднимает
    /// Reset, а каждый привязанный к списку <c>ComboBox</c> отвечает на Reset сбросом своего
    /// <c>SelectedItem</c>, — из-за чего повторное открытие и без того открытого макроса (путь
    /// «Перечитать», где id до и после ОДИНАКОВЫ) оставляло выбор стартовой ноды пустым.
    /// Пересборка на месте означает, что в обычном случае не поднимается вообще ничего.
    /// </summary>
    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(values[i]);
            }
            else if (!string.Equals(target[i], values[i], StringComparison.Ordinal))
            {
                target[i] = values[i];
            }
        }

        while (target.Count > values.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
