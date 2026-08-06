using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels;

/// <summary>Одна строка ленты журнала.</summary>
public sealed class LogRowViewModel : ObservableObject
{
    private bool _isExpanded;

    internal LogRowViewModel(LogEntryDto entry)
    {
        Seq = entry.Seq;
        Level = entry.Level;
        Message = entry.Message;
        Exception = entry.Exception;
        SourceFull = entry.Source;

        TimeText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        LevelText = Abbreviate(entry.Level);
        SourceText = ShortSource(entry.Source);

        // Стог сена для поиска считается один раз при создании строки. Поиск идёт по каждому
        // нажатию клавиши и по всей ленте (до пяти тысяч строк), так что ToLowerInvariant на
        // каждый символ запроса стоил бы заметно больше, чем эта строка в памяти.
        _haystack = string.Concat(entry.Message, "", entry.Source, "", entry.Exception)
            .ToLowerInvariant();
    }

    private readonly string _haystack;

    /// <summary>Сквозной номер записи у демона — по нему панель отбрасывает повтор на подписке.</summary>
    public long Seq { get; }

    /// <summary>Уровень. Им же красится трёхбуквенный жетон и линейка проблемной строки.</summary>
    public LogLevelDto Level { get; }

    /// <summary>«14:22:09.318», местное время.</summary>
    public string TimeText { get; }

    /// <summary>
    /// «INF», «WRN», «DBG» — латиницей, хотя весь интерфейс русский, и это осознанно: ровно так
    /// же (<c>{Level:u3}</c>) уровень выглядит в <c>logs/smartmacro-*.log</c>, и человек, который
    /// увидел строку в панели, а потом пошёл искать её в файле, ищет то же самое слово.
    /// «[ПРД]» вместо «[WRN]» стоило бы ему grep.
    /// </summary>
    public string LevelText { get; }

    /// <summary>Короткое имя типа-источника: <c>WindowRegistry</c> вместо <c>SmartMacro.Windows.WindowRegistry</c>.</summary>
    public string SourceText { get; }

    /// <summary>Полный <c>SourceContext</c> — уезжает в подсказку.</summary>
    public string? SourceFull { get; }

    /// <summary>Отрисованное сообщение.</summary>
    public string Message { get; }

    /// <summary>Исключение со стеком либо <c>null</c>.</summary>
    public string? Exception { get; }

    /// <summary><c>true</c>, когда у строки есть стек и ей полагается раскрывашка.</summary>
    public bool HasException => Exception is not null;

    /// <summary>
    /// Развёрнут ли стек. Свёрнут по умолчанию: стек в двадцать строк, вклеенный в ленту,
    /// вытесняет с экрана те записи, среди которых его и ищут.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    /// <summary><c>true</c> у предупреждений и хуже — то, что считает счётчик рейки.</summary>
    public bool IsProblem => Level >= LogLevelDto.Warning;

    /// <summary>Подходит ли строка под фильтр уровня и подстроку поиска.</summary>
    internal bool Matches(LogLevelDto minimum, string? search) =>
        Level >= minimum
        && (string.IsNullOrEmpty(search) || _haystack.Contains(search, StringComparison.Ordinal));

    private static string Abbreviate(LogLevelDto level) => level switch
    {
        LogLevelDto.Verbose => "VRB",
        LogLevelDto.Debug => "DBG",
        LogLevelDto.Information => "INF",
        LogLevelDto.Warning => "WRN",
        LogLevelDto.Error => "ERR",
        LogLevelDto.Fatal => "FTL",
        // Уровень, которого эта панель не знает, а её демон знает. Показываем как есть, а не
        // прячем строку: правило то же, что у незнакомого исхода прогона, — терять возможность,
        // а не лог.
        _ => "???",
    };

    // Пространство имён в колонке — это шестьдесят символов «SmartMacro.», одинаковых во всех
    // строках. Полное имя остаётся в подсказке.
    private static string ShortSource(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var dot = source.LastIndexOf('.');
        return dot < 0 || dot == source.Length - 1 ? source : source[(dot + 1)..];
    }
}

/// <summary>Пункт фильтра «не ниже такого-то уровня».</summary>
/// <param name="Level">Минимальный уровень, который проходит фильтр.</param>
/// <param name="Title">Подпись в выпадающем списке.</param>
public sealed record LogLevelOption(LogLevelDto Level, string Title);

/// <summary>
/// Режим «Лог»: журнал демона, выведенный в трубу.
///
/// <b>Вторая подписка на русле волны D3b, а не второй механизм.</b> Всё то же самое: поток идёт
/// только по явной просьбе (<c>SubscribeLog</c>), едет склеенными пачками и честно считает
/// потери. Отличий от событий прогона ровно два, и оба намеренные.
///
/// <b>Первое: предыстория есть.</b> У демона кольцо последних
/// <see cref="LogLimits.HistoryCapacity"/> записей, которое наполняется независимо от подписки,
/// и <c>SubscribeLog</c> отвечает им. Без этого панель, подключившаяся к демону, работающему
/// вторые сутки, показала бы пустой экран — то есть ничего не показала бы именно в тот момент,
/// ради которого её открыли. У событий прогона буфера нет по обратной причине: пока никто не
/// подписан, съём выключен, записывать нечего.
///
/// <b>Второе: подписка живёт столько же, сколько панель, а не столько, сколько открыт режим.</b>
/// События прогона включаются только в «Макросах», потому что рисует их одна лишь канва и никому
/// другому они не нужны. С журналом не так: вопрос, на который он отвечает, — «что-то пошло не
/// так?», и задают его из любого режима. Сигнал, который виден, только если пойти и посмотреть,
/// — не сигнал; поэтому счётчик рейки живой, и живым его делает постоянная подписка. Демону это
/// стоит одной пачки раз в сотню миллисекунд — того же порядка, что <c>RunningMacrosChanged</c>,
/// — а движок не платит ничего: постановка записи в очередь неблокирующая, а сериализация идёт
/// на насосе.
///
/// <b>Переподписка ЗАМЕЩАЕТ ленту, а не дополняет её.</b> Предыстория — это авторитетный снимок
/// кольца демона, а <c>Seq</c> сквозной только в пределах жизни ОДНОГО демона: после его
/// перезапуска номера начинаются заново и столкнулись бы с уже накопленными. Замещение убирает
/// весь этот класс ошибок ценой того, что при обрыве связи из ленты пропадает всё старше кольца.
/// </summary>
public sealed class LogViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Сколько записей панель держит у себя.
    ///
    /// Больше кольца демона (тысяча) намеренно: пока связь не рвётся, лента копится дальше
    /// предыстории, и длинная сессия отладки не упирается в чужой предел. Больше пяти тысяч
    /// смысла нет — виртуализованный список это отрисует, но искать глазами в таком объёме уже
    /// нельзя, для этого есть файл.
    /// </summary>
    public const int MaxEntries = 5000;

    private readonly IIpcClient _client;
    private readonly IUiDispatcher _dispatcher;

    // Вся лента, от старой записи к новой. Rows — её подпоследовательность, отфильтрованная и в
    // том же порядке; на этом свойстве держится обрезка снизу.
    private readonly List<LogRowViewModel> _all = [];

    private LogLevelOption _levelFilter;
    private string _search = string.Empty;
    private bool _autoScroll = true;
    private bool _wantsLog;
    private long _lastSeq;
    private int _dropped;
    private int _problems;

    public LogViewModel(IIpcClient client, IUiDispatcher? dispatcher = null)
    {
        _client = client;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;
        _levelFilter = LevelOptions[0];

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;
    }

    /// <summary>Видимые строки — то, что прошло фильтр уровня и поиск.</summary>
    public ObservableCollection<LogRowViewModel> Rows { get; } = [];

    /// <summary>
    /// Пункты фильтра уровня. По умолчанию выбран самый нижний: панель показывает ВСЁ, что
    /// приехало, и ничего не прячет молча. Порог, ниже которого запись не покидает демон, — это
    /// <c>MinimumLevel</c> его Serilog, и он отсюда не управляется (см. замечание про
    /// <c>LoggingLevelSwitch</c> в отчёте волны).
    /// </summary>
    public IReadOnlyList<LogLevelOption> LevelOptions { get; } =
    [
        new(LogLevelDto.Verbose, Strings.Log_Level_All),
        new(LogLevelDto.Debug, Strings.Log_Level_Debug),
        new(LogLevelDto.Information, Strings.Log_Level_Information),
        new(LogLevelDto.Warning, Strings.Log_Level_Warning),
        new(LogLevelDto.Error, Strings.Log_Level_Error),
    ];

    /// <summary>Выбранный порог. Присвоение пересобирает <see cref="Rows"/>.</summary>
    [AllowNull]
    public LogLevelOption LevelFilter
    {
        get => _levelFilter;
        set
        {
            // ComboBox проталкивает null, пока перетряхивается его ItemsSource.
            if (value is null || ReferenceEquals(value, _levelFilter))
            {
                return;
            }

            _levelFilter = value;
            OnPropertyChanged(nameof(LevelFilter));
            Refilter();
        }
    }

    /// <summary>Подстрока поиска: по сообщению, источнику и тексту исключения, без учёта регистра.</summary>
    [AllowNull]
    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                Refilter();
            }
        }
    }

    /// <summary>
    /// Прокручивать ли ленту к последней записи. Включено по умолчанию и выключается одним
    /// щелчком: на живом демоне лента едет сама, и читать без этого выключателя нельзя.
    /// </summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetField(ref _autoScroll, value);
    }

    /// <summary>Сколько записей панель держит — до фильтра.</summary>
    public int TotalCount => _all.Count;

    /// <summary>
    /// Сколько среди них предупреждений и хуже. Это и есть счётчик рейки: см. пояснение к классу
    /// о том, почему подписка постоянная и почему число обязано быть живым из любого режима.
    /// </summary>
    public int ProblemCount => _problems;

    /// <summary>Сколько записей демон выбросил из-за переполнения своей очереди — дыра в ленте.</summary>
    public int Dropped => _dropped;

    /// <summary><c>true</c>, когда дыра есть и о ней надо сказать.</summary>
    public bool HasGap => _dropped > 0;

    /// <summary>«пропущено записей: 12» — прямо в шапке, а не в подсказке.</summary>
    public string GapText =>
        string.Format(CultureInfo.CurrentCulture, Strings.Log_Gap_Count, _dropped);

    /// <summary><c>true</c>, пока в ленте нет ни одной записи, — пустое состояние режима.</summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>
    /// <c>true</c>, когда лента пуста ПОТОМУ, что её очистили, а не потому, что записей не было.
    ///
    /// Существует ради одной строки текста, и строка того стоит: после «Очистить» демон записал
    /// предостаточно, и «демон пока ничего не записал» было бы прямым враньём про него — тем
    /// самым сортом вранья, который в этом проекте отдельно вычищают.
    /// </summary>
    public bool WasCleared { get; private set; }

    /// <summary>Пояснение под заголовком пустого состояния — своё для каждой из двух пустот.</summary>
    public string EmptyHint => WasCleared
        ? Strings.Log_Cleared_Hint
        : Strings.Log_Empty_Hint;

    /// <summary><c>true</c>, когда записи есть, но ни одна не прошла фильтр.</summary>
    public bool IsFilteredOut => _all.Count > 0 && Rows.Count == 0;

    /// <summary>Строка шапки: «812 записей · показано 41 · проблем: 3».</summary>
    public string SummaryText
    {
        get
        {
            if (_all.Count == 0)
            {
                return Strings.Log_Header_Empty;
            }

            var text = EntriesWord(_all.Count);
            if (Rows.Count != _all.Count)
            {
                text += string.Format(CultureInfo.CurrentCulture, Strings.Log_Header_Shown, Rows.Count);
            }

            return _problems == 0
                ? text
                : text + string.Format(CultureInfo.CurrentCulture, Strings.Log_Header_Problems, _problems);
        }
    }

    /// <summary>
    /// Поднимается после любого изменения ленты — по нему оболочка обновляет счётчик рейки, а
    /// вид доводит автопрокрутку.
    /// </summary>
    public event Action? LogChanged;

    /// <summary>
    /// Включает или выключает ленту для этого соединения и (при включении) забирает предысторию.
    ///
    /// Публичный, потому что зовут его двое: конструктор оболочки — один раз, на всю жизнь
    /// панели, — и обработчик <c>Connected</c>, потому что демон забывает флаг подписки вместе с
    /// соединением, и после переподключения ленту надо просить заново.
    /// </summary>
    public async Task SetSubscriptionAsync(bool enabled)
    {
        _wantsLog = enabled;
        try
        {
            var history = await _client
                .RequestAsync<LogEntryDto[]>(IpcMessageTypes.SubscribeLog, new SubscribeLogRequest(enabled))
                .ConfigureAwait(false);

            if (!enabled)
            {
                return;
            }

            _dispatcher.Post(() => Reset(history ?? []));
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // Обрыв между подключением и запросом. Поддерживающий цикл переподключится, снова
            // сработает Connected, и подписка повторится.
            Log.Warning(ex, "Не удалось подписаться на журнал демона");
        }
    }

    /// <summary>
    /// Убирает всё, что панель накопила. Кольцо демона при этом не трогается — там всё осталось,
    /// и переподключение принесёт его обратно. Это «протереть экран», а не «стереть журнал».
    /// </summary>
    public void Clear()
    {
        _all.Clear();
        Rows.Clear();
        _problems = 0;
        _dropped = 0;
        WasCleared = true;
        // _lastSeq НЕ сбрасывается: записи с меньшими номерами демон уже присылал, и приняв их
        // повторно, мы бы дождались их второй копии в следующей предыстории.
        RaiseCounts();
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.EventReceived -= OnEventReceived;
    }

    // ---- проводка к демону -----------------------------------------------------------------

    private void OnConnected()
    {
        if (_wantsLog)
        {
            _ = SetSubscriptionAsync(true);
        }
    }

    private void OnEventReceived(IpcEvent evt)
    {
        if (!string.Equals(evt.Type, IpcMessageTypes.LogEntries, StringComparison.Ordinal))
        {
            return;
        }

        var batch = IpcJson.Read<LogEntryBatch>(evt.Payload);
        if (batch is null)
        {
            return;
        }

        _dispatcher.Post(() => Ingest(batch));
    }

    // ---- лента ------------------------------------------------------------------------------

    private void Reset(IReadOnlyList<LogEntryDto> history)
    {
        _all.Clear();
        Rows.Clear();
        _problems = 0;
        _dropped = 0;
        _lastSeq = 0;
        // Новая предыстория — новое начало: то, что пользователь очистил у прошлого соединения,
        // к этой пустоте отношения не имеет.
        WasCleared = false;

        foreach (var entry in history)
        {
            Append(entry);
        }

        RaiseCounts();
    }

    private void Ingest(LogEntryBatch batch)
    {
        var changed = false;

        if (batch.Dropped > 0)
        {
            _dropped += batch.Dropped;
            changed = true;
        }

        foreach (var entry in batch.Entries)
        {
            // Двоение на подписке: демон включает ленту ДО того, как снимает предысторию (иначе
            // между этими двумя действиями была бы невосполнимая дыра), поэтому запись, попавшая
            // ровно между ними, приезжает дважды. Номер — единственное, чем настоящий повтор
            // отличается от второй такой же строки.
            if (entry.Seq <= _lastSeq)
            {
                continue;
            }

            Append(entry);
            changed = true;
        }

        if (changed)
        {
            RaiseCounts();
        }
    }

    private void Append(LogEntryDto entry)
    {
        var row = new LogRowViewModel(entry);
        _all.Add(row);
        _lastSeq = Math.Max(_lastSeq, entry.Seq);

        if (row.IsProblem)
        {
            _problems++;
        }

        if (row.Matches(_levelFilter.Level, Needle()))
        {
            Rows.Add(row);
        }

        Trim();
    }

    // Обрезка снизу. Rows — подпоследовательность _all в том же порядке, поэтому самая старая
    // строка, если она вообще видна, стоит в Rows первой; сверка по ссылке и есть весь поиск.
    private void Trim()
    {
        while (_all.Count > MaxEntries)
        {
            var oldest = _all[0];
            _all.RemoveAt(0);

            if (oldest.IsProblem)
            {
                _problems--;
            }

            if (Rows.Count > 0 && ReferenceEquals(Rows[0], oldest))
            {
                Rows.RemoveAt(0);
            }
        }
    }

    private void Refilter()
    {
        var needle = Needle();
        Rows.Clear();
        foreach (var row in _all)
        {
            if (row.Matches(_levelFilter.Level, needle))
            {
                Rows.Add(row);
            }
        }

        RaiseCounts();
    }

    private string? Needle() => _search.Length == 0 ? null : _search.ToLowerInvariant();

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(Dropped));
        OnPropertyChanged(nameof(HasGap));
        OnPropertyChanged(nameof(GapText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(WasCleared));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(IsFilteredOut));
        OnPropertyChanged(nameof(SummaryText));
        LogChanged?.Invoke();
    }

    // Разбор по %10/%100 лежал здесь копией; теперь форму выбирает общий PluralForms, а
    // сами формы — три явных ключа ресурсов.
    private static string EntriesWord(int count) => PluralForms.Format(
        count,
        Strings.Log_Header_Entries_One,
        Strings.Log_Header_Entries_Few,
        Strings.Log_Header_Entries_Many);
}
