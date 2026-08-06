using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Что панель способна показывать. Порядок — тот же, что в боковой полосе.
///
/// <see cref="Settings"/> здесь шестой член, но НЕ шестой равноправный режим: в полосе он стоит
/// под разделителем, в самом низу, с шестерёнкой и без счётчика. Счётчики полосы отвечают на
/// вопрос «сколько сейчас есть», а у настроек такого числа нет. Место счётчика у этой строки
/// пустует намеренно: занимавшая его красная точка проваленной диагностики ушла вместе с самой
/// диагностикой, и выдумывать туда число не из чего.
/// </summary>
public enum ShellMode
{
    Windows,
    Macros,
    Runs,
    Log,
    Settings,
}

/// <summary>
/// Одна строка полосы режимов: имя и счётчик.
///
/// Счётчик — <see cref="int"/>? намеренно: выдуманное число хуже, чем никакого, поэтому режим,
/// за которым нет данных, рисуется вовсе без счётчика. Сейчас таких режимов нет,
/// но <c>null</c> остаётся законным значением и должен им остаться: следующий пустой режим
/// обязан выглядеть пустым, а не показывать ноль, которого никто не считал.
/// </summary>
public sealed class ShellModeViewModel : ObservableObject
{
    private int? _count;
    private bool _showsActivityDot;
    private bool _isSelected;

    internal ShellModeViewModel(ShellMode mode, string title)
    {
        Mode = mode;
        Title = title;
    }

    /// <summary>Какой режим выбирает эта строка.</summary>
    public ShellMode Mode { get; }

    /// <summary>Подпись в полосе (по-русски, как и везде в UI).</summary>
    public string Title { get; }

    /// <summary>Нарисованный счётчик либо <c>null</c>, когда честно показывать нечего.</summary>
    public string? CounterText => _count?.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>true</c>, когда у строки вообще есть счётчик, который стоит рисовать.</summary>
    public bool HasCounter => _count is not null;

    /// <summary>Маленькая акцентная точка рядом со счётчиком «Прогоны», пока что-то идёт.</summary>
    public bool ShowsActivityDot
    {
        get => _showsActivityDot;
        private set
        {
            if (SetField(ref _showsActivityDot, value))
            {
                OnPropertyChanged(nameof(CounterIsAccent));
            }
        }
    }

    /// <summary>Управляет акцентной линейкой 2px и 12%-заливкой (и то и другое делает тема <c>ListBoxItem</c>).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetField(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(CounterIsAccent));
            }
        }
    }

    /// <summary>
    /// Акцентный счётчик у выбранной строки и у всего живого; у остальных — приглушённый.
    /// Ровно то правило, что в макете: «Прогоны» держат акцентное число, пока прогоны есть, —
    /// даже когда открыт другой режим.
    /// </summary>
    public bool CounterIsAccent => _isSelected || _showsActivityDot;

    internal void SetCount(int? count, bool activityDot = false)
    {
        if (_count != count)
        {
            _count = count;
            OnPropertyChanged(nameof(CounterText));
            OnPropertyChanged(nameof(HasCounter));
        }

        ShowsActivityDot = activityDot;
    }
}

/// <summary>Один чип сводки по тегам в полосе: сам тег и сколько окон его несут.</summary>
public sealed class TagSummaryItemViewModel
{
    internal TagSummaryItemViewModel(string tag, int count)
    {
        Tag = tag;
        Count = count;
        CountText = count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Сам тег.</summary>
    public string Tag { get; }

    /// <summary>Сколько живых окон его несут.</summary>
    public int Count { get; }

    /// <summary>Нарисованное число — приглушённая цифра внутри чипа.</summary>
    public string CountText { get; }
}

/// <summary>
/// Оболочка 1b: одно окно, режимы в левой полосе, полоса прогонов, приколоченная снизу.
///
/// Ничем из того, что знает демон, она не владеет. Обе view-model режимов —
/// <see cref="Workspace"/> (окна и прогоны) и <see cref="Editor"/> (библиотека макросов) —
/// держат собственные подписки IPC ровно так же, как держали, когда были окном и диалогом;
/// этот класс лишь собирает их вместе, выводит счётчики полосы и сводку по тегам из того, что у
/// них уже есть, и решает, кто из них на экране.
///
/// <b>Выведено, а не запрошено.</b> Каждое число в полосе берётся из коллекции, которая и так
/// в памяти. Сводка по тегам в особенности пересчитывается по
/// <see cref="WorkspaceViewModel.WindowsChanged"/>, а он срабатывает на пуш
/// <c>WindowTagsChanged</c>, — так что повешенный тег обновляет состав мгновенно, без round
/// trip.
///
/// <b>Приостановка хоткеев и поток событий прогона привязаны к режиму «Макросы».</b>
/// См. <see cref="ApplyMacrosModeScope"/>.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private ShellModeViewModel? _selectedMode;
    private ShellMode _currentMode = ShellMode.Windows;
    private bool _hotkeysSuspended;
    private bool _runEventsSubscribed;

    public ShellViewModel(
        WorkspaceViewModel workspace,
        MacroEditorViewModel editor,
        LogViewModel log,
        SettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);

        Workspace = workspace;
        Editor = editor;
        Log = log;
        Settings = settings;

        // ⚠️ Строки «Шаблоны» здесь БОЛЬШЕ НЕТ (волна F2). Рейка — про сущности, а шаблон
        // перестал ею быть: общего дерева templates/ не существует, файл лежит внутри бандла
        // .hsm ровно одного макроса. Браузер сложился внутрь редактора, в инспектор макроса,
        // рядом с триггерами и переменными — то есть туда, где живут остальные свойства макроса
        // как целого. Заодно исчез счётчик, который отвечал на вопрос «сколько всего шаблонов»,
        // — теперь этого числа не существует, есть только «сколько их у ЭТОГО макроса».
        Modes =
        [
            new ShellModeViewModel(ShellMode.Windows, Strings.Shell_Mode_Windows),
            new ShellModeViewModel(ShellMode.Macros, Strings.Shell_Mode_Macros),
            new ShellModeViewModel(ShellMode.Runs, Strings.Shell_Mode_Runs),
            new ShellModeViewModel(ShellMode.Log, Strings.Shell_Mode_Log),
        ];

        // Настройки — отдельная строка, а не шестой элемент Modes: она рисуется под
        // разделителем, у неё нет счётчика, и выделяться две строки одновременно не должны.
        SettingsMode = new ShellModeViewModel(ShellMode.Settings, Strings.Shell_Mode_Settings);

        _selectedMode = Modes[0];
        _selectedMode.IsSelected = true;

        Workspace.WindowsChanged += OnWindowsChanged;
        Workspace.Runs.CollectionChanged += OnRunsChanged;
        Editor.Macros.CollectionChanged += OnMacrosChanged;
        Log.LogChanged += OnLogChanged;

        RefreshWindowState();
        RefreshRunState();
        RefreshMacroState();
        RefreshLogState();

        // Лента журнала включается сразу и на всю жизнь панели — в отличие от событий прогона,
        // которые живут ровно столько, сколько открыт режим «Макросы». Разница обоснована у
        // LogViewModel и сводится к одному: счётчик «сколько раз что-то пошло не так» обязан
        // быть виден из любого режима, а значит, лента должна идти и тогда, когда «Лог» не на
        // экране. Сигнал, который появляется, только если пойти и посмотреть, — не сигнал.
        _ = SafeAsync(Log.SetSubscriptionAsync(true), "log-subscribe");
    }

    /// <summary>Окна и прогоны — тело «Окон» и «Прогонов», а заодно полоса прогонов.</summary>
    public WorkspaceViewModel Workspace { get; }

    /// <summary>Библиотека макросов и редактор на canvas — тело «Макросов».</summary>
    public MacroEditorViewModel Editor { get; }

    /// <summary>Лента журнала демона — тело «Лога».</summary>
    public LogViewModel Log { get; }

    /// <summary>Настройки демона — тело «Настроек».</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Четыре рабочих строки боковой полосы, в порядке показа.</summary>
    public IReadOnlyList<ShellModeViewModel> Modes { get; }

    /// <summary>
    /// Пятая строка полосы — «Настройки», отделённая от четырёх рабочих.
    ///
    /// Держится отдельно от <see cref="Modes"/> ровно потому, что она не равноправна: своего
    /// счётчика у неё нет (счётчики отвечают «сколько сейчас есть», а у настроек такого числа
    /// нет), выделение у неё своё, и в списке она стоит под разделителем.
    /// </summary>
    public ShellModeViewModel SettingsMode { get; }


    /// <summary>Тег → сколько окон, самые многочисленные первыми. Единственное место, где весь состав виден сразу.</summary>
    public ObservableCollection<TagSummaryItemViewModel> TagSummary { get; } = [];

    /// <summary><c>true</c>, пока хоть одно окно несёт тег; иначе сводка показывает свою пустую строку.</summary>
    public bool HasTagSummary => TagSummary.Count > 0;

    /// <summary>
    /// Выбранная строка боковой полосы. Привязана двусторонне от <c>ListBox</c>.
    ///
    /// <c>null</c> здесь ЗНАЧАЩЕЕ: так выглядит полоса, когда на экране «Настройки». Выделение
    /// в <c>ListBox</c> рисует он сам, а не наш флаг, поэтому единственный способ снять с рейки
    /// подсветку — обнулить его выбор; иначе подсвеченными оказались бы две строки сразу.
    ///
    /// Раньше сеттер игнорировал <c>null</c>, защищаясь от того, что <c>ListBox</c> проталкивает
    /// его, пока перетряхивается <c>ItemsSource</c>. Защита снята сознательно: <see cref="Modes"/>
    /// строится один раз в конструкторе и не меняется никогда, так что перетряхивать нечего, — а
    /// цена ошибки, если это всё же случится, теперь косметическая (рейка без подсветки при
    /// нетронутом содержимом), а не «пустая рабочая область».
    /// </summary>
    [AllowNull]
    public ShellModeViewModel? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (ReferenceEquals(value, _selectedMode))
            {
                return;
            }

            if (_selectedMode is { } previous)
            {
                previous.IsSelected = false;
            }

            SetField(ref _selectedMode, value);

            if (value is null)
            {
                // Полосу обнулили — значит, показываем настройки; сам режим переключит
                // ShowSettings.
                return;
            }

            value.IsSelected = true;
            SettingsMode.IsSelected = false;
            SetCurrentMode(value.Mode);
        }
    }

    /// <summary>Какой режим на экране.</summary>
    public ShellMode CurrentMode => _currentMode;

    /// <summary>Переключает режим по личности, а не по строке, — так удобнее code-behind и тестам.</summary>
    public void SelectMode(ShellMode mode)
    {
        if (mode == ShellMode.Settings)
        {
            ShowSettings();
            return;
        }

        SelectedMode = Mode(mode);
    }

    /// <summary>
    /// Показывает «Настройки»: снимает выбор с рейки, подсвечивает свою строку и переключает
    /// рабочую область.
    /// </summary>
    public void ShowSettings()
    {
        if (_currentMode == ShellMode.Settings)
        {
            return;
        }

        SelectedMode = null;
        SettingsMode.IsSelected = true;
        SetCurrentMode(ShellMode.Settings);
    }

    public bool IsWindowsMode => CurrentMode == ShellMode.Windows;

    public bool IsMacrosMode => CurrentMode == ShellMode.Macros;

    public bool IsRunsMode => CurrentMode == ShellMode.Runs;

    public bool IsLogMode => CurrentMode == ShellMode.Log;

    public bool IsSettingsMode => CurrentMode == ShellMode.Settings;

    // ---- полоса прогонов ----------------------------------------------------------------

    /// <summary>
    /// Тот прогон, который называет полоса. Первый в списке, а не «самый свежий»: список обычно
    /// в одну запись, а устойчивый выбор не даёт полосе мигать между двумя одновременными
    /// прогонами.
    /// </summary>
    public RunningMacroRowViewModel? PrimaryRun =>
        Workspace.Runs.Count > 0 ? Workspace.Runs[0] : null;

    /// <summary><c>true</c>, пока хоть что-то идёт, — переключатель полосы между «живо» и «пусто».</summary>
    public bool HasRuns => Workspace.Runs.Count > 0;

    /// <summary>Что говорит полоса, когда ничего не идёт. Схлопываться она не умеет.</summary>
    public string IdleText => Strings.Shell_RunBar_Idle;

    /// <summary>«+2», когда прогонов в полёте больше, чем полоса способна назвать; иначе пусто.</summary>
    public string OtherRunsText => Workspace.Runs.Count > 1
        ? string.Create(CultureInfo.CurrentCulture, $"+{Workspace.Runs.Count - 1}")
        : string.Empty;

    /// <summary><c>true</c>, когда <see cref="OtherRunsText"/> есть что показать.</summary>
    public bool HasOtherRuns => Workspace.Runs.Count > 1;

    // ---- хоткеи ---------------------------------------------------------------------------

    /// <summary><c>true</c>, пока глобальные хоткеи демона выключены по нашей просьбе.</summary>
    public bool HotkeysSuspended => _hotkeysSuspended;

    /// <summary>
    /// Возвращает хоткеи демона на место, если эта оболочка их приостанавливала. Вызывается на
    /// выходе из «Макросов» и ещё раз при закрытии окна: демон НЕ перерегистрирует их сам,
    /// когда клиент отваливается, так что пропущенное возобновление оставляет каждый
    /// глобальный хоткей мёртвым до перезапуска демона.
    /// </summary>
    public async Task ResumeHotkeysIfSuspendedAsync()
    {
        if (!_hotkeysSuspended)
        {
            return;
        }

        _hotkeysSuspended = false;
        OnPropertyChanged(nameof(HotkeysSuspended));
        await SafeAsync(Editor.ResumeHotkeysAsync(), "resume").ConfigureAwait(false);
    }

    /// <summary>Подгоняет полосу прогонов и список «Прогоны». Её тикает односекундный таймер окна.</summary>
    public void RefreshElapsed() => Workspace.RefreshElapsed();

    public void Dispose()
    {
        Workspace.WindowsChanged -= OnWindowsChanged;
        Workspace.Runs.CollectionChanged -= OnRunsChanged;
        Editor.Macros.CollectionChanged -= OnMacrosChanged;
        Log.LogChanged -= OnLogChanged;
        Workspace.Dispose();
        // Браузер шаблонов освобождает редактор: он его и создал.
        Editor.Dispose();
        Settings.Dispose();
        // Отписываться от ленты у демона отдельным запросом не нужно и негде: этот путь ведёт к
        // закрытию панели, а разрыв трубы сервер разбирает сам — соединение уходит вместе со
        // своей подпиской. (Хоткеи — исключение ровно потому, что их демон обратно НЕ
        // регистрирует.)
        Log.Dispose();
    }

    // ---- внутренности -----------------------------------------------------------------------

    /// <summary>
    /// Единственное место, где меняется видимый режим: и рейка, и строка настроек проходят через
    /// него, поэтому побочные эффекты входа в режим описаны один раз, а не по разу на каждый
    /// способ туда попасть.
    /// </summary>
    private void SetCurrentMode(ShellMode mode)
    {
        if (_currentMode == mode)
        {
            return;
        }

        _currentMode = mode;
        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(IsWindowsMode));
        OnPropertyChanged(nameof(IsMacrosMode));
        OnPropertyChanged(nameof(IsRunsMode));
        OnPropertyChanged(nameof(IsLogMode));
        OnPropertyChanged(nameof(IsSettingsMode));

        ApplyMacrosModeScope();
        RefreshSettingsOnEntry();
    }

    /// <summary>
    /// Перечитывает настройки на входе в режим.
    ///
    /// Снимок и без того живой — демон толкает <c>SettingsChanged</c>, — так что запрос здесь
    /// страховочный: он закрывает окно между стартом панели и первым пушем.
    /// </summary>
    private void RefreshSettingsOnEntry()
    {
        if (CurrentMode != ShellMode.Settings)
        {
            return;
        }

        _ = SafeAsync(Settings.RefreshAsync(), "settings");
    }

    /// <summary>
    /// В скобки «„Макросы“ на экране» взяты две вещи: глобальные хоткеи ложатся, а поток
    /// событий прогона поднимается.
    ///
    /// <b>Хоткеи.</b> Win32 <c>RegisterHotKey</c> проглатывает нажатия сочетания, которым уже
    /// владеет, поэтому сочетание, привязанное сейчас к макросу, до ловушки не дошло бы
    /// никогда — а это ровно то сочетание, которое пользователь скорее всего и перебивает. До
    /// D2 скобками служило время жизни диалога; когда редактор стал режимом, ближайший
    /// эквивалент — активация режима. Привязывать это к «ловушка взведена» намеренно НЕ стали:
    /// взведение происходит по клику, а приостановка — это round trip, так что самое первое
    /// нажатие всё равно могло бы обогнать демон.
    ///
    /// <b>События прогона (D3b).</b> Те же скобки по другой причине: рисует их только canvas,
    /// поток — единственное частое сообщение в протоколе, а пока никто не подписан, демон не
    /// производит ничего. Держать его включённым всю жизнь панели значило бы, что движок
    /// форматирует строку лога на каждую ноду каждого макроса, пока пользователь смотрит на
    /// список окон.
    /// </summary>
    private void ApplyMacrosModeScope()
    {
        var inMacros = CurrentMode == ShellMode.Macros;

        // Две независимые защёлки, намеренно не одна: хоткейную досрочно отпускает
        // ResumeHotkeysIfSuspendedAsync на выходе из процесса, и общий флаг привёл бы к тому,
        // что это освобождение проглотило бы отписку при более поздней смене режима.
        if (inMacros != _runEventsSubscribed)
        {
            _runEventsSubscribed = inMacros;
            _ = SafeAsync(Editor.SetRunEventSubscriptionAsync(inMacros), "run-events");
        }

        if (inMacros == _hotkeysSuspended)
        {
            return;
        }

        _hotkeysSuspended = inMacros;
        OnPropertyChanged(nameof(HotkeysSuspended));
        _ = inMacros
            ? SafeAsync(Editor.SuspendHotkeysAsync(), "suspend")
            : SafeAsync(Editor.ResumeHotkeysAsync(), "resume");
    }

    private static async Task SafeAsync(Task task, string what)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Полное имя намеренно: у оболочки есть собственное свойство Log — режим «Лог», — и
            // простое имя разрешалось бы в него.
            Serilog.Log.Warning(ex, "Фоновая операция режима '{What}' не выполнена", what);
        }
    }

    private void OnWindowsChanged() => RefreshWindowState();

    private void OnRunsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshRunState();

    private void OnMacrosChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshMacroState();

    private void OnLogChanged() => RefreshLogState();

    /// <summary>
    /// Счётчик «Лога» — это ПРОБЛЕМЫ (предупреждения и хуже) в ленте, а не число записей в ней.
    ///
    /// Единственный счётчик рейки, который считает не все свои строки, и отступление намеренное.
    /// Число записей упирается в потолок ленты и после этого стоит на месте — константа, а
    /// значит, не сообщение. А вот «три раза что-то пошло не так» — ровно то, ради чего в этот
    /// режим и заходят, и остаётся при этом счётом СТРОК: тех самых, что видны при фильтре
    /// «предупреждения и выше».
    ///
    /// Точка рядом с числом — акцент режима «Прогоны», означающий «прямо сейчас что-то идёт», и
    /// перегружать её вторым смыслом «а тут есть ошибки» нельзя: два разных факта одним пятном
    /// делают бесполезными оба.
    /// </summary>
    private void RefreshLogState() => Mode(ShellMode.Log).SetCount(Log.ProblemCount);

    private void RefreshWindowState()
    {
        Mode(ShellMode.Windows).SetCount(Workspace.Windows.Count);
        RebuildTagSummary();
    }

    private void RefreshRunState()
    {
        var count = Workspace.Runs.Count;
        Mode(ShellMode.Runs).SetCount(count, activityDot: count > 0);
        OnPropertyChanged(nameof(PrimaryRun));
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(OtherRunsText));
        OnPropertyChanged(nameof(HasOtherRuns));
    }

    private void RefreshMacroState() => Mode(ShellMode.Macros).SetCount(Editor.Macros.Count);

    // Самый многочисленный тег первым, дальше по алфавиту — устойчивый порядок, поднимающий
    // наверх полосы реальный состав пати.
    private void RebuildTagSummary()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var window in Workspace.Windows)
        {
            foreach (var chip in window.Tags)
            {
                counts[chip.Text] = counts.TryGetValue(chip.Text, out var n) ? n + 1 : 1;
            }
        }

        var ordered = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.CurrentCulture)
            .Select(pair => new TagSummaryItemViewModel(pair.Key, pair.Value))
            .ToList();

        TagSummary.Clear();
        foreach (var item in ordered)
        {
            TagSummary.Add(item);
        }

        OnPropertyChanged(nameof(HasTagSummary));
    }

    private ShellModeViewModel Mode(ShellMode mode) => Modes.First(row => row.Mode == mode);
}
