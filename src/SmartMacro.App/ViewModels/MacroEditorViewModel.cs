using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Macros;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Один макрос в левом списке библиотеки редактора — <b>в том числе тот, который не читается</b>.
///
/// До F3 библиотека приезжала списком графов (<c>GetMacros</c>), а хранилище демона молча
/// пропускало испорченный бандл: файл лежал в папке, но в интерфейсе его не было ВООБЩЕ. Теперь
/// папку читает панель, и нечитаемый бандл остаётся строкой — с восклицательным знаком и
/// причиной. Пользователь должен видеть, что макрос существует, но с ним беда: иначе единственный
/// способ узнать об этом — открыть папку проводником.
///
/// Отдельный случай, ради которого <c>metadata.json</c> и <c>nodes.json</c> и разносили:
/// <b>паспорт мог прочитаться, когда граф не прочитался</b>. Тогда строка несёт НАСТОЯЩЕЕ имя и
/// описание, а не «файл X — ошибка», из которой не понять даже, какой это был макрос.
/// </summary>
public sealed class MacroListItemViewModel : ObservableObject
{
    private bool _isRunning;
    private bool _isCurrent;
    private string? _hotkeyProblem;

    /// <param name="entry">Строка папки: бандл ровно в том виде, в каком он прочитался.</param>
    /// <param name="issues">
    /// Что сказал валидатор об этом графе. Панель считает его сама и тем же кодом, каким считает
    /// демон при загрузке (<see cref="MacroGraphValidator"/> живёт в <c>Shared</c>), — поэтому
    /// «почему хоткей молчит» она объясняет без единого запроса.
    /// </param>
    public MacroListItemViewModel(MacroBundleEntry entry, IReadOnlyList<ValidationIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Name = entry.Name;
        ErrorCount = issues?.Count(issue => issue.Severity == ValidationSeverity.Error) ?? 0;

        if (entry.Graph is { } macro)
        {
            Summary = Describe(macro);
            TriggerBadge = Badge(macro);
            HasHotkeyTrigger = macro.Triggers.OfType<HotkeyTrigger>().Any();
            return;
        }

        // Бандл не читается. Имя строки — по-прежнему основа имени файла (она и есть личность),
        // а вот подпись берётся из паспорта, если тот уцелел.
        Problem = entry.FaultMessage ?? Strings.Macros_Row_BrokenFallback;
        BrokenDetail = entry.Metadata is { } passport
            ? string.IsNullOrWhiteSpace(passport.Description)
                ? passport.Name
                : $"{passport.Name} — {passport.Description}"
            : Strings.Macros_Row_BrokenNoPassport;
        Summary = $"{BrokenDetail} · {Problem}";
    }

    /// <summary>Имя макроса = основа имени файла = его личность.</summary>
    public string Name { get; }

    /// <summary>Триггеры и число нод — текст подсказки. У нечитаемого бандла — имя из паспорта и причина.</summary>
    public string Summary { get; }

    /// <summary>
    /// Почему бандл не открылся, вердиктом читателя целиком, либо <c>null</c> у здорового
    /// макроса. Строка при этом из списка НЕ пропадает — см. примечание к типу.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// Вторая строка у испорченного макроса: имя и описание из паспорта, если он прочитался.
    /// Именно ради этого случая формат и держит паспорт отдельной записью.
    /// </summary>
    public string? BrokenDetail { get; }

    /// <summary><c>true</c> у бандла, чей граф не прочитался.</summary>
    public bool IsBroken => Problem is not null;

    /// <summary>Сколько ошибок нашёл валидатор. Больше нуля — демон не вооружит триггеры этого макроса.</summary>
    public int ErrorCount { get; }

    /// <summary>У макроса есть хоткей-триггер — значит, «не вооружён» про него говорить осмысленно.</summary>
    public bool HasHotkeyTrigger { get; }

    /// <summary>Запуск возможен: граф прочитан и в нём нет ошибок, а прогона сейчас нет.</summary>
    public bool CanRun => !IsBroken && ErrorCount == 0 && !_isRunning;

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
                OnPropertyChanged(nameof(CanRun));
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
            : Strings.Macros_Row_NoTriggers;
        return string.Format(CultureInfo.CurrentCulture, Strings.Macros_Row_Summary,
            triggerText, macro.Nodes.Count);
    }

    private static string? Badge(MacroGraph macro) => macro.Triggers.Count switch
    {
        0 => null,
        _ => macro.Triggers[0] switch
        {
            HotkeyTrigger { IsMouse: true } hotkey => HotkeyNames.Chord(hotkey.Modifiers, hotkey.MouseButton.ToString()),
            HotkeyTrigger hotkey => HotkeyNames.Chord(hotkey.Modifiers, hotkey.Key.ToString()),
            ProcessAppearedTrigger => Strings.Macros_Row_TriggerProcess,
            var other => other.GetType().Name,
        },
    };

    // ⚠️ Не Modifiers.ToString(): у флагового перечисления он даёт «Alt, Control+F9» — с
    // запятой и словом, которого Windows не пишет, — тогда как ловушка клавиш в двух сантиметрах
    // отсюда рисует тот же аккорд кейкапами «Ctrl Shift F1». Найдено обходом раскладки: длинная
    // запись переполняла строку библиотеки и заезжала под кнопки ▸ ■ ⤓ ×. Правило имён теперь
    // одно, в HotkeyNames.
    private static string DescribeTrigger(MacroTrigger trigger) => trigger switch
    {
        HotkeyTrigger { IsMouse: true } hotkey => HotkeyNames.Chord(hotkey.Modifiers, hotkey.MouseButton.ToString()),
        HotkeyTrigger hotkey => HotkeyNames.Chord(hotkey.Modifiers, hotkey.Key.ToString()),
        ProcessAppearedTrigger process => string.Format(
            CultureInfo.CurrentCulture, Strings.Macros_Row_TriggerProcessNamed, process.ProcessName),
        _ => trigger.GetType().Name,
    };
}

/// <summary>
/// Один ПОД-МАКРОС в дереве библиотеки — вложенная строка под своим макросом (волна F4).
///
/// Проще строки макроса намеренно, и это не экономия. У функции нет ни триггера, ни отдельного
/// файла, ни собственных шаблонов, значит нечего показывать бейджем, нечего экспортировать и
/// нечего запускать самой по себе: ▸ на ней означало бы запуск, которого протокол не умеет и
/// который был бы неправдой про «функцию». Остаётся то, что у неё есть на самом деле, — подпись,
/// число нод и × (удалить).
/// </summary>
public sealed class SubmacroListItemViewModel : ObservableObject
{
    private bool _isCurrent;

    internal SubmacroListItemViewModel(string macroName, MacroSubmacro submacro, int errorCount)
    {
        ArgumentNullException.ThrowIfNull(submacro);
        MacroName = macroName;
        Id = submacro.Id;
        Name = submacro.Name;
        ErrorCount = errorCount;
        Summary = string.Format(CultureInfo.CurrentCulture, Strings.Macros_SubmacroRow_Summary,
            submacro.Graph.Nodes.Count);
    }

    /// <summary>Макрос, которому под-макрос принадлежит, — то есть файл, в котором он лежит.</summary>
    public string MacroName { get; }

    /// <summary>Личность под-макроса; по ней на него ссылается нода вызова.</summary>
    public Guid Id { get; }

    /// <summary>Подпись — то, что видно в дереве и в списке ноды вызова.</summary>
    public string Name { get; }

    /// <summary>Текст подсказки.</summary>
    public string Summary { get; }

    /// <summary>Сколько ошибок валидатор нашёл ИМЕННО В ЭТОМ графе. Больше нуля — макрос не вооружён.</summary>
    public int ErrorCount { get; }

    /// <summary><c>true</c>, когда в графе этой функции есть ошибка.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Именно этот граф сейчас на канве.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetField(ref _isCurrent, value);
    }
}

/// <summary>Одна строка панели валидации.</summary>
public sealed class ValidationIssueViewModel
{
    public ValidationIssueViewModel(ValidationIssue issue, string? submacroName = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        IsError = issue.Severity == ValidationSeverity.Error;
        NodeId = issue.NodeId;
        SubmacroId = issue.SubmacroId;
        // Адресуемся по id, печатаем имя: guid читателю ничего не говорит, а имя может
        // повторяться (это всего лишь предупреждение), так что одного из двух не хватает.
        // Под-макрос называется ПЕРВЫМ: без него «[find-3] ребро ведёт в ноду, которой нет»
        // отправляет читателя искать find-3 в открытом графе, где её нет вовсе.
        var where = (submacroName, issue.NodeName) switch
        {
            ({ } sub, { } node) => $"[{sub} ▸ {node}] ",
            ({ } sub, null) => $"[{sub}] ",
            (null, { } node) => $"[{node}] ",
            _ => string.Empty,
        };
        Display = where + issue.Message;
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
    public Guid? NodeId { get; }

    /// <summary>
    /// Граф, которому нода принадлежит: под-макрос либо <c>null</c> — верхний уровень. Клик по
    /// замечанию переключает канву именно по нему.
    /// </summary>
    public Guid? SubmacroId { get; }

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
/// <b>Волна F3 вернула сюда файлы, но не демона.</b> На стадии 3 эта VM отдала библиотеку демону и
/// смотрела на неё через <c>GetMacros</c>/<c>SaveMacro</c>; теперь она держит
/// <see cref="MacroLibrary"/> — папку <c>macros/</c> со своим наблюдателем — и пишет туда сама.
/// Демон эти файлы только читает. Четыре следствия, которые стоит знать, прежде чем править класс:
///
///   * <b>Валидатор — местный, и он же демонский.</b> <see cref="MacroGraphValidator"/> живёт в
///     <c>Shared</c>: панель гоняет его на каждой записи, демон — при загрузке библиотеки. Один
///     код, одна опись шаблонов, один вердикт; разойтись им негде, и потому «почему хоткей
///     молчит» панель объясняет без запросов. <b>Записи ошибка не мешает</b> — см.
///     <see cref="SaveAsync"/>.
///   * <b>Эха собственной записи ждать не надо.</b> Файл записан к моменту возврата из
///     <see cref="SaveAsync"/>, снимок библиотеки перечитан там же. Наблюдатель принесёт то же
///     самое содержимое спустя гашение дребезга, и <see cref="ApplyLibrary"/> узнает его по
///     сравнению СОДЕРЖИМОГО (<c>_diskJson</c>) — этой проверки достаточно, подавления записей,
///     как было у демона, здесь нет.
///   * <b>Нечитаемый бандл — это строка списка, а не пропажа.</b> См.
///     <see cref="MacroListItemViewModel"/>.
///   * <b>Про демона осталось ровно одно: какие сочетания взяла Windows.</b> Пуш
///     <c>MacrosChanged</c> теперь значит «демон перечитал папку и перерегистрировал хоткеи», и
///     единственный правильный ответ на него — перечитать <c>GetHotkeyFailures</c>.
///
/// <b>Состояния «черновик» здесь нет: панель сохраняет макрос сама.</b> Файл заводится в момент
/// создания (<see cref="NewMacro"/>), а дальше правки записываются ПО ЗАТИХАНИЮ — через
/// <see cref="AutoSaveQuietTicks"/> тиков после последней, — см. <see cref="TickAutoSave"/>. Три
/// следствия, о которых стоит знать, прежде чем что-то здесь «упрощать»:
///
///   * <b>По затиханию, а не по таймеру.</b> Каждая запись будит демона: он перечитывает папку,
///     перерегистрирует ВСЕ хоткеи и целиком сбрасывает кэш шаблонов. По таймеру это были бы
///     десятки пробуждений за сеанс правки у движка, который может прямо сейчас вести игру.
///   * <b>Пишем и с ошибками валидации.</b> Граф невалиден ровно тогда, когда над ним работают, —
///     отказ записи и автосохранение несовместимы. Предохранитель стоит на другой стороне:
///     демон не вооружает невалидный макрос, а строка библиотеки несёт красный «!».
///   * <b>Автосохранение НИКОГДА не переименовывает и никогда не спрашивает.</b> Пишет оно в
///     ЗАГРУЖЕННОЕ имя (<c>_loadedName</c>), а не в набираемое, иначе каждое нажатие в поле имени
///     плодило бы файлы. Переименование поэтому осталось ручным — <see cref="CommitRenameAsync"/>.
///
/// Всё, что имеет форму Avalonia, держится снаружи намеренно, чтобы класс целиком можно было
/// гонять headless против поддельного <see cref="IIpcClient"/> и настоящей библиотеки во временной
/// папке, — а это важно, потому что отображение «граф ↔ VM» и есть то место, где завелась бы тихая
/// потеря данных. Часы автосохранения по той же причине живут в виде и дёргают
/// <see cref="TickAutoSave"/>: тест гоняет их вручную, никаких настоящих задержек.
/// </summary>
public sealed class MacroEditorViewModel : ObservableObject, IDisposable
{
    private readonly IIpcClient _client;
    private readonly MacroLibrary _macros;
    private readonly IMacroLauncher? _launcher;
    private readonly IHotkeySuspension? _hotkeys;
    private readonly IMacroNameConflictPrompt? _conflicts;
    private readonly IRegionCapturePrompt? _regions;
    private readonly IUiDispatcher _dispatcher;

    private IReadOnlyList<MacroBundleEntry> _library = [];
    private IReadOnlyList<RunningMacroDto> _runningMacros = [];
    private IReadOnlyList<HotkeyFailureDto> _hotkeyFailures = [];

    private MacroListItemViewModel? _selectedMacro;
    private NodeRowViewModel? _selectedNode;
    private bool _suppressSelectionReload;

    // Имя открытого сейчас макроса в том виде, в каком оно есть в библиотеке — то есть основа
    // имени ЕГО ФАЙЛА. Файл заводится в момент создания макроса, поэтому у открытого макроса это
    // поле не бывает пустым: состояния «черновик, которого ещё нет на диске» больше нет.
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

    // ---- бандл на канве (волна F4) ----------------------------------------------------
    //
    // Канва показывает ОДИН граф за раз, а бандл их содержит несколько. Разложено так:
    //   * Nodes / _startNodeId / Triggers — граф, который сейчас на канве;
    //   * _submacros — ВСЕ под-макросы бандла (у открытого запись устаревшая, её подменяет
    //     BuildSubmacros);
    //   * _parkedParent — родитель, отложенный на время, пока на канве функция.
    //
    // Второй набор строк для второго графа не заводится намеренно: строка ноды тянет за собой
    // подписку на каталог окон, кэш выбора и точки останова, и держать это для графа, на который
    // никто не смотрит, значило бы завести вторую живую копию редактора.
    private readonly List<MacroSubmacro> _submacros = [];
    private Guid? _openSubmacroId;
    private MacroGraph? _parkedParent;
    private string _submacroName = string.Empty;

    private Guid _startNodeId;
    private bool _hasOpenMacro;
    private bool _changedOnDisk;
    private string? _errorMessage;
    private string? _statusMessage;

    // ---- автосохранение ---------------------------------------------------------------
    //
    // Содержимое бандла на ПРОШЛОМ тике часов; совпало — значит между тиками ничего не правили.
    // Опора «по затиханию» строится именно на сравнении содержимого, а не на крючках в местах
    // правки: мест этих десятки (строка ноды, ребро, триггер, имя, стартовая нода, перетаскивание
    // коробки, правка функции), и забытое означало бы правку, которая не сохранится НИКОГДА.
    private string? _seenJson;
    private int _quietTicks;

    // Идёт запись — не начинаем вторую. Несёт и время, пока открыт вопрос о занятом имени:
    // модальный диалог крутит свой цикл диспетчера, то есть часы во время него тикают.
    private bool _writeInFlight;

    // Дирти-флаг, снятый последним тиком: подпись состояния читает его, чтобы не сериализовать
    // бандл заново на каждое нажатие клавиши в поле имени.
    private bool _dirtySeen;
    private string? _saveStateText;

    // Почему автосохранение стоит, если стоит. Поле, а не разовое присваивание подписи: тик
    // пересчитывает подпись каждые полсекунды, и разовое сообщение мигало бы, сменяясь обратно на
    // «правки ещё не записаны».
    private string? _autoSaveBlocked;

    // Имя, на которое уже ответили отказом. Нужно ровно затем, чтобы уход фокуса из поля имени
    // не спрашивал про одно и то же занятое имя снова и снова.
    private string? _renameRefused;

    private readonly MacroRunTracker _runs = new();

    private string _librarySearch = string.Empty;
    private double _zoom = 1;
    private double _panX = MinPan;
    private double _panY = MinPan;
    private Guid? _executingNodeId;
    private MacroRunViewModel? _selectedRun;
    private bool _wantsRunEvents;

    private int _droppedRunEvents;

    // Геометрия рёбер пересобирается по нодам; пока в полёте пачка структурных правок
    // (загрузка, удаление, перенацеливающее рёбра), пересборка откладывается до конца, чтобы
    // canvas не прокладывали по наполовину обновлённому графу.
    private int _edgeRebuildSuspended;

    /// <param name="client">Соединение с демоном — прогоны, хоткеи, отладчик. Библиотека сюда больше не ходит.</param>
    /// <param name="macros">Папка <c>macros/</c>: чтение, запись, импорт. Панель — её единственный автор.</param>
    /// <param name="launcher">Шов для ручного «Запустить»; <c>null</c> гасит кнопку.</param>
    /// <param name="hotkeys">Приостановка и возобновление вокруг ловушки сочетаний; <c>null</c> ничего не делает.</param>
    /// <param name="dispatcher">Перекладывание пушей демона и событий наблюдателя в поток UI.</param>
    /// <param name="conflicts">
    /// Вопрос пользователю о занятом имени. <c>null</c> означает «спросить не у кого», и тогда
    /// ответом считается отмена: молча заменить чужой макрос — худший из возможных исходов.
    /// </param>
    /// <param name="regions">
    /// Диалог «выдели область на свежем снимке окна». <c>null</c> означает «спросить не у кого», и
    /// тогда кнопка вырезки просто ничего не делает — ровно как <see cref="_conflicts"/> выше и по
    /// той же причине: путь дизайнера и headless-тесты, где диалог не проверяют. Кнопка при этом
    /// НЕ гаснет, потому что гасить её пришлось бы привязкой из шаблона ноды к view-model
    /// редактора, а такой связи в этой разметке нет ни у чего.
    /// </param>
    public MacroEditorViewModel(
        IIpcClient client,
        MacroLibrary macros,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null,
        IUiDispatcher? dispatcher = null,
        IMacroNameConflictPrompt? conflicts = null,
        IRegionCapturePrompt? regions = null)
    {
        ArgumentNullException.ThrowIfNull(macros);
        _client = client;
        _macros = macros;
        _launcher = launcher;
        _hotkeys = hotkeys;
        _conflicts = conflicts;
        _regions = regions;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;
        // Браузер шаблонов принадлежит РЕДАКТОРУ, а не оболочке (волна F2): с переездом шаблонов
        // внутрь бандла они перестали быть самостоятельной сущностью и стали свойством макроса —
        // таким же, как триггеры и переменные, и живущим там же, в инспекторе.
        Templates = new TemplatesViewModel(macros, _dispatcher);

        _macros.Changed += OnLibraryChanged;
        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;
        // Одна подписка на весь список триггеров, а не крючок в каждом из
        // AddTrigger / RemoveTrigger / LoadGraph / CloseEditor: это четыре места, каждое из
        // которых должно было бы помнить, а забытое оставляет ловушку, чьё состояние конфликта
        // не обновляется никогда.
        Triggers.CollectionChanged += OnTriggersCollectionChanged;

        // Библиотека — факт файловой системы, а не факт демона, поэтому список наполняется сразу и
        // безусловно: с упавшим (или ещё не поднятым) демоном он всё равно правда.
        ApplyLibrary(_macros.Entries);

        if (_client.IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Шаблоны машинного зрения ОТКРЫТОГО макроса — раздел инспектора, бывший режим «Шаблоны».
    /// Наводится на макрос из <see cref="LoadGraph"/> и <see cref="CloseEditor"/>, а «какие ноды
    /// его называют» пересчитывается по живому графу, ещё до сохранения.
    /// </summary>
    public TemplatesViewModel Templates { get; }

    // ---- библиотека (левая панель) ----------------------------------------------------

    /// <summary>Макросы библиотеки в том порядке, в каком их отдаёт демон (по имени).</summary>
    public ObservableCollection<MacroListItemViewModel> Macros { get; } = [];

    /// <summary>
    /// Выбранная запись библиотеки. Присвоение загружает этот граф в редактор, дописав перед этим
    /// тот, с которого уходим: «переключился» не имеет права значить «потерял последние секунды
    /// правки». Отброшены правки будут, только если дописать их было нельзя (недонабранные поля
    /// либо расхождение с диском) — тогда об этом говорит красная строка.
    /// </summary>
    public MacroListItemViewModel? SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (!_suppressSelectionReload && value is not null
                && !string.Equals(_selectedMacro?.Name, value.Name, StringComparison.Ordinal))
            {
                FlushAutoSave();
                // Запись выше могла пересобрать библиотеку, а с ней и все строки; берём живую с
                // тем же именем, иначе подсветка осталась бы на объекте, которого в дереве нет.
                value = Macros.FirstOrDefault(item => string.Equals(item.Name, value.Name, StringComparison.Ordinal))
                        ?? value;
            }

            if (!SetField(ref _selectedMacro, value) || _suppressSelectionReload)
            {
                return;
            }

            if (value is null)
            {
                return;
            }

            if (TryGet(value.Name) is { Graph: { } graph } entry)
            {
                var discarded = _hasOpenMacro && IsDirty() ? _loadedName ?? _macroName : null;
                LoadGraph(graph, entry.Submacros);
                ErrorMessage = discarded is null
                    ? null
                    : string.Format(CultureInfo.CurrentCulture,
                        Strings.Editor_Status_DiscardedChanges, discarded);
            }
            else if (value.Problem is { } problem)
            {
                // Нечитаемый бандл. Открывать нечего, но молчать нельзя: строка кликабельна ровно
                // затем, чтобы можно было спросить «а что с ним не так».
                ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                    Strings.Editor_Status_CannotOpen, value.Name, problem);
            }
        }
    }

    /// <summary>
    /// Библиотека в том виде, в каком её рисует панель: ДЕРЕВО, где заголовок группы — сам
    /// макрос, а вложенные строки — его под-макросы (волна F4). Выводится из <see cref="Macros"/>
    /// и <see cref="LibrarySearch"/>; довод против прежней группировки по префиксу имени записан
    /// в <see cref="MacroLibraryGroupViewModel"/>.
    /// </summary>
    public ObservableCollection<MacroLibraryGroupViewModel> MacroGroups { get; } = [];

    /// <summary>
    /// У демона в <c>macros/</c> нет НИ ОДНОГО макроса. Считается по <see cref="Macros"/>, а не
    /// по <see cref="MacroGroups"/>: поиск, не нашедший совпадений, — это другое состояние, и
    /// подсказка «где взять примеры» на нём была бы неправдой.
    ///
    /// Отдельное состояние понадобилось, когда посев примеров убрали из демона: свежая
    /// установка открывается с пустой библиотекой, и «Выберите макрос слева» на пустом списке
    /// читалось бы как «панель сломалась».
    /// </summary>
    public bool IsLibraryEmpty => Macros.Count == 0;

    /// <summary>Канва свободна, но выбирать есть из чего: «выберите макрос слева».</summary>
    public bool ShowPickMacroHint => !HasOpenMacro && !IsLibraryEmpty;

    /// <summary>
    /// Канва свободна, и выбирать не из чего: «библиотека пуста, вот где взять примеры».
    /// Именно ЭТИМ открывается свежая установка.
    ///
    /// Условие с <see cref="HasOpenMacro"/> с приходом автосохранения стало избыточным —
    /// создание макроса заводит файл сразу, так что «открыт макрос при пустой библиотеке» больше
    /// не бывает, — но оставлено: подсказка на весь экран поверх чьего-то графа стоит дороже
    /// лишнего условия.
    /// </summary>
    public bool ShowEmptyLibraryHint => !HasOpenMacro && IsLibraryEmpty;

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

    /// <summary>Абсолютный путь к папке макросов — то, что стоит за кнопкой «открыть папку».</summary>
    public string FolderPath => _macros.FolderPath;

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
        private set
        {
            if (SetField(ref _hasOpenMacro, value))
            {
                OnPropertyChanged(nameof(ShowPickMacroHint));
                OnPropertyChanged(nameof(ShowEmptyLibraryHint));
                // ⚠️ Найдено глазами на живой панели: раздел «Триггеры» и кнопка «+ Под-макрос»
                // после создания макроса оставались пустыми и погашенными. LoadCanvasGraph
                // поднимает ShowsTriggers, но ДО того, как здесь встанет true, — а больше это
                // свойство никто не поднимает. Ни сборка, ни тесты этого не видят: значение
                // считается верно, просто привязка о нём не узнаёт.
                OnPropertyChanged(nameof(ShowsTriggers));
                OnPropertyChanged(nameof(CanExtractSubmacro));
            }
        }
    }

    /// <summary>
    /// Правимое имя открытого графа. Сохранение под другим именем переименовывает макрос: демон
    /// ключуется по основе имени файла, поэтому переименование — это «записать новый файл,
    /// удалить старый», чем эта VM и занимается, ведь операции переименования в протоколе нет.
    ///
    /// <b>Набранное здесь имя автосохранение НЕ подхватывает.</b> Оно пишет в
    /// <c>_loadedName</c> — иначе каждое нажатие клавиши в этом поле заводило бы новый файл, а
    /// «pw-l», «pw-lo», «pw-log» лежали бы в папке вместе с «pw-login». Переименование поэтому
    /// осталось ручным жестом: Enter, уход фокуса или «Сохранить»
    /// (<see cref="CommitRenameAsync"/>), а до тех пор о нём напоминает
    /// <see cref="SaveStateText"/>.
    /// </summary>
    [AllowNull]
    public string MacroName
    {
        get => _macroName;
        set
        {
            if (!SetField(ref _macroName, value ?? string.Empty))
            {
                return;
            }

            // Набрали другое — прежний отказ забыт, спросить можно снова.
            _renameRefused = null;
            OnPropertyChanged(nameof(IsRenamePending));
            RefreshSaveState();
        }
    }

    /// <summary>
    /// В поле имени набрано не то, как называется файл: переименование ждёт подтверждения.
    /// Только для графа верхнего уровня — у функции правится <see cref="SubmacroName"/>, а он
    /// именем файла не является.
    /// </summary>
    public bool IsRenamePending =>
        HasOpenMacro
        && _openSubmacroId is null
        && _loadedName is { } loaded
        && !string.Equals(_macroName.Trim(), loaded, StringComparison.Ordinal);

    /// <summary>
    /// Подпись открытого ПОД-макроса. Пусто, когда на канве родитель.
    ///
    /// Отдельное поле от <see cref="MacroName"/>, а не переиспользование его: имя макроса — это
    /// имя файла, и позволить переписать его, пока правишь функцию, значило бы переименовать
    /// макрос жестом, который выглядит как переименование функции.
    /// </summary>
    [AllowNull]
    public string SubmacroName
    {
        get => _submacroName;
        set => SetField(ref _submacroName, value ?? string.Empty);
    }

    /// <summary>На канве под-макрос, а не сам макрос.</summary>
    public bool IsSubmacroOpen => _openSubmacroId is not null;

    /// <summary>
    /// Триггеры показываются только у графа верхнего уровня. У функции их не бывает по
    /// определению (<see cref="MacroSubmacro"/>), и предложить «добавить хоткей» здесь значило бы
    /// предложить ошибку валидации.
    /// </summary>
    public bool ShowsTriggers => HasOpenMacro && _openSubmacroId is null;

    /// <summary>Строки триггеров открытого графа. У под-макроса всегда пусто.</summary>
    public ObservableCollection<TriggerRowViewModel> Triggers { get; } = [];

    /// <summary>Строки нод открытого графа, в том порядке, в каком они сохранены в списке.</summary>
    public ObservableCollection<NodeRowViewModel> Nodes { get; } = [];

    /// <summary>
    /// Выбираемые цели рёбер: «конец прогона», а за ним все ноды графа. Один общий экземпляр, к
    /// которому привязан каждый выпадающий список ребра, — так новая нода или переименование
    /// появляются везде разом.
    /// </summary>
    public ObservableCollection<NodeChoiceViewModel> NodeChoices { get; } = [];

    /// <summary>Ноды для выбора стартовой. Тот же список без записи «конец» — стартовая нода обязательна.</summary>
    public ObservableCollection<NodeChoiceViewModel> StartNodeChoices { get; } = [];

    /// <summary>
    /// Под-макросы ЭТОГО бандла, которые предлагает выпадающий список ноды вызова (волна F4).
    ///
    /// До неё здесь были имена макросов ВСЕЙ библиотеки. Список сузился до одного файла — и это
    /// не ограничение интерфейса, а то, чем формат чинит свою главную беду: назвать можно только
    /// то, что уедет вместе с макросом получателю.
    /// </summary>
    public ObservableCollection<SubmacroChoiceViewModel> SubmacroChoices { get; } = [];

    /// <summary>С чего начинается исполнение. Должна указывать на одну из <see cref="Nodes"/>.</summary>
    public Guid StartNodeId
    {
        get => _startNodeId;
        private set
        {
            if (SetField(ref _startNodeId, value))
            {
                OnPropertyChanged(nameof(StartNode));
            }
        }
    }

    /// <summary>
    /// Стартовая нода в том виде, к какому привязан <c>ComboBox</c>. Экземпляры выбора живут
    /// дольше пересборок списка, поэтому «SelectedItem выпал из ItemsSource» здесь больше не
    /// случается; <c>null</c> всё же игнорируем — стереть стартовую ноду мимолётностью нельзя.
    /// </summary>
    public NodeChoiceViewModel? StartNode
    {
        get => StartNodeChoices.FirstOrDefault(choice => choice.Id == _startNodeId);
        set
        {
            if (value?.Id is { } id)
            {
                StartNodeId = id;
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

    /// <summary>
    /// «pw-загрузка ▸ опознать класс» — что именно сейчас на канве.
    ///
    /// Хлебные крошки, а не заголовок с одним именем: без родителя слева пользователь, глядя на
    /// граф функции, видел бы просто другой макрос. Стрелка — U+25B8, тот же символ, что у ▸
    /// «Запустить» в библиотеке, и без варианта представления эмодзи (ловушка U+25B6 из D1).
    /// </summary>
    public string OpenGraphPath => _openSubmacroId is null
        ? _macroName
        : $"{_macroName} ▸ {_submacroName}";

    /// <summary>Управляет двумя состояниями инспектора: нода либо сам макрос.</summary>
    public bool HasSelectedNode => _selectedNode is not null;

    /// <summary>Заголовок инспектора — тип выделенной ноды либо «Макрос».</summary>
    public string InspectorTitle => _selectedNode?.TypeLabel ?? Strings.Editor_Inspector_MacroTitle;

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
    public Guid? ExecutingNodeId
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
                node.IsExecuting = value is not null && node.Id == value;
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
        ? Strings.Editor_RunLog_EmptyRecorded
        : Strings.Editor_RunLog_EmptyUnsubscribed;

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
                parts.Add(Strings.Editor_RunLog_NoticePartial);
            }

            if (_droppedRunEvents > 0)
            {
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Editor_RunLog_NoticeDropped,
                    _droppedRunEvents));
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
        { IsPaused: true, CurrentNodeName: { } node } run => string.Format(
            CultureInfo.CurrentCulture, Strings.Editor_Debug_PausedAt, run.PauseReason, node),
        { PauseRequested: true } => Strings.Editor_Debug_PauseRequested,
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
        { IsFinished: true } run => run.FinalOutcome ?? Strings.Editor_Debug_StateFinished,
        { IsPaused: true } run => string.Format(
            CultureInfo.CurrentCulture, Strings.Editor_Debug_StatePaused, run.PauseReason),
        { PauseRequested: true } => Strings.Editor_Debug_StatePausing,
        _ => Strings.Editor_Debug_StateRunning,
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
        ? string.Format(CultureInfo.CurrentCulture, Strings.Editor_Debug_StopMany, n)
        : Strings.Editor_Debug_Stop;

    /// <summary>Проговаривает, что именно ■ отменит на самом деле.</summary>
    public string StopTooltip => LiveSiblingCount() is > 1 and var n
        ? PluralForms.Format(
            n,
            Strings.Editor_Debug_StopManyTip_One,
            Strings.Editor_Debug_StopManyTip_Few,
            Strings.Editor_Debug_StopManyTip_Many)
        : Strings.Editor_Debug_StopTip;

    /// <summary>Просит демон припарковать выбранный обход на его следующей ноде.</summary>
    public Task PauseAsync() => DebugAsync(DebugCommand.Pause);

    /// <summary>Отпускает выбранный обход.</summary>
    public Task ResumeAsync() => DebugAsync(DebugCommand.Resume);

    /// <summary>Отпускает выбранный обход и тут же паркует его на самой следующей ноде.</summary>
    public Task StepAsync() => DebugAsync(DebugCommand.Step);

    /// <summary>Гонит выбранный обход до ноды, выделенной на canvas.</summary>
    public Task RunToCursorAsync() => _selectedNode is { } node
        ? DebugAsync(DebugCommand.RunToNode, node.Id)
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

    private async Task DebugAsync(DebugCommand command, Guid? nodeId = null)
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
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Debug_CommandFailed, ex.Message);
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
            StatusMessage = Strings.Editor_Debug_WalkAlreadyFinished;
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
    public void RewireEdge(NodeEdgeViewModel edge, Guid? targetId)
    {
        ArgumentNullException.ThrowIfNull(edge);
        // До ноды не добраться из её же исхода иначе как бесконечным циклом, который canvas
        // нарисовал бы узлом; правила против этого у валидатора нет, поэтому редактор просто
        // отказывается создавать такое перетаскиванием.
        var owner = Nodes.FirstOrDefault(node => node.Edges.Contains(edge));
        if (owner is not null && targetId is not null && owner.Id == targetId)
        {
            return;
        }

        edge.TargetId = targetId;
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

    /// <summary>Те ноды открытого графа, на которых сейчас стоит точка останова, в порядке строк.</summary>
    public IReadOnlyList<Guid> BreakpointNodeIds =>
        [.. Nodes.Where(node => node.HasBreakpoint).Select(node => node.Id)];

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

        var writers = row is null ? [] : row.Info.Writes.Select(w => w.NodeId).ToHashSet();
        var readers = row is null ? [] : row.Info.Reads.Select(r => r.NodeId).ToHashSet();

        foreach (var node in Nodes)
        {
            node.IsVariableSource = writers.Contains(node.Id);
            node.IsVariableConsumer = readers.Contains(node.Id);
        }

        VariableLinks.Clear();
        if (row is null)
        {
            return;
        }

        var byId = Nodes.ToDictionary(node => node.Id);
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
    ///
    /// <b>Пока флаг поднят, автосохранение стоит.</b> Иначе оно затёрло бы чужую правку через
    /// несколько секунд, ничего не спросив, — то есть само стало бы той бедой, ради которой этот
    /// флаг и заведён. Сторону выбирает человек: «Перечитать» или «Сохранить».
    /// </summary>
    public bool ChangedOnDisk
    {
        get => _changedOnDisk;
        private set
        {
            if (SetField(ref _changedOnDisk, value))
            {
                RefreshSaveState();
            }
        }
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

    /// <summary>
    /// Постоянная подпись о состоянии записи: «сохранено», «правки ещё не записаны», «жду, пока
    /// поправят поля», «Enter — переименовать».
    ///
    /// <b>Отдельно от <see cref="StatusMessage"/>, и это не украшение.</b> Индикатор
    /// несохранённого при автосохранении почти всегда чист, и если бы каждая запись мигала
    /// словом «Сохранено» в общей строке статуса, она затирала бы «Макрос удалён», «Импортировано»
    /// и прочие однократные сообщения по нескольку раз в минуту. Здесь же СОСТОЯНИЕ: меняется
    /// дважды за приступ правки, а не на каждое нажатие.
    ///
    /// <c>null</c> — когда сказать нечего: макрос не открыт либо про запись уже говорит жёлтая
    /// строка <see cref="ChangedOnDisk"/>, и второй фразы рядом с ней не нужно.
    /// </summary>
    public string? SaveStateText
    {
        get => _saveStateText;
        private set => SetField(ref _saveStateText, value);
    }

    // ---- команды библиотеки -----------------------------------------------------------

    /// <summary>
    /// Пересевает у демона всё, чем владеет ОН: прогоны, окна, отказы регистрации хоткеев и точки
    /// останова. Библиотека сюда не входит — её панель читает с диска сама (F3).
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
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
                    _breakpoints[BreakpointKey(set.MacroName, set.SubmacroId)] = set.NodeIds;
                }

                RefreshRunState();
                RefreshHotkeyConflicts();
                ApplyBreakpointsToRows();
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось получить состояние демона");
        }
    }

    /// <summary>
    /// Заводит новый макрос: одна нода Delay, чтобы граф сразу был валиден, — и <b>сразу файл в
    /// папке</b>, под свободным именем.
    ///
    /// <b>Черновика больше нет, и это решение.</b> Прежде «+ Новый макрос» открывал граф, которого
    /// на диске не существовало, и всё, что с ним делали до первого «Сохранить», жило только в
    /// памяти панели. С автосохранением такое состояние стало бы единственным, которое оно не
    /// покрывает, — то есть ровно тем местом, где работа и терялась бы. Файл заводится первым
    /// действием, дальше правки дописываются сами.
    ///
    /// Имя берётся из <see cref="Strings.Editor_NewMacro_Name"/> и разводится
    /// <see cref="MacroLibrary.FreeName"/> до свободного: «новый-макрос-2», «новый-макрос-3». Оно
    /// же — имя файла, поэтому годится в имена NTFS по построению.
    /// </summary>
    public void NewMacro()
    {
        ErrorMessage = null;
        // Уходя с открытого макроса, дописываем его: создание нового не повод потерять последние
        // секунды правки прежнего.
        FlushAutoSave();

        // Подпись ноды выдаётся здесь, а не через NodeRowViewModel.Create: граф строится из
        // модели, а не из строк редактора, — зато выглядит она ровно так же, как у ноды,
        // добавленной кнопкой.
        var first = new DelayNode { Ms = 1000, DisplayName = "delay-1" };
        var name = _macros.FreeName(Strings.Editor_NewMacro_Name);
        var graph = new MacroGraph
        {
            Name = name,
            StartNodeId = first.Id,
            Nodes = [first],
        };

        try
        {
            // Foreign, а не Own: имя свободно по построению, и если кто-то занял его между
            // FreeName и записью, отказать правильнее, чем затереть чужой бандл.
            _macros.Save(graph, [], renamedFrom: null, MacroSaveTarget.Foreign);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Открывать нечего: граф из одной ноды не жалко, а редактор, показывающий макрос,
            // которого нет в папке, врал бы про главное обещание этого экрана.
            ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.Editor_Status_CreateFailed, ex.Message);
            Log.Warning(ex, "Не удалось создать макрос '{Macro}'", name);
            return;
        }

        ApplyLibrary(_macros.Entries);
        LoadGraph(TryGet(name)?.Graph ?? graph);
        SelectByName(name);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.Editor_Status_Created, name);
    }

    /// <summary>
    /// Удаляет файл макроса (и закрывает его, если он был открыт).
    ///
    /// Удаление возможно и у НЕЧИТАЕМОГО бандла — это, собственно, единственный способ убрать его
    /// из библиотеки, не открывая проводник.
    /// </summary>
    public bool DeleteMacro(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;

        try
        {
            // false = такого файла нет. Это ничегонеделание, а не ошибка: библиотеку могли
            // почистить снаружи между отрисовкой строки и нажатием.
            _macros.Delete(item.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Status_DeleteFailed, item.Name, ex.Message);
            Log.Warning(ex, "Удаление макроса '{Macro}' не выполнено", item.Name);
            return false;
        }

        if (string.Equals(_loadedName, item.Name, StringComparison.Ordinal))
        {
            CloseEditor();
        }

        ApplyLibrary(_macros.Entries);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.Editor_Status_Deleted, item.Name);
        return true;
    }

    /// <summary>
    /// Импорт: копирует чужой <c>.hsm</c> в <c>macros/</c> и открывает его.
    ///
    /// Это ВЕСЬ импорт целиком — процессы делят файловую систему, поэтому «положить макрос в
    /// библиотеку» и есть «положить файл в папку». Бандл копируется байт в байт: пропустив его
    /// через сегодняшнего писателя, мы бы потеряли всё, чего сегодняшняя версия формата не знает.
    ///
    /// <b>Занятое имя — вопрос к пользователю, тот же самый, что и при сохранении.</b> Раньше
    /// импорт молча приписывал суффикс: файл ложился «pw-login-2», сообщение об этом уезжало в
    /// строку статуса, и в библиотеке оказывались два похожих макроса, из которых работает не тот.
    /// Вариант «взять свободное имя» остался — он просто перестал выбираться за человека.
    /// </summary>
    /// <param name="sourcePath">Путь к импортируемому файлу.</param>
    /// <returns>Имя, под которым макрос лёг в библиотеку, либо <c>null</c> при отказе.</returns>
    public async Task<string?> ImportMacroAsync(string sourcePath)
    {
        ErrorMessage = null;
        // Импорт открывает импортированное, то есть уводит с открытого макроса, — дописываем его
        // до того, как вопрос о занятом имени встанет в диалог: пока диалог открыт,
        // автосохранение стоит.
        FlushAutoSave();

        // Затвор держится ВКЛЮЧАЯ время, пока открыт вопрос о занятом имени: модальный диалог
        // крутит свой цикл диспетчера, то есть часы автосохранения во время него тикают.
        _writeInFlight = true;
        try
        {
            string? targetName = null;
            var replace = false;
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            if (TryGet(stem) is { } occupant)
            {
                switch (await AskAboutTakenNameAsync(MacroNameConflictKind.Import, occupant).ConfigureAwait(true))
                {
                    case MacroNameConflictChoice.Replace:
                        replace = true;
                        break;

                    case MacroNameConflictChoice.FreeName:
                        targetName = _macros.FreeName(stem);
                        break;

                    default:
                        StatusMessage = string.Format(CultureInfo.CurrentCulture,
                            Strings.Editor_Status_ImportCancelled, stem);
                        return null;
                }
            }

            string name;
            try
            {
                name = _macros.Import(sourcePath, targetName, replace);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                    Strings.Editor_Status_ImportFailed, ex.Message);
                Log.Warning(ex, "Импорт макроса из {Path} не выполнен", sourcePath);
                return null;
            }

            ApplyLibrary(_macros.Entries);
            if (TryGet(name) is { Graph: { } graph } entry)
            {
                LoadGraph(graph, entry.Submacros);
                SelectByName(name);
            }

            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.Editor_Status_Imported, name);
            return name;
        }
        finally
        {
            _writeInFlight = false;
        }
    }

    /// <summary>
    /// Экспорт: путь к бандлу, который вид скопирует туда, куда укажет пользователь. Отдаём файл
    /// как есть — в нём уже лежит всё, что нужно получателю.
    /// </summary>
    public string? ExportPath(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _macros.PathOf(item.Name);
    }

    /// <summary>Запускает макрос без контекстного окна — ручной эквивалент его хоткея.</summary>
    public void Run(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;
        if (_launcher is null)
        {
            ErrorMessage = Strings.Editor_Status_RunUnavailable;
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
        var row = NodeRowViewModel.Create(kind, Nodes.Select(node => node.DisplayName));
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
            StartNodeId = row.Id;
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
            foreach (var edge in AllEdges())
            {
                if (edge.TargetId == row.Id)
                {
                    edge.TargetId = null;
                }
            }

            if (_startNodeId == row.Id)
            {
                _startNodeId = Nodes.Count > 0 ? Nodes[0].Id : Guid.Empty;
            }

            if (ReferenceEquals(SelectedNode, row))
            {
                SelectedNode = null;
            }

            RebuildChoices();
        }

        OnPropertyChanged(nameof(StartNodeId));
        OnPropertyChanged(nameof(StartNode));
        // На удалённой ноде могла стоять точка останова, а могла она быть единственным местом,
        // где переменную записывают.
        PushBreakpoints();
        RebuildVariables();
        SyncTemplateUsage();
    }

    /// <summary>
    /// Подсвечивает ноду, о которой говорит замечание, — при необходимости переключив канву на
    /// тот граф, где эта нода живёт.
    ///
    /// Переключение обязательно, а не любезность: замечание о ноде под-макроса, оставленное без
    /// перехода, искало бы её среди нод родителя и молча не подсвечивало ничего — то есть клик по
    /// строке выглядел бы сломанным.
    /// </summary>
    public void SelectIssue(ValidationIssueViewModel issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        if (issue.SubmacroId != _openSubmacroId)
        {
            if (issue.SubmacroId is { } id)
            {
                OpenSubmacro(id);
            }
            else
            {
                OpenParentGraph();
            }
        }

        if (issue.NodeId is not null)
        {
            SelectedNode = Nodes.FirstOrDefault(node => node.Id == issue.NodeId);
        }
    }

    // ---- под-макросы (волна F4) -----------------------------------------------------------

    /// <summary>Открытые сейчас под-макросы бандла, по подписи. Пусто у макроса без функций.</summary>
    public IReadOnlyList<MacroSubmacro> Submacros => BuildSubmacros();

    /// <summary>
    /// Переключает канву на под-макрос бандла. Правки текущего графа не теряются: они уезжают в
    /// модель бандла, откуда их возьмёт «Сохранить».
    /// </summary>
    /// <returns><c>false</c>, если такого под-макроса в бандле нет.</returns>
    public bool OpenSubmacro(Guid id)
    {
        if (!HasOpenMacro || _openSubmacroId == id)
        {
            return _openSubmacroId == id;
        }

        CommitCanvasGraph();
        if (_submacros.FirstOrDefault(submacro => submacro.Id == id) is not { } target)
        {
            return false;
        }

        _openSubmacroId = id;
        LoadCanvasGraph(target.Graph);
        return true;
    }

    /// <summary>Возвращает канву к самому макросу.</summary>
    public void OpenParentGraph()
    {
        if (!HasOpenMacro || _openSubmacroId is null)
        {
            return;
        }

        CommitCanvasGraph();
        var parent = _parkedParent ?? BuildParentGraph();
        _openSubmacroId = null;
        _parkedParent = null;
        LoadCanvasGraph(parent);
    }

    /// <summary>
    /// Заводит пустой под-макрос и открывает его.
    ///
    /// Одна нода <c>Delay</c> внутри — по тому же доводу, что и у нового макроса: граф без
    /// стартовой ноды не проходит валидацию, и встречать пользователя ошибкой сразу после
    /// нажатия кнопки незачем.
    /// </summary>
    /// <returns>Личность заведённой функции.</returns>
    public Guid AddSubmacro(string? name = null)
    {
        if (!HasOpenMacro)
        {
            return Guid.Empty;
        }

        CommitCanvasGraph();

        var first = new DelayNode { Ms = 500, DisplayName = "delay-1" };
        var submacro = new MacroSubmacro(
            Guid.NewGuid(),
            new MacroGraph
            {
                Name = string.IsNullOrWhiteSpace(name)
                    ? MacroExtraction.FreeName(_submacros.Select(existing => existing.Name))
                    : name.Trim(),
                Triggers = [],
                StartNodeId = first.Id,
                Nodes = [first],
            });

        _submacros.Add(submacro);
        _openSubmacroId = submacro.Id;
        LoadCanvasGraph(submacro.Graph);
        RebuildLibrary(_library);
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            Strings.Editor_Status_SubmacroCreated, submacro.Name);
        return submacro.Id;
    }

    /// <summary>
    /// Убирает под-макрос из бандла.
    ///
    /// <b>Отказ, пока на него ссылаются.</b> Удалить и оставить валидатору сказать «под-макроса
    /// нет» было бы дешевле в коде и дороже для пользователя: он получил бы макрос, который
    /// нельзя сохранить, и вторую задачу («найти ноды вызова») вместо ответа. Отказ называет
    /// ноды, и это ровно то, что надо сделать дальше.
    /// </summary>
    /// <returns><c>false</c>, если удалять нечего или удаление отклонено.</returns>
    public bool DeleteSubmacro(Guid id)
    {
        if (!HasOpenMacro || _submacros.All(submacro => submacro.Id != id))
        {
            return false;
        }

        CommitCanvasGraph();

        var callers = BuildParentGraph().Nodes
            .OfType<RunSubmacroNode>()
            .Where(node => node.SubmacroId == id)
            .Select(MacroNodeNames.Display)
            .ToList();
        if (callers.Count > 0)
        {
            ErrorMessage = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Editor_Status_SubmacroInUse,
                string.Join(", ", callers.Select(name => $"«{name}»")));
            return false;
        }

        var removed = _submacros.First(submacro => submacro.Id == id);
        _submacros.Remove(removed);
        _breakpoints.Remove(BreakpointKey(_loadedName, id));

        if (_openSubmacroId == id)
        {
            var parent = _parkedParent ?? BuildParentGraph();
            _openSubmacroId = null;
            _parkedParent = null;
            LoadCanvasGraph(parent);
        }
        else
        {
            RebuildSubmacroChoices();
            RebuildLibrary(_library);
        }

        ErrorMessage = null;
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            Strings.Editor_Status_SubmacroDeleted, removed.Name);
        return true;
    }

    // ---- выделение куска графа в под-макрос -----------------------------------------------

    /// <summary>Отмеченные для извлечения ноды, в порядке строк.</summary>
    public IReadOnlyList<NodeRowViewModel> MarkedNodes => [.. Nodes.Where(node => node.IsMarked)];

    /// <summary>Сколько нод отмечено — то, что печатает кнопка извлечения.</summary>
    public int MarkedCount => Nodes.Count(node => node.IsMarked);

    /// <summary>
    /// Извлекать есть что и есть откуда: отмечено хоть что-то, и мы не внутри функции (плоскость).
    /// </summary>
    public bool CanExtractSubmacro => HasOpenMacro && _openSubmacroId is null && MarkedCount > 0;

    /// <summary>«Выделить в под-макрос (3)» либо «Выделить в под-макрос».</summary>
    public string ExtractLabel => MarkedCount > 0
        ? string.Format(CultureInfo.CurrentCulture, Strings.Editor_Toolbar_ExtractCount, MarkedCount)
        : Strings.Editor_Toolbar_Extract;

    /// <summary>Ctrl+клик по коробке: добавить её в набор для извлечения или убрать из него.</summary>
    public void ToggleMark(NodeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.IsMarked = !row.IsMarked;
        RefreshMarkState();
    }

    /// <summary>Снимает все отметки (клик по пустому месту канвы, Esc, смена графа).</summary>
    public void ClearMarks()
    {
        foreach (var node in Nodes)
        {
            node.IsMarked = false;
        }

        RefreshMarkState();
    }

    /// <summary>
    /// Выносит отмеченные ноды в новый под-макрос и ставит на их место ноду вызова.
    ///
    /// Вся арифметика — в <see cref="MacroExtraction"/> (<c>Shared</c>), включая ОТКАЗ: там же
    /// записано, почему выделение с двумя входами или с расходящимися выходами отвергается, а не
    /// достраивается догадкой. Здесь остаётся применить результат к строкам редактора.
    /// </summary>
    /// <returns><c>false</c>, когда извлечение отклонено; причина уходит в <see cref="ErrorMessage"/>.</returns>
    public bool ExtractSubmacro(string? name = null)
    {
        ErrorMessage = null;
        if (!HasOpenMacro)
        {
            return false;
        }

        if (_openSubmacroId is not null)
        {
            ErrorMessage = Strings.Editor_Status_ExtractInsideSubmacro;
            return false;
        }

        var marked = Nodes.Where(node => node.IsMarked).Select(node => node.Id).ToList();
        var result = MacroExtraction.Extract(
            BuildGraph(),
            marked,
            string.IsNullOrWhiteSpace(name)
                ? MacroExtraction.FreeName(_submacros.Select(submacro => submacro.Name))
                : name.Trim());

        if (!result.IsOk)
        {
            ErrorMessage = result.Refusal;
            return false;
        }

        _submacros.Add(result.Submacro!);
        // Точки останова уехавших нод переезжают вместе с ними: id тот же, сменился только граф,
        // и потерять красную точку на ноде, которую пользователь только что рассматривал, было бы
        // неприятной мелочью с очевидной причиной.
        MoveBreakpoints(result.Submacro!);
        LoadCanvasGraph(result.Parent!);
        RebuildLibrary(_library);
        SelectedNode = Nodes.FirstOrDefault(node => node.Id == result.CallNodeId);
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            Strings.Editor_Status_Extracted, result.Submacro!.Name);
        return true;
    }

    // Отмеченные ноды уехали в функцию: их точки останова обязаны уехать туда же, иначе они
    // молча растворятся (набор родителя пересобирается из СТРОК, а строк этих больше нет).
    private void MoveBreakpoints(MacroSubmacro submacro)
    {
        if (_loadedName is null)
        {
            return;
        }

        var moved = submacro.Graph.Nodes
            .Select(node => node.Id)
            .Where(id => Nodes.FirstOrDefault(row => row.Id == id)?.HasBreakpoint == true)
            .ToList();
        if (moved.Count == 0)
        {
            return;
        }

        _breakpoints[BreakpointKey(_loadedName, submacro.Id)] = moved;
        _ = SendBreakpointsAsync(_loadedName, submacro.Id, moved);
    }

    private void RefreshMarkState()
    {
        OnPropertyChanged(nameof(MarkedCount));
        OnPropertyChanged(nameof(CanExtractSubmacro));
        OnPropertyChanged(nameof(ExtractLabel));
    }

    // ---- сохранение ---------------------------------------------------------------------

    /// <summary>
    /// Собирает граф, который сейчас НА КАНВЕ, из текущего состояния редактора. Снисходителен —
    /// на плохом вводе не бросает никогда.
    ///
    /// У под-макроса имя берётся из <see cref="SubmacroName"/>, а триггеры пусты: и то и другое —
    /// не поведение редактора, а свойство самого понятия (<see cref="MacroSubmacro"/>).
    /// </summary>
    public MacroGraph BuildGraph() => new()
    {
        Name = (_openSubmacroId is null ? _macroName : _submacroName).Trim(),
        Triggers = _openSubmacroId is null ? [.. Triggers.Select(row => row.ToTrigger())] : [],
        StartNodeId = _startNodeId,
        Nodes = [.. Nodes.Select(row => row.ToNode())],
    };

    /// <summary>Граф ВЕРХНЕГО УРОВНЯ бандла — с канвы, если открыт он, иначе отложенный.</summary>
    private MacroGraph BuildParentGraph() => _openSubmacroId is null ? BuildGraph() : _parkedParent!;

    /// <summary>
    /// Под-макросы бандла с подставленным вместо открытого тем, что сейчас на канве.
    ///
    /// Подмена, а не запись в <c>_submacros</c> на каждое нажатие клавиши: список — это модель, и
    /// перекладывать в неё содержимое канвы имеет смысл ровно в двух точках, где канва
    /// переключается или бандл записывается (<see cref="CommitCanvasGraph"/>).
    /// </summary>
    private List<MacroSubmacro> BuildSubmacros() =>
        _openSubmacroId is not { } open
            ? [.. _submacros]
            : [.. _submacros.Select(submacro => submacro.Id == open
                ? submacro with { Graph = BuildGraph() }
                : submacro)];

    /// <summary>
    /// Перекладывает то, что на канве, в модель бандла. Зовётся перед сменой открытого графа и
    /// перед записью — то есть везде, где содержимое канвы вот-вот перестанет быть на виду.
    /// </summary>
    private void CommitCanvasGraph()
    {
        if (_openSubmacroId is not { } open)
        {
            _parkedParent = BuildGraph();
            return;
        }

        var index = _submacros.FindIndex(submacro => submacro.Id == open);
        if (index >= 0)
        {
            _submacros[index] = _submacros[index] with { Graph = BuildGraph() };
        }
    }

    /// <summary>
    /// <c>true</c>, когда состояние редактора отличается от последнего загруженного или
    /// сохранённого. Незавершённое переименование сюда НЕ входит — оно не про содержимое, и
    /// держит его <see cref="IsRenamePending"/>; довод — у <see cref="SerializeForAutoSave"/>.
    /// </summary>
    public bool IsDirty() =>
        HasOpenMacro && !string.Equals(_loadedJson, SerializeForAutoSave(), StringComparison.Ordinal);

    // ---- вырезка шаблона со свежего снимка (issue #29) --------------------------------------

    /// <summary>
    /// Снимает окно, даёт выделить на нём область, кладёт вырезку шаблоном в бандл и заполняет
    /// область ноды.
    ///
    /// <b>Смысл — в точности, а не в удобстве.</b> Кадр идёт тем же путём, что и сопоставление:
    /// <c>PrintWindow</c> с <c>PW_CLIENTONLY | PW_RENDERFULLCONTENT</c> по разбуженному окну.
    /// Шаблон, вырезанный из «Win+Shift+S», отличается от того, что увидит зрение (цветокоррекция
    /// композитора, масштаб, курсор в кадре), и половина вопросов «почему не находит» родом
    /// оттуда.
    ///
    /// <b>Область заполняется ВСЕГДА и с запасом.</b> Почему не ровно по кромке вырезки — записано
    /// у <see cref="SearchRegion"/>: совпадающая с шаблоном пиксель в пиксель область превращает
    /// «найти» в «проверить, что оно ровно здесь». Все четыре поля остаются в инспекторе, так что
    /// ужать их до точных координат — одно движение.
    ///
    /// ⚠️ <b>Про гонку с автосохранением.</b> Её здесь нет, и держится это на двух вещах, а не на
    /// удаче. Первая: оба писателя работают в потоке UI (часы — <c>DispatcherTimer</c> в виде,
    /// этот метод — обработчик кнопки), а обе записи синхронны от чтения бандла до подмены файла,
    /// так что вклиниться между «прочитал» и «записал» второму просто негде — даже пока модальный
    /// диалог крутит свой цикл диспетчера. Вторая: правка шаблонов кладёт граф обратно ТЕМ ЖЕ,
    /// каким прочла, поэтому наш собственный <c>_diskJson</c> не устаревает, «изменён на диске» не
    /// загорается и следующая запись автосохранения спокойно наследует свежий шаблон из файла.
    /// </summary>
    /// <param name="row">Нода, из инспектора которой нажали. Не нода распознавания — ничего не делаем.</param>
    public async Task CaptureRegionAsync(NodeRowViewModel? row)
    {
        if (_regions is null || row is not ConditionalNodeRowViewModel node)
        {
            return;
        }

        if (_loadedName is not { } macroName)
        {
            ErrorMessage = Strings.Editor_Region_NoBundle;
            return;
        }

        var set = node.CaptureSet;
        if (node.CaptureKind == RegionCaptureKind.Tag && set is null)
        {
            // Класть некуда: MatchTemplateSet называет НАБОР, и без него у файла нет пути внутри
            // бандла. Отказ вслух, а не погашенная кнопка: пользователь нажал ровно ту кнопку,
            // которая ему нужна, и обязан узнать, чего не хватает.
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Region_NoTemplateSet, node.DisplayName);
            return;
        }

        RegionCaptureResult? result;
        try
        {
            result = await _regions.AskAsync(new RegionCaptureRequest(
                node.CaptureKind,
                macroName,
                node.DisplayName,
                set,
                node.CaptureName,
                Templates.NamesIn(set),
                Windows.Windows)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Region_Failed, ex.Message);
            return;
        }

        if (result is null)
        {
            return; // отменили — законный и частый исход
        }

        if (!Templates.Add(set, result.Name, result.Png))
        {
            ErrorMessage = Templates.ImportProblem ?? string.Format(
                CultureInfo.CurrentCulture, Strings.Editor_Region_Failed, result.Name);
            return;
        }

        node.ApplyCapturedName(result.Name);
        node.ApplyCapturedRegion(result.Region);

        // ⚠️ Дописываем немедленно, и найдено это глазами. Добавление ноды Find останавливает
        // автосохранение («сперва поправьте поля»: пустое имя шаблона — ошибка ВВОДА), а вырезка
        // это поле как раз заполняет. Без этой строки подпись ещё три с половиной секунды
        // требовала поправить поле, которое уже поправлено, и панель замечаний держала снятое
        // замечание. Порядок обязателен: до ApplyCapturedName запись отказала бы по той же самой
        // причине.
        //
        // Заодно это единственное место, где две записи бандла идут подряд (шаблон и граф), и
        // гонки между ними нет по построению: обе синхронны и обе в потоке UI, а правка шаблонов
        // кладёт граф обратно тем же, каким прочла.
        FlushAutoSave();

        // Показать только что вырезанное: строка выделяется, и в панели превью видно ровно те
        // байты, которые пойдут в сопоставление, — единственная возможность заметить промах
        // выделения до того, как макрос не найдёт ничего на живой игре.
        Templates.Selected = Templates.Templates.FirstOrDefault(template =>
            string.Equals(template.Set, set, StringComparison.OrdinalIgnoreCase)
            && string.Equals(template.Name, result.Name, StringComparison.Ordinal));

        ErrorMessage = null;
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            Strings.Editor_Region_Captured,
            MacroBundleFormat.TemplatePath(set, result.Name));
    }

    // ---- автосохранение ------------------------------------------------------------------

    /// <summary>
    /// Период часов автосохранения. Сами часы — <c>DispatcherTimer</c> в виде (тип Avalonia, а
    /// эта VM гоняется headless); отсюда вид берёт и период, чтобы «через сколько» было записано
    /// в одном месте, а не по половине на файл.
    /// </summary>
    public static readonly TimeSpan AutoSaveTick = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Сколько тиков подряд содержимое бандла должно не меняться, чтобы запись состоялась:
    /// <b>6 × 500 мс = 3 секунды затишья</b>. Правка происходит между тиками, поэтому тот тик, на
    /// котором её замечают, в отсчёт не входит, и от последнего нажатия клавиши до файла проходит
    /// от 3 до 3,5 секунд.
    ///
    /// <b>Почему по затиханию и почему именно три секунды.</b> Каждая запись будит демона: он
    /// перечитывает папку, ПЕРЕРЕГИСТРИРУЕТ ВСЕ ХОТКЕИ и целиком сбрасывает кэш шаблонов, — и
    /// делает это, возможно, посреди прогона по живым клиентам. По таймеру («каждые N секунд»)
    /// это были бы десятки пробуждений за сеанс правки; по затиханию приступ правки любой длины
    /// стоит РОВНО ОДНОГО.
    ///
    /// Три секунды выбраны с трёх сторон сразу: это на порядок больше 300 мс гашения дребезга у
    /// наблюдателя демона (значит две записи подряд — это два события, а не смазанный поток);
    /// это заведомо больше паузы внутри набираемого слова, так что имя или тег, набранные с
    /// обычной скоростью, дают одну запись, а не по одной на слог; и это больше двухсекундного
    /// окна, в котором обработчик <c>RunMacro</c> у демона считает бандл «только что записанным» и
    /// перечитывает папку, — то есть ▸ через паузу после правки не платит ничего лишнего, а ▸
    /// сразу после записи платит ровно там, где демон и правда отстал.
    ///
    /// Цена названа: при сбое панели теряется работа последних трёх секунд. Прежде терялось всё
    /// с момента последнего «Сохранить».
    /// </summary>
    public const int AutoSaveQuietTicks = 6;

    /// <summary>
    /// Тик часов автосохранения. Вызывается видом; ничего Avalonia-образного здесь нет
    /// намеренно — тест дёргает этот метод напрямую и не ждёт ни одной настоящей задержки.
    ///
    /// <b>Затишье определяется сравнением СОДЕРЖИМОГО, а не крючками в местах правки.</b> Мест
    /// этих десятки — строка ноды, ребро, триггер, имя, стартовая нода, перетаскивание коробки,
    /// правка функции, — и забытое означало бы правку, которая не сохранится никогда. Сравнение
    /// же смотрит ровно на то, на что смотрит признак «есть несохранённое», так что пропустить
    /// изменение оно не может по построению. Стоит это одной сериализации бандла на тик
    /// (десятки микросекунд) и только пока открыт макрос.
    /// </summary>
    public void TickAutoSave()
    {
        if (!HasOpenMacro || _writeInFlight)
        {
            return;
        }

        var current = SerializeForAutoSave();
        if (!string.Equals(current, _seenJson, StringComparison.Ordinal))
        {
            // Между тиками правили — часы затишья начинаются заново.
            _seenJson = current;
            _quietTicks = 0;
            SetDirtySeen(!string.Equals(current, _loadedJson, StringComparison.Ordinal));
            return;
        }

        if (string.Equals(current, _loadedJson, StringComparison.Ordinal))
        {
            _quietTicks = 0;
            SetDirtySeen(false);
            return;
        }

        SetDirtySeen(true);
        if (++_quietTicks < AutoSaveQuietTicks)
        {
            return;
        }

        _quietTicks = 0;
        AutoSave();
    }

    /// <summary>
    /// Записывает прямо сейчас, не дожидаясь затишья, — но по тем же правилам, что и оно (в своё
    /// имя, без вопросов, молча).
    ///
    /// Зовётся там, где открытый макрос вот-вот перестанет быть открытым: переключение на другой
    /// макрос, создание, импорт и закрытие окна панели. Без этого «переключился через секунду
    /// после правки» стоило бы правки, а именно от этого автосохранение и заводилось.
    /// </summary>
    public void FlushAutoSave()
    {
        if (!HasOpenMacro || _writeInFlight || !IsDirty())
        {
            return;
        }

        _quietTicks = 0;
        AutoSave();
    }

    /// <summary>
    /// Запись без единого вопроса: в загруженное имя, поверх своего же файла.
    ///
    /// Три вещи, которых здесь НЕТ и которым здесь не место:
    /// <list type="bullet">
    ///   <item><b>Переименования.</b> Пишем в <c>_loadedName</c>, даже если в поле имени набрано
    ///     другое; имя внутри графа подменяется тем же, чтобы читатель не встречал расхождение
    ///     «стем файла против поля Name» и не писал об этом в журнал на каждой загрузке.</item>
    ///   <item><b>Вопросов.</b> <see cref="IMacroNameConflictPrompt"/> отсюда недостижим по
    ///     построению: своё имя занять нельзя. Модальное окно раз в несколько секунд было бы
    ///     издевательством.</item>
    ///   <item><b>Отказа по ошибкам валидации.</b> Граф невалиден ровно тогда, когда над ним
    ///     работают. Замечания при этом обновляются — панель проблем становится живым
    ///     подсказчиком, — а не вооружает такой макрос демон.</item>
    /// </list>
    ///
    /// Стоит же автосохранение в двух случаях, и оба видны на экране: недонабранное поле (иначе в
    /// файл уехало бы не то, что на экране) и расхождение с диском (иначе чужая правка была бы
    /// затёрта молча).
    /// </summary>
    private void AutoSave()
    {
        if (_loadedName is not { } name)
        {
            return;
        }

        if (ChangedOnDisk)
        {
            // Про это уже говорит жёлтая строка панели инструментов, и выбор стороны — за
            // человеком: «Перечитать» либо «Сохранить».
            return;
        }

        CommitCanvasGraph();
        var submacros = BuildSubmacros();
        var graph = BuildParentGraph();
        var inputErrors = InputErrors();
        if (inputErrors.Count > 0)
        {
            // ⚠️ ЕДИНСТВЕННОЕ, ЧТО МЫ ВСЁ ЖЕ НЕ ПИШЕМ, и это не непоследовательность рядом с
            // «пишем даже с ошибками валидации». Ошибка валидации — про граф, КОТОРЫЙ ПОЛУЧИЛСЯ;
            // ошибка ввода — про граф, которого не получилось:
            //   * «две секунды» в поле задержки BuildGraph превращает в Ms = 0. Файл разошёлся бы
            //     с экраном МОЛЧА — без единого замечания, потому что получившийся граф
            //     безупречен;
            //   * незаполненное поле (шаблон, тег, клавиша) валидатор НЕ ловит вовсе, значит
            //     демон такой макрос вооружит и по клавише запустит недоделанную ноду.
            // Цена названа: пока поле не поправят, не записывается ВЕСЬ бандл. Поэтому причина и
            // едет сразу в два места — в подпись состояния и в панель замечаний.
            ShowIssues(
                inputErrors,
                MacroGraphValidator.ValidateBundle(graph, submacros, Templates.Inventory),
                submacros);
            _autoSaveBlocked = Strings.Editor_Toolbar_SaveStateInputErrors;
            RefreshSaveState();
            return;
        }

        _autoSaveBlocked = null;
        if (!string.Equals(graph.Name, name, StringComparison.Ordinal))
        {
            graph = WithName(graph, name);
        }

        ShowIssues([], MacroGraphValidator.ValidateBundle(graph, submacros, Templates.Inventory), submacros);
        Write(graph, submacros, renamedFrom: null, MacroSaveTarget.Own, name);
    }

    /// <summary>Собственные ошибки ввода строк — то, чего не видит никакой валидатор графа.</summary>
    private List<string> InputErrors() =>
    [
        .. Triggers.SelectMany(row => row.GetInputErrors())
            .Concat(Nodes.SelectMany(row => row.GetInputErrors())),
    ];

    /// <summary>Тот же граф под другим именем. <c>MacroGraph</c> — не record, <c>with</c> тут нет.</summary>
    private static MacroGraph WithName(MacroGraph graph, string name) => new()
    {
        Name = name,
        Triggers = graph.Triggers,
        StartNodeId = graph.StartNodeId,
        Nodes = graph.Nodes,
    };

    /// <summary>
    /// Общая для ручной и автоматической записи часть: сдвинуть опоры, записать, перечитать папку.
    /// </summary>
    /// <returns><c>false</c> — файловая система отказала; опоры возвращены, причина в <see cref="ErrorMessage"/>.</returns>
    private bool Write(
        MacroGraph graph,
        IReadOnlyList<MacroSubmacro> submacros,
        string? renamedFrom,
        MacroSaveTarget target,
        string name)
    {
        // Опорные значения сдвигаются ДО записи, и это не педантизм: библиотека поднимает своё
        // Changed изнутри Save, то есть ApplyLibrary отработает раньше, чем сюда вернётся
        // управление. Со старыми опорами он опознал бы наш собственный файл как чужую правку и
        // на мгновение зажёг «изменён на диске».
        var previousName = _loadedName;
        var previousLoadedJson = _loadedJson;
        var previousDiskJson = _diskJson;
        _loadedName = name;
        _loadedJson = SerializeForAutoSave();
        _diskJson = MacroGraphJson.Serialize(graph);
        _seenJson = _loadedJson;

        try
        {
            // renamedFrom едет вместе с графом: под НОВЫМ именем бандла ещё нет, и без подсказки
            // не от чего унаследовать шаблоны, под-макросы и паспорт переименованного макроса.
            // Под-макросы передаются СПИСКОМ, а не наследуются: редактор держит их все, и только
            // он знает, что среди них удалили.
            _macros.Save(graph, submacros, renamedFrom, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Сюда же приходят оба отказа записи, заведённые ради сохранности вложений:
            // «имя занято» (кто-то занял его между вопросом и записью) и «свой бандл не
            // читается». Оба — IOException с готовым объяснением, и пересказывать их своими
            // словами незачем: вердикт читателя точнее любого пересказа.
            _loadedName = previousName;
            _loadedJson = previousLoadedJson;
            _diskJson = previousDiskJson;
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Status_SaveFailed, ex.Message);
            Log.Warning(ex, "Запись макроса '{Macro}' не выполнена", name);
            return false;
        }

        if (renamedFrom is not null)
        {
            // Переименование: имя И ЕСТЬ основа имени файла, поэтому старый файл должен уйти.
            // Порядок важен — сперва записать, потом удалить, чтобы сбой между этими шагами
            // оставил две копии, а не ноль.
            try
            {
                _macros.Delete(renamedFrom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Переименование: старый файл '{Macro}' не удалён", renamedFrom);
            }
        }

        ApplyLibrary(_macros.Entries);
        // Бандл теперь есть (а при переименовании — под новым именем): браузер шаблонов обязан
        // перенацелиться, иначе кнопка «+ файл…» продолжит говорить «сохраните макрос».
        SyncTemplateUsage();
        ChangedOnDisk = false;
        _autoSaveBlocked = null;
        // Имя файла могло стать другим (переименование) — значит, «переименование ждёт» могло
        // погаснуть, а вычисляется оно из _loadedName, о котором привязке никто не сообщал.
        OnPropertyChanged(nameof(IsRenamePending));
        SetDirtySeen(false);
        return true;
    }

    // ---- ручное сохранение ---------------------------------------------------------------

    /// <summary>
    /// Проверяет открытый граф и записывает его в <c>macros/{имя}.hsm</c> — <b>под тем именем,
    /// которое набрано в поле</b>, то есть с переименованием, если оно там другое.
    ///
    /// <b>Зачем кнопка осталась, когда пишет автосохранение.</b> Ровно за тем, чего оно не делает
    /// и делать не должно: переименовать файл, выбрать свою сторону при расхождении с диском и
    /// записать НЕМЕДЛЕННО, не досиживая затишья.
    ///
    /// <b>Ошибка валидации записи больше НЕ мешает, и это разворот прежнего поведения.</b>
    /// «Сохранение отменено: исправьте ошибки» появилось, когда сохранение было ручным и редким;
    /// с автосохранением две кнопки вели бы себя по-разному, а граф невалиден ровно тогда, когда
    /// над ним работают. Предохранитель стоит на стороне исполнителя и стоял там до этой правки:
    /// демон валидирует бандл при загрузке и НЕ ВООРУЖАЕТ триггеры макроса с ошибкой
    /// (<c>MacroGraphStore.Armed</c>), а строка библиотеки несёт красный «!» и объясняет молчащую
    /// клавишу. Тот же валидатор с той же описью шаблонов гоняет панель — разойтись им негде,
    /// код один и живёт в <c>Shared</c>.
    ///
    /// Два заслона всё же остались, и оба про то, что записать НЕЧЕГО, а не про то, что записанное
    /// нехорошо: недонабранные поля строк (иначе в файл уехало бы не то, что на экране) и имя, не
    /// годящееся в имя файла (иначе «имя содержит /» дошло бы до пользователя невнятно упавшим
    /// вводом-выводом).
    ///
    /// Запись АТОМАРНА: целиком во временный файл рядом, затем <c>ReplaceFile</c>, — так что
    /// наблюдатель демона не поймает половину, а читающий прямо сейчас демон не порвётся. Механизм
    /// живёт в <c>Shared</c> рядом с писателем (<c>MacroBundleWriter</c>) и «упрощению» до
    /// <c>File.Move(overwrite: true)</c> не подлежит — это измерено, а не вычитано.
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

        // Затвор держится на всё время метода, включая ожидание ответа о занятом имени: модальный
        // диалог крутит свой цикл диспетчера, и часы автосохранения во время него тикают.
        _writeInFlight = true;
        try
        {
            return await SaveCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _writeInFlight = false;
        }
    }

    private async Task<bool> SaveCoreAsync(CancellationToken cancellationToken)
    {
        var inputErrors = InputErrors();
        foreach (var error in inputErrors)
        {
            AddIssue(new ValidationIssueViewModel(error, isError: true));
        }

        if (inputErrors.Count > 0)
        {
            ErrorMessage = Strings.Editor_Status_SaveBlocked;
            return false;
        }

        // Канва — это один граф бандла; перед записью её содержимое обязано оказаться в модели,
        // иначе правка функции, сделанная последней, не попала бы в файл.
        CommitCanvasGraph();
        var graph = BuildParentGraph();
        var submacros = BuildSubmacros();
        var name = graph.Name;

        // Имя проверяется отдельно от графа и ПЕРВЫМ: это правило NTFS, а не правило модели, и
        // живёт оно там же, где запись (MacroBundleFolder). Иначе «имя содержит /» дошло бы до
        // пользователя невнятно упавшим вводом-выводом.
        if (MacroBundleFolder.ValidateName(name) is { } nameError)
        {
            AddIssue(new ValidationIssueViewModel(nameError, isError: true));
            ErrorMessage = Strings.Editor_Status_SaveBlocked;
            return false;
        }

        // Судится БАНДЛ ЦЕЛИКОМ — тем же вызовом, каким его судит демон при загрузке (волна F4).
        // Опись шаблонов подаётся ТА ЖЕ, что увидит он: перечень бандла у панели уже есть, его
        // держит браузер шаблонов. Расхождение двух прогонов одного валидатора — ровно та ложь,
        // которой этот проект избегает у бейджа целей.
        //
        // ⚠️ Вердикт СМОТРЯТ, а не подчиняются ему: ошибка записи не мешает (см. примечание к
        // методу). Стоит она макросу вооружённых триггеров, о чём говорят и статус ниже, и
        // красный «!» на строке библиотеки.
        var issues = MacroGraphValidator.ValidateBundle(graph, submacros, Templates.Inventory).ToList();
        var errors = issues.Count(issue => issue.Severity == ValidationSeverity.Error);

        var previousName = _loadedName;

        // ЗАНЯТОЕ ИМЯ — ВОПРОС К ПОЛЬЗОВАТЕЛЮ, и задать его может только здесь.
        //
        // Личность макроса — это имя его файла, поэтому «pw-buff, переименованный в pw-login» и
        // «pw-login, сохранённый под своим именем» на уровне папки выглядят одинаково. Разница
        // известна ровно одному объекту — этому: он знает, какой макрос открыт (_loadedName).
        // Пока он молчал, переименование в занятое имя давало файл с графом одного макроса и
        // шаблонами другого, после чего исходный файл удалялся: два макроса становились одним,
        // и в статусе значилось «Сохранено».
        var target = MacroSaveTarget.Own;
        if (!string.Equals(previousName, name, StringComparison.Ordinal) && TryGet(name) is { } occupant)
        {
            switch (await AskAboutTakenNameAsync(MacroNameConflictKind.Save, occupant).ConfigureAwait(true))
            {
                case MacroNameConflictChoice.Replace:
                    target = MacroSaveTarget.ForeignReplace;
                    break;

                case MacroNameConflictChoice.FreeName:
                    // Имя меняем В РЕДАКТОРЕ, а не только на диске: пользователь согласился на
                    // «pw-login-2», и увидеть он должен именно его — иначе поле говорило бы одно,
                    // а библиотека другое. Граф правим копией, а не пересборкой с канвы: на
                    // канве может быть открыт под-макрос, и тогда родитель приезжает из
                    // _parkedParent, до которого новое значение поля не дошло бы.
                    name = _macros.FreeName(name);
                    MacroName = name;
                    graph = new MacroGraph
                    {
                        Name = name,
                        Triggers = graph.Triggers,
                        StartNodeId = graph.StartNodeId,
                        Nodes = graph.Nodes,
                    };
                    // Под свободным именем файла нет, так что чужого здесь уже не встретим, —
                    // а если кто-то успел его занять между вопросом и записью, отказ придёт из
                    // MacroBundleFolder, и это правильнее тихой перезаписи.
                    target = MacroSaveTarget.Foreign;
                    break;

                default:
                    StatusMessage = string.Format(CultureInfo.CurrentCulture,
                        Strings.Editor_Status_SaveNameTaken, name);
                    return false;
            }
        }

        var renamedFrom = previousName is null || string.Equals(previousName, name, StringComparison.Ordinal)
            ? null
            : previousName;

        if (!Write(graph, submacros, renamedFrom, target, name))
        {
            return false;
        }

        foreach (var issue in issues)
        {
            AddIssue(IssueRow(issue, submacros));
        }

        SelectByName(name);
        RefreshSaveState();
        StatusMessage = (errors, Issues.Count) switch
        {
            ( > 0, _) => string.Format(CultureInfo.CurrentCulture, Strings.Editor_Status_SavedWithErrors, errors),
            (0, > 0) => string.Format(CultureInfo.CurrentCulture, Strings.Editor_Status_SavedWithWarnings, Issues.Count),
            _ => Strings.Editor_Status_Saved,
        };
        return true;
    }

    /// <summary>
    /// Переименовывает файл открытого макроса в то, что набрано в поле имени.
    ///
    /// <b>Переименование осталось РУЧНЫМ жестом, и это следствие того, куда пишет
    /// автосохранение.</b> Оно пишет в загруженное имя; подхватывай оно набираемое, «pw-l»,
    /// «pw-lo» и «pw-log» легли бы в папку вместе с «pw-login». Значит, момент «имя набрано
    /// целиком» обязан назвать человек, и называет он его тремя равнозначными способами: Enter в
    /// поле, уход фокуса из него и кнопка «Сохранить». До тех пор о незавершённом переименовании
    /// говорит <see cref="SaveStateText"/> — молча теряться ему не с чего.
    /// </summary>
    /// <param name="force">
    /// <c>true</c> — Enter: пробуем даже то имя, на котором только что отказали. Уход фокуса
    /// (<c>false</c>) отказанное имя пропускает, иначе один и тот же вопрос о занятом имени
    /// вставал бы модальным окном на каждый щелчок мимо поля.
    /// </param>
    public async Task<bool> CommitRenameAsync(bool force = false)
    {
        if (!IsRenamePending)
        {
            return false;
        }

        var wanted = _macroName.Trim();
        if (!force && string.Equals(wanted, _renameRefused, StringComparison.Ordinal))
        {
            return false;
        }

        var saved = await SaveAsync().ConfigureAwait(true);
        _renameRefused = saved ? null : wanted;
        OnPropertyChanged(nameof(IsRenamePending));
        RefreshSaveState();
        return saved;
    }

    /// <summary>Esc в поле имени: вернуть то, как файл называется на самом деле.</summary>
    public void CancelRename()
    {
        if (_loadedName is { } name)
        {
            MacroName = name;
        }
    }

    /// <summary>
    /// Задаёт вопрос о занятом имени и отдаёт ответ человека.
    ///
    /// Цена замены называется числами — сколько шаблонов и под-макросов уйдёт вместе с
    /// существующим макросом, — и берутся они из снимка библиотеки, который у панели уже есть.
    /// У НЕЧИТАЕМОГО бандла (бандл будущей версии формата лежит в библиотеке намеренно) чисел
    /// нет, и вместо них едет вердикт читателя: «неизвестно даже, что внутри» — тоже ответ, и
    /// куда более честный, чем «0 шаблонов».
    ///
    /// Спрашивать некому — значит «Отмена»: молча заменить чужой макрос хуже, чем не сохранить.
    /// </summary>
    private async Task<MacroNameConflictChoice> AskAboutTakenNameAsync(
        MacroNameConflictKind kind,
        MacroBundleEntry occupant)
    {
        if (_conflicts is null)
        {
            Log.Warning("Имя «{Macro}» занято, а спросить не у кого — считаем отменой", occupant.Name);
            return MacroNameConflictChoice.Cancel;
        }

        var conflict = new MacroNameConflict(
            kind,
            occupant.Name,
            _macros.FreeName(occupant.Name),
            occupant.TemplatePaths.Count,
            occupant.Submacros.Count,
            occupant.IsReadable ? null : occupant.FaultMessage);

        return await _conflicts.AskAsync(conflict).ConfigureAwait(true);
    }

    /// <summary>Отбрасывает локальные правки и перечитывает открытый макрос из снимка библиотеки.</summary>
    public void ReloadFromDisk()
    {
        if (_loadedName is null || TryGet(_loadedName) is not { Graph: { } graph } entry)
        {
            return;
        }

        LoadGraph(graph, entry.Submacros);
        StatusMessage = Strings.Editor_Status_Reloaded;
    }

    /// <summary>
    /// Загружает БАНДЛ в правую панель: граф верхнего уровня на канву, его под-макросы — в
    /// модель. Точка входа для всего, что открывает макрос.
    /// </summary>
    /// <param name="graph">Граф верхнего уровня; его имя становится именем открытого макроса.</param>
    /// <param name="submacros">Под-макросы бандла; <c>null</c> — функций у него нет.</param>
    public void LoadGraph(MacroGraph graph, IReadOnlyList<MacroSubmacro>? submacros = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _submacros.Clear();
        _submacros.AddRange(submacros ?? []);
        _openSubmacroId = null;
        _parkedParent = null;
        _loadedName = graph.Name;
        MacroName = graph.Name;
        // Единственное место, откуда опоры «есть несохранённые правки» и «изменили снаружи»
        // берутся заново: они про БАНДЛ, а переключение канвы бандла не меняет.
        LoadCanvasGraph(graph, freshBundle: true);
    }

    /// <summary>
    /// Кладёт ОДИН граф бандла на канву. Не трогает ни <c>_loadedName</c>, ни модель бандла: этим
    /// же путём ходит переключение между макросом и его функциями.
    /// </summary>
    /// <param name="graph">Граф, который станет содержимым канвы.</param>
    /// <param name="freshBundle">
    /// <c>true</c> — это загрузка ДРУГОГО БАНДЛА, и опоры «есть несохранённые правки» и
    /// «изменили снаружи» надо взять заново. <c>false</c> — просто переключили канву внутри того
    /// же бандла (вошли в функцию, вернулись к родителю, завели функцию).
    ///
    /// ⚠️ <b>Разделение появилось не для красоты.</b> Опоры сдвигались ЗДЕСЬ и безусловно, то
    /// есть на каждом переключении графа. Из этого следовали две беды, обе тихие: правка
    /// родителя, после которой вошли в функцию, объявлялась сохранённой (<c>IsDirty</c>
    /// отвечал «нет», и закрытие редактора её теряло), а <c>_diskJson</c> начинал держать граф
    /// ФУНКЦИИ — тогда как <see cref="ApplyLibrary"/> сравнивает с ним граф ВЕРХНЕГО УРОВНЯ с
    /// диска. Любое событие наблюдателя при открытой функции читалось поэтому как чужая правка и
    /// на чистом редакторе перезагружало бандл целиком, выбрасывая функцию, которую в этот момент
    /// правили. С автосохранением, где записи часты, второе стреляло бы регулярно.
    /// </param>
    private void LoadCanvasGraph(MacroGraph graph, bool freshBundle = false)
    {
        foreach (var row in Nodes)
        {
            DetachNode(row);
        }

        Nodes.Clear();
        Triggers.Clear();
        ClearIssues();

        if (_openSubmacroId is null)
        {
            MacroName = graph.Name;
            _submacroName = string.Empty;
        }
        else
        {
            // Родителя сюда уже отложил CommitCanvasGraph у вызывающего — это и есть причина,
            // по которой переключение графа обязано идти через него, а не через прямой вызов
            // этого метода.
            _submacroName = graph.Name;
        }

        OnPropertyChanged(nameof(SubmacroName));
        OnPropertyChanged(nameof(IsSubmacroOpen));
        OnPropertyChanged(nameof(ShowsTriggers));
        OnPropertyChanged(nameof(OpenGraphPath));
        // ⚠️ Найдено глазами: после извлечения кнопка «Выделить в под-макрос (2)» оставалась на
        // панели инструментов со СТАРЫМ числом — отмеченных нод на канве уже не было, но об этом
        // никто не сообщал. Заодно она отъедала место у строки состояния, и та обрезалась
        // посередине слова.
        RefreshMarkState();

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
            // ⚠️ Найдено глазами: у ноды вызова был ПУСТОЙ выпадающий список, а коробка на канве
            // теряла подпись функции — при первом же открытии макроса из библиотеки. Ссылка была
            // цела; терялся список, который строкам раздаёт редактор. Собирался он только в
            // RebuildLibrary, а тот отрабатывает ДО загрузки бандла, когда под-макросов ещё нет.
            RebuildSubmacroChoices();

            // Графы, написанные до появления canvas, координат не несут. Раскладывая их ЗДЕСЬ,
            // а не при первой отрисовке, мы берём опорное значение ниже уже вместе с
            // положениями, — поэтому открытие старого макроса не читается как несохранённая
            // правка, но сохранение его по любому другому поводу раскладку записывает.
            MacroGraphLayout.EnsurePositions(Nodes, _startNodeId);
        }

        OnPropertyChanged(nameof(StartNodeId));
        OnPropertyChanged(nameof(StartNode));
        ResetView();

        if (freshBundle)
        {
            // Опора для «есть несохранённые правки» — собственный round trip редактора, а не
            // файл: загрузка нормализует пару-тройку форм, и эта нормализация правкой
            // пользователя не является.
            _loadedJson = SerializeForAutoSave();
            _diskJson = MacroGraphJson.Serialize(graph);
            ChangedOnDisk = false;
            ErrorMessage = null;
            StatusMessage = null;
            ResetAutoSaveClock();
        }
        else
        {
            RefreshSaveState();
        }

        SelectedNode = null;
        SyncCurrentFlags();

        // Сперва точки — чтобы к моменту, когда переключатель ниже зажжёт коробку, она уже была
        // помечена.
        ApplyBreakpointsToRows();
        RebuildVariables();
        SyncTemplateUsage();

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
        _macros.Changed -= OnLibraryChanged;
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

        Templates.Dispose();
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
        foreach (var entry in _library)
        {
            if (entry.Graph is not { } macro
                || (_loadedName is not null && string.Equals(entry.Name, _loadedName, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var trigger in macro.Triggers.OfType<HotkeyTrigger>())
            {
                if (HotkeyTriggerRowViewModel.ChordKey(trigger) is { } key)
                {
                    owners.TryAdd(key, entry.Name);
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
                row.Conflict = string.Format(CultureInfo.CurrentCulture,
                    Strings.Editor_Hotkey_ConflictOther, other);
            }
            else if (!seen.Add(key))
            {
                row.Conflict = Strings.Editor_Hotkey_ConflictSelf;
            }
            else
            {
                row.Conflict = refusedHere.Contains(key) ? Strings.Editor_Hotkey_ConflictSystem : null;
            }
        }

        RefreshLibraryHotkeyProblems();
    }

    /// <summary>
    /// Строки библиотеки несут ту же новость для макросов, которых никто не открывал, — на тот
    /// случай, когда хоткей умер при старте демона и сказать об этом на экране больше нечему.
    ///
    /// <b>С волны F3 причин две, и они разные.</b> Первая старая: Windows не отдала сочетание, и
    /// узнать это может только демон (<c>GetHotkeyFailures</c>). Вторая появилась вместе с
    /// инверсией авторства: демон не вооружает триггеры макроса, в графе которого валидатор нашёл
    /// ошибку, — иначе «оно в библиотеке ⇒ демон его принял» сменилось бы на «кто-то положил туда
    /// файл», а симптом остался бы прежним: клавиша нажимается, ничего не происходит. Панель
    /// выносит этот вердикт САМА, тем же валидатором из <c>Shared</c> и по тому же файлу, так что
    /// нового запроса не потребовалось ни одного.
    ///
    /// Порядок намеренный: ошибка в графе называется первой. Сочетание, отвергнутое Windows,
    /// чинится сменой аккорда, а сломанный граф — правкой макроса, и второе надо делать раньше.
    /// </summary>
    private void RefreshLibraryHotkeyProblems()
    {
        var byMacro = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var failure in _hotkeyFailures)
        {
            // Через HotkeyNames, а не интерполяцией: у флагового перечисления ToString() даёт
            // «Control, Shift+F1» — с запятой и словом, которого Windows не пишет. Ровно эта
            // подсказка объясняет пользователю, почему хоткей не работает, и печатать в ней
            // аккорд иначе, чем его печатает бейдж на той же строке, — значит объяснять поломку
            // записью, которой он нигде больше не видел.
            var chord = HotkeyNames.Chord(failure.Modifiers, failure.Key.ToString());
            byMacro[failure.MacroName] = string.Format(
                CultureInfo.CurrentCulture, Strings.Macros_Row_HotkeyRefused, chord);
        }

        foreach (var item in Macros)
        {
            item.HotkeyProblem = item switch
            {
                { HasHotkeyTrigger: true, ErrorCount: > 0 } =>
                    string.Format(CultureInfo.CurrentCulture,
                        Strings.Macros_Row_HotkeyUnarmed, item.ErrorCount),
                _ => byMacro.TryGetValue(item.Name, out var text) ? text : null,
            };
        }
    }

    // ---- внутренности --------------------------------------------------------------------

    private MacroBundleEntry? TryGet(string name) =>
        _library.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

    // Папка изменилась: своей записью, чужим редактором или файлом, положенным в неё проводником.
    // Наблюдатель стреляет с пула потоков, поэтому перекладываем в поток UI.
    private void OnLibraryChanged() => _dispatcher.Post(() => ApplyLibrary(_macros.Entries));

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
                // С волны F3 это событие НЕ про содержимое библиотеки: её мы читаем сами и своим
                // наблюдателем, обычно раньше. Означает оно ровно «демон перечитал папку и
                // перерегистрировал хоткеи» — то есть единственный момент, когда список отказов
                // Windows мог смениться. Это и есть то, за чем сюда идут.
                _ = RefreshHotkeyFailuresAsync();
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
            if (run is not null && run.Passed.TryGetValue(node.Id, out var passed))
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
    /// <summary>
    /// Наводит браузер шаблонов на открытый макрос и пересчитывает «какие ноды называют этот
    /// шаблон».
    ///
    /// Считается по ЖИВОМУ графу, а не по тому, что лежит на диске: имя шаблона, набранное в
    /// ноде, должно снять со строки пометку «не используется» немедленно, а не после сохранения.
    /// Зовётся из тех же мест, что <see cref="RebuildVariables"/>, и по той же причине — обе
    /// панели читают одну и ту же форму графа.
    /// </summary>
    /// <remarks>
    /// Наводится на <c>_loadedName</c>, а не на текущее имя в поле: браузер работает с ФАЙЛОМ, а у
    /// несохранённого черновика файла нет — и класть шаблон в бандл, которого не существует,
    /// некуда. Панель про это так и говорит, вместо того чтобы предлагать кнопку, которая
    /// откажет.
    /// </remarks>
    private void SyncTemplateUsage() =>
        // ВСЕ графы бандла, а не только тот, что на канве: папка шаблонов у бандла одна, и
        // шаблон, названный только из функции, обязан перестать быть «не используется».
        Templates.ShowMacro(
            HasOpenMacro ? _loadedName : null,
            HasOpenMacro ? [BuildParentGraph(), .. BuildSubmacros().Select(submacro => submacro.Graph)] : null);

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

    // Что держит у себя демон, по ГРАФАМ. Хранится, чтобы открытие графа возвращало его точки
    // без round trip и чтобы переименование не теряло наборы остальных макросов.
    //
    // Ключ — пара (макрос, под-макрос), ТРЕТЬЯ КООРДИНАТА из F4: точка, поставленная в функции,
    // не имеет права сняться, когда панель присылает точки её родителя, — а «заменить целиком»
    // без этой пары делало бы ровно это.
    private readonly Dictionary<(string Macro, Guid? Submacro), IReadOnlyList<Guid>> _breakpoints = [];

    private static (string Macro, Guid? Submacro) BreakpointKey(string? macroName, Guid? submacroId) =>
        (macroName ?? string.Empty, submacroId);

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
        var wanted = _loadedName is not null
                     && _breakpoints.TryGetValue(BreakpointKey(_loadedName, _openSubmacroId), out var ids)
            ? ids.ToHashSet()
            : [];

        _applyingBreakpoints = true;
        try
        {
            foreach (var node in Nodes)
            {
                node.HasBreakpoint = wanted.Contains(node.Id);
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
        _breakpoints[BreakpointKey(_loadedName, _openSubmacroId)] = ids;
        OnPropertyChanged(nameof(HasBreakpoints));
        _ = SendBreakpointsAsync(_loadedName, _openSubmacroId, ids);
    }

    private async Task SendBreakpointsAsync(string macroName, Guid? submacroId, IReadOnlyList<Guid> nodeIds)
    {
        try
        {
            await _client
                .RequestAsync(
                    IpcMessageTypes.SetBreakpoints,
                    new SetBreakpointsRequest(macroName, nodeIds, submacroId))
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

    private void ApplyLibrary(IReadOnlyList<MacroBundleEntry> entries)
    {
        SetLibrary(entries);

        if (!HasOpenMacro || _loadedName is null)
        {
            return; // ничего не открыто либо это несохранённый черновик, за файлом которого следить нечего
        }

        var entry = entries.FirstOrDefault(row => string.Equals(row.Name, _loadedName, StringComparison.Ordinal));
        if (entry is null)
        {
            ChangedOnDisk = true;
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Status_FileGone, _loadedName);
            return;
        }

        if (entry.Graph is not { } onDisk)
        {
            // Файл на месте, но перестал читаться — подменили снаружи на что-то испорченное.
            // Открытый граф не трогаем: он у нас цел, и сохранение его восстановит.
            ChangedOnDisk = true;
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Status_FileUnreadable, _loadedName, entry.FaultMessage);
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

        LoadGraph(onDisk, entry.Submacros);
        StatusMessage = Strings.Editor_Status_RefreshedFromDisk;
    }

    private void SetLibrary(IReadOnlyList<MacroBundleEntry> entries)
    {
        _library = entries;
        RebuildLibrary(entries);
        RefreshRunState();
        // Макрос, только что занявший (или отпустивший) сочетание, меняет то, с чем сталкивается
        // каждая открытая ловушка, а RebuildLibrary наделала новых объектов строк, которым нужны
        // их отметки.
        RefreshHotkeyConflicts();
    }

    private void RebuildLibrary(IReadOnlyList<MacroBundleEntry> entries)
    {
        // ОТКРЫТЫЙ макрос предпочтительнее подсвеченной сейчас строки: после сохранения нового
        // макроса список пересобирается из отложенного события, и опора на прежнее выделение
        // сняла бы подсветку ровно с того макроса, который пользователь правит.
        var previous = _loadedName ?? _selectedMacro?.Name;
        _suppressSelectionReload = true;
        try
        {
            Macros.Clear();
            _submacroRows.Clear();
            foreach (var entry in entries)
            {
                // Вердикт выносится ЗДЕСЬ и тем же вызовом, каким его выносит демон при загрузке:
                // только так строка может честно сказать «хоткей не вооружён, потому что в графе
                // ошибка» — без единого запроса и без второй копии правила.
                var issues = entry.Validate();
                Macros.Add(new MacroListItemViewModel(entry, entry.Graph is null ? null : issues));

                // Под-макросы ОТКРЫТОГО макроса берём из редактора, а не с диска: только что
                // выделенная функция обязана появиться в дереве до сохранения, иначе кнопка
                // выглядит несработавшей.
                var submacros = HasOpenMacro && string.Equals(entry.Name, _loadedName, StringComparison.Ordinal)
                    ? BuildSubmacros()
                    : entry.Submacros;
                _submacroRows[entry.Name] =
                [
                    .. submacros
                        .OrderBy(submacro => submacro.Name, StringComparer.CurrentCulture)
                        .Select(submacro => new SubmacroListItemViewModel(
                            entry.Name,
                            submacro,
                            issues.Count(issue => issue.SubmacroId == submacro.Id
                                                  && issue.Severity == ValidationSeverity.Error)))
                ];
            }

            // Черновика, которого в папке ещё нет, в дереве нет и подавно: дерево показывает
            // ПАПКУ. Его функции видны в инспекторе и в списке ноды вызова, а строкой библиотеки
            // станут вместе с файлом.
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
        RebuildSubmacroChoices();
    }

    /// <summary>
    /// Наполняет выпадающий список ноды вызова под-макросами ОТКРЫТОГО бандла (волна F4).
    ///
    /// До неё здесь были имена всей библиотеки. Теперь множество и мельче, и честнее: назвать
    /// можно ровно то, что уедет вместе с файлом.
    /// </summary>
    private void RebuildSubmacroChoices()
    {
        var choices = BuildSubmacros()
            .OrderBy(submacro => submacro.Name, StringComparer.CurrentCulture)
            .Select(submacro => Choice(submacro.Id, submacro.Name))
            .ToList();

        // Ссылку на несуществующий больше под-макрос оставляем ВЫБИРАЕМОЙ — тот же довод, что у
        // повисшего ребра: редактор обязан показывать правду, чтобы валидатор мог на неё
        // пожаловаться, а не обнулять ссылку молча.
        foreach (var row in Nodes.OfType<RunSubmacroNodeRowViewModel>())
        {
            if (row.SubmacroId != Guid.Empty && choices.All(choice => choice.Id != row.SubmacroId))
            {
                choices.Add(Missing(row.SubmacroId));
            }
        }

        Replace(SubmacroChoices, choices);
        foreach (var row in Nodes.OfType<RunSubmacroNodeRowViewModel>())
        {
            row.SubmacroChoices = SubmacroChoices;
        }

        SubmacroChoiceViewModel Choice(Guid id, string display)
        {
            if (!_submacroChoiceCache.TryGetValue(id, out var choice))
            {
                choice = SubmacroChoiceViewModel.For(id, display);
                _submacroChoiceCache[id] = choice;
            }

            choice.Display = display;
            return choice;
        }

        SubmacroChoiceViewModel Missing(Guid id)
        {
            if (!_submacroChoiceCache.TryGetValue(id, out var choice))
            {
                choice = SubmacroChoiceViewModel.Missing(id);
                _submacroChoiceCache[id] = choice;
            }

            return choice;
        }
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
        _submacros.Clear();
        _submacroRows.Clear();
        _openSubmacroId = null;
        _parkedParent = null;
        SubmacroName = string.Empty;
        OnPropertyChanged(nameof(IsSubmacroOpen));
        OnPropertyChanged(nameof(ShowsTriggers));
        OnPropertyChanged(nameof(OpenGraphPath));
        RefreshMarkState();
        _startNodeId = Guid.Empty;
        OnPropertyChanged(nameof(StartNodeId));
        OnPropertyChanged(nameof(StartNode));
        HasOpenMacro = false;
        ChangedOnDisk = false;
        SelectedNode = null;
        ResetAutoSaveClock();
        RebuildChoices();
        RebuildSubmacroChoices();
        SyncCurrentFlags();
        RebuildGroups();
        RebuildVariables();
        SyncTemplateUsage();
        // Граф не открыт ⇒ следовать не за чем. Сами обходы остаются отслеживаемыми, так что
        // повторное открытие макроса возвращает его лог.
        RebuildRuns();
    }

    private void AttachNode(NodeRowViewModel row)
    {
        row.PropertyChanged += OnNodeRowChanged;
        foreach (var edge in row.Edges)
        {
            edge.Choices = NodeChoices;
            // Перенацеливание исхода двигает линию на canvas — сделали ли это выпадающим
            // списком в инспекторе или перетаскиванием порта.
            edge.PropertyChanged += OnEdgeChanged;
        }

        if (row is RunSubmacroNodeRowViewModel call)
        {
            call.SubmacroChoices = SubmacroChoices;
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
            case nameof(NodeRowViewModel.DisplayName):
                // Переименование больше НЕ трогает рёбра — они ссылаются по Id, и вся прежняя
                // механика «перенацелить каждое входящее ребро» отсюда исчезла. Обновить нужно
                // ровно подписи: элемент выпадающего списка (он живёт дольше пересборок) и
                // карточки переменных, которые называют ноду по имени.
                RefreshChoiceLabels();
                RebuildVariables();
                SyncTemplateUsage();
                break;
            case nameof(NodeRowViewModel.Summary):
                // Набранный в пути к иконке {tag} добавляет читателя — панель обязана показать
                // его ещё до того, как макрос хоть раз запускали. То же и с именем шаблона:
                // набрал — и строка браузера перестала быть «не используется».
                RebuildVariables();
                SyncTemplateUsage();
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

        // Подсвечена ровно одна строка на всё дерево: либо макрос, либо одна из его функций, —
        // потому что на канве ровно один граф. Две подсветки читались бы как «открыто два».
        foreach (var rows in _submacroRows.Values)
        {
            foreach (var row in rows)
            {
                row.IsCurrent = _openSubmacroId == row.Id
                                && current is not null
                                && string.Equals(row.MacroName, current, StringComparison.Ordinal);
            }
        }

        foreach (var item in Macros)
        {
            item.IsCurrent = item.IsCurrent && _openSubmacroId is null;
        }
    }

    /// <summary>
    /// Пересобирает дерево библиотеки: макрос — заголовок группы, его под-макросы — вложенные
    /// строки (волна F4, вместо группировки по префиксу имени; довод — в
    /// <see cref="MacroLibraryGroupViewModel"/>).
    ///
    /// Поиск смотрит и на имя функции: макрос показывается, когда совпало его собственное имя
    /// ИЛИ имя любой его функции, — иначе набранное «опознать» не находило бы ничего, хотя
    /// функция с таким именем в библиотеке есть.
    /// </summary>
    private void RebuildGroups()
    {
        var needle = _librarySearch.Trim();
        MacroGroups.Clear();

        foreach (var macro in Macros)
        {
            var submacros = _submacroRows.GetValueOrDefault(macro.Name) ?? [];
            if (needle.Length > 0)
            {
                var hitsMacro = macro.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
                var matched = submacros
                    .Where(item => hitsMacro || item.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
                    .ToList();
                if (!hitsMacro && matched.Count == 0)
                {
                    continue;
                }

                MacroGroups.Add(new MacroLibraryGroupViewModel(macro, matched));
                continue;
            }

            MacroGroups.Add(new MacroLibraryGroupViewModel(macro, submacros));
        }

        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(ShowPickMacroHint));
        OnPropertyChanged(nameof(ShowEmptyLibraryHint));
    }

    private IEnumerable<NodeEdgeViewModel> AllEdges() => Nodes.SelectMany(node => node.Edges);

    // Один элемент выбора на ноду, переживающий пересборки списка. Кэш и есть то, что позволило
    // выбросить снимок-и-восстановление значений вокруг RebuildChoices: подмена элементов
    // выбивала SelectedItem из ItemsSource, а ComboBox отвечал на это null'ом.
    private readonly Dictionary<Guid, NodeChoiceViewModel> _choiceCache = [];

    // То же самое для под-макросов и по той же причине.
    private readonly Dictionary<Guid, SubmacroChoiceViewModel> _submacroChoiceCache = [];

    // Строки функций по имени макроса — из них дерево библиотеки собирает свои вложенные списки.
    private readonly Dictionary<string, List<SubmacroListItemViewModel>> _submacroRows =
        new(StringComparer.Ordinal);

    private void RebuildChoices()
    {
        var live = Nodes.Select(node => Choice(node.Id, node.DisplayName)).ToList();

        var edgeChoices = new List<NodeChoiceViewModel>(live.Count + 2) { NodeChoiceViewModel.End };
        edgeChoices.AddRange(live);
        // Правленный руками файл способен направить ребро (или старт) на несуществующую ноду.
        // Оставляем такое значение выбираемым, чтобы редактор показывал правду, а валидатор мог
        // на неё пожаловаться, — вместо того чтобы тихо переписать её в «конец прогона».
        foreach (var edge in AllEdges())
        {
            if (edge.TargetId is { } target && edgeChoices.All(choice => choice.Id != target))
            {
                edgeChoices.Add(Dangling(target));
            }
        }

        var startChoices = new List<NodeChoiceViewModel>(live);
        if (_startNodeId != Guid.Empty && startChoices.All(choice => choice.Id != _startNodeId))
        {
            startChoices.Add(Dangling(_startNodeId));
        }

        Replace(NodeChoices, edgeChoices);
        Replace(StartNodeChoices, startChoices);

        foreach (var edge in AllEdges())
        {
            edge.Resolve();
        }

        OnPropertyChanged(nameof(StartNodeId));
        OnPropertyChanged(nameof(StartNode));

        NodeChoiceViewModel Choice(Guid id, string display)
        {
            if (!_choiceCache.TryGetValue(id, out var choice))
            {
                choice = NodeChoiceViewModel.ForNode(id, display);
                _choiceCache[id] = choice;
            }

            choice.Display = display;
            return choice;
        }

        NodeChoiceViewModel Dangling(Guid id)
        {
            if (!_choiceCache.TryGetValue(id, out var choice))
            {
                choice = NodeChoiceViewModel.Dangling(id);
                _choiceCache[id] = choice;
            }

            return choice;
        }
    }

    // Переименование меняет ТОЛЬКО подпись элемента выбора — сам элемент остаётся тем же
    // объектом, поэтому ни один ComboBox не теряет выделения и ни одно ребро не трогается.
    private void RefreshChoiceLabels()
    {
        foreach (var node in Nodes)
        {
            if (_choiceCache.TryGetValue(node.Id, out var choice))
            {
                choice.Display = node.DisplayName;
            }
        }
    }

    /// <summary>
    /// Слепок ВСЕГО БАНДЛА одной строкой — опора для «есть ли несохранённые правки».
    ///
    /// Бандла, а не открытого графа: с волны F4 правка функции — это правка того же файла, и
    /// сравнение по одному графу считало бы её отсутствующей ровно до тех пор, пока пользователь
    /// не вернётся на родителя. Порядок под-макросов берётся из модели и потому устойчив.
    /// </summary>
    /// <param name="nameOverride">
    /// Чем подменить имя графа верхнего уровня, либо <c>null</c> — брать как есть. См.
    /// <see cref="SerializeForAutoSave"/>.
    /// </param>
    private string SerializeCurrent(string? nameOverride = null)
    {
        try
        {
            var parent = BuildParentGraph();
            if (nameOverride is { } name && !string.Equals(parent.Name, name, StringComparison.Ordinal))
            {
                parent = WithName(parent, name);
            }

            var parts = new List<string> { MacroGraphJson.Serialize(parent) };
            parts.AddRange(BuildSubmacros().Select(submacro =>
                submacro.Id.ToString("D") + MacroGraphJson.Serialize(submacro.Graph)));
            return string.Join("\0", parts);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Достижимо, только если строка выдала нечто несериализуемое; считаем это «отличным
            // от чего угодно», чтобы состояние читалось как «есть несохранённые правки», а не
            // как чистое.
            return Guid.NewGuid().ToString();
        }
    }

    /// <summary>
    /// Слепок бандла в том виде, в каком его ЗАПИШЕТ автосохранение: имя графа — ЗАГРУЖЕННОЕ, а
    /// не набранное в поле.
    ///
    /// ⚠️ Найдено на живой панели, ни сборка, ни тесты этого не видят. Набор нового имени
    /// попадает в обычный слепок, значит читается как правка, — и приступ переименования стоил
    /// лишней записи файла с полностью совпадающим содержимым, то есть лишнего пробуждения
    /// демона (перечитать папку, перерегистрировать все хоткеи, сбросить кэш шаблонов). Сравнивать
    /// надо ровно то, что уедет на диск: раз имя автосохранение не меняет, то и правкой оно для
    /// него не является. Переименование при этом не теряется — его держит
    /// <see cref="IsRenamePending"/> и подпись состояния.
    /// </summary>
    private string SerializeForAutoSave() => SerializeCurrent(_loadedName);

    // Замечание валидатора в строку панели — вместе с ПОДПИСЬЮ под-макроса, если оно про его
    // ноду. Валидатор носит только id (имена он не резолвит принципиально), а печатать guid
    // человеку незачем.
    private static ValidationIssueViewModel IssueRow(ValidationIssue issue, IReadOnlyList<MacroSubmacro> submacros) =>
        new(issue, issue.SubmacroId is { } id
            ? submacros.FirstOrDefault(submacro => submacro.Id == id)?.Name
            : null);

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
    /// Заново наполняет панель замечаний — тем же составом, каким её наполняет ручное сохранение.
    ///
    /// <b>Автосохранение обязано это делать, иначе оно стало бы тихим.</b> Пока запись отказывала
    /// на ошибке, список замечаний открывался нажатием «Сохранить», и нажатие было моментом, когда
    /// пользователь про ошибку узнавал. Теперь ошибка записи не мешает, так что единственное, что
    /// её показывает, — эта панель (плюс красный «!» на строке библиотеки). Пересборка происходит
    /// в затишье, то есть заведомо не под рукой у щёлкающего по замечаниям.
    /// </summary>
    private void ShowIssues(
        IReadOnlyList<string> inputErrors,
        IEnumerable<ValidationIssue> issues,
        IReadOnlyList<MacroSubmacro> submacros)
    {
        ClearIssues();
        foreach (var error in inputErrors)
        {
            AddIssue(new ValidationIssueViewModel(error, isError: true));
        }

        foreach (var issue in issues)
        {
            AddIssue(IssueRow(issue, submacros));
        }
    }

    // ---- подпись состояния записи ----------------------------------------------------------

    /// <summary>
    /// Забывает всё, что часы автосохранения успели насчитать: открыт другой граф, и затишье над
    /// прежним значения не имеет.
    /// </summary>
    private void ResetAutoSaveClock()
    {
        _seenJson = _loadedJson;
        _quietTicks = 0;
        _autoSaveBlocked = null;
        _renameRefused = null;
        OnPropertyChanged(nameof(IsRenamePending));
        SetDirtySeen(false);
    }

    private void SetDirtySeen(bool dirty)
    {
        _dirtySeen = dirty;
        RefreshSaveState();
    }

    /// <summary>
    /// Пересчитывает <see cref="SaveStateText"/> — состояние, а не событие.
    ///
    /// Порядок важен: незавершённое переименование важнее всего остального (это единственное, что
    /// сохранится ТОЛЬКО по прямому действию), «изменён на диске» уже сказано жёлтой строкой
    /// рядом, дальше — причина, по которой автосохранение стоит, и лишь потом обычная пара
    /// «не записано / записано».
    /// </summary>
    private void RefreshSaveState()
    {
        if (!HasOpenMacro)
        {
            SaveStateText = null;
            return;
        }

        if (IsRenamePending)
        {
            SaveStateText = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Toolbar_RenamePending, _macroName.Trim());
            return;
        }

        SaveStateText = ChangedOnDisk
            ? null
            : _autoSaveBlocked
              ?? (_dirtySeen ? Strings.Editor_Toolbar_SaveStatePending : Strings.Editor_Toolbar_SaveStateSaved);
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
    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
        where T : class
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(values[i]);
            }
            else if (!ReferenceEquals(target[i], values[i]))
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
