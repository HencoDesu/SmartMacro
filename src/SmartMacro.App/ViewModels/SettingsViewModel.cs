using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Contracts.Settings;

namespace SmartMacro.App.ViewModels;

/// <summary>Пункт выбора способа ввода. Весь текст берётся из <see cref="InputMethodInfo"/> — второй копии нет.</summary>
public sealed class InputMethodChoice
{
    internal InputMethodChoice(InputMethod? method)
    {
        Method = method;
        if (method is null)
        {
            Title = "По умолчанию";
            Badge = string.Empty;
            Detail = "Берётся способ из «Ввод по умолчанию».";
            return;
        }

        var info = InputMethodInfo.Of(method.Value);
        Title = info.Title;
        Badge = info.Badge;
        Detail = info.Detail;
    }

    /// <summary><c>null</c> — «взять из умолчания»; пункт есть только у профиля.</summary>
    public InputMethod? Method { get; }

    /// <summary>Подпись.</summary>
    public string Title { get; }

    /// <summary>«в фоне ✓ · Shift+1 ✕» — две главные характеристики одной строкой.</summary>
    public string Badge { get; }

    /// <summary>Полная подсказка при наведении.</summary>
    public string Detail { get; }

    /// <summary><c>true</c>, когда бейдж есть что показать.</summary>
    public bool HasBadge => Badge.Length > 0;
}

/// <summary>Пункт выбора уровня журнала демона.</summary>
/// <param name="Level">Минимальный уровень, ниже которого записи не покидают демон.</param>
/// <param name="Title">Подпись — латиницей, как в файле журнала и в жетоне режима «Лог».</param>
public sealed record LogLevelChoice(LogLevelDto Level, string Title);

/// <summary>Одна строка блока диагностики.</summary>
public sealed class DiagnosticRowViewModel
{
    internal DiagnosticRowViewModel(DiagnosticDto dto)
    {
        Id = dto.Id;
        Status = dto.Status;
        Title = dto.Title;
        Detail = dto.Detail;
    }

    public string Id { get; }

    public DiagnosticStatus Status { get; }

    public string Title { get; }

    public string Detail { get; }

    /// <summary>
    /// Провал и предупреждение рисуются карточкой с текстом, порядок — карточкой не рисуется.
    ///
    /// Это не экономия места, а расстановка веса: успешных проверок всегда большинство, и если
    /// они выглядят так же, как провал, то провал перестаёт бросаться в глаза — то есть блок
    /// диагностики теряет единственный смысл, ради которого существует.
    /// </summary>
    public bool IsProblem => Status != DiagnosticStatus.Ok;

    public bool IsOk => Status == DiagnosticStatus.Ok;

    public bool IsFailed => Status == DiagnosticStatus.Failed;

    /// <summary>Значок: ✕ у провала, ⚑ у предупреждения, ✓ у порядка.</summary>
    public string Glyph => Status switch
    {
        DiagnosticStatus.Failed => "✕",
        DiagnosticStatus.Warning => "⚑",
        _ => "✓",
    };
}

/// <summary>
/// Режим «Настройки» — вариант 2c макета: один экран без прокрутки, плитки, диагностика полосой
/// снизу.
///
/// <b>Механика важнее списка полей.</b> До этой волны каждая ручка движка приезжала через
/// <c>IOptions&lt;T&gt;</c>, вычислялась один раз на старте, и любая правка требовала перезапуска
/// демона. Экран настроек поверх такой механики раздражал бы сильнее блокнота, поэтому под ним
/// лежит схема макросов: демон владеет файлом, панель правит его по IPC, демон поднимает
/// <c>SettingsChanged</c>, подписчики перечитывают. Живьём применяется почти всё — интервалы
/// опроса, пороги vision, профили (к новым окнам), способ ввода (со следующего цикла активации).
/// Вне процесса остаются только автозапуск и права.
///
/// <b>Правки НАКАПЛИВАЮТСЯ и уходят по «Применить».</b> Хранилище живое, но редактор — нет, и это
/// намеренно: поле, применяющееся на каждое нажатие клавиши, означает, что «12» по дороге побудет
/// «1», и демон честно поработает секунду с интервалом опроса в одну секунду вместо двенадцати.
/// Живость — это про то, что перезапуск не нужен, а не про то, что состояние меняется под
/// пальцами.
///
/// <b>Уровень журнала — единственное исключение, применяется сразу.</b> Он не часть файла: демон
/// двигает <c>LoggingLevelSwitch</c> и ничего не переписывает, а сдвиг живёт до перезапуска.
/// Копить в «Применить» вещь, которая никуда не сохраняется, значило бы приписать ей чужие
/// свойства.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Уровни, которые предлагает экран. Verbose и Fatal опущены: первый демон не пишет, второй нечего выбирать.</summary>
    public static IReadOnlyList<LogLevelChoice> LogLevels { get; } =
    [
        new(LogLevelDto.Error, "Error"),
        new(LogLevelDto.Warning, "Warning"),
        new(LogLevelDto.Information, "Information"),
        new(LogLevelDto.Debug, "Debug"),
    ];

    private readonly IIpcClient _client;
    private readonly IUiDispatcher _dispatcher;

    private SettingsSnapshotDto? _snapshot;
    private bool _loading;

    private string _processPoll = "1";
    private string _windowPoll = "2";
    private double _matchThreshold = 0.7;
    private InputMethodChoice _defaultInput;
    private LogLevelChoice _logLevel;
    private bool _runAtLogon;
    private bool _runElevated = true;
    private string _newProfileName = string.Empty;
    private string _status = string.Empty;
    private DateTimeOffset? _checkedAt;
    private bool _busy;

    public SettingsViewModel(IIpcClient client, IUiDispatcher? dispatcher = null)
    {
        _client = client;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;
        _defaultInput = InputChoices[0];
        _logLevel = LogLevels[2];

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;
    }

    /// <summary>
    /// Способы ввода, которые экран ПРЕДЛАГАЕТ, — только реализованные.
    ///
    /// <c>SendInput</c> в модели настроек место занимает, а в списке его нет: проверить его без
    /// живой игры нельзя, а предложить непроверенный способ хуже, чем не предложить. Список
    /// строится из <see cref="InputMethodInfo.Available"/>, так что в тот день, когда способ
    /// появится, он появится и здесь — сам.
    /// </summary>
    public IReadOnlyList<InputMethodChoice> InputChoices { get; } =
        [.. InputMethodInfo.Available.Select(info => new InputMethodChoice(info.Method))];

    /// <summary>Те же способы плюс пункт «По умолчанию» — для выбора у конкретного профиля.</summary>
    public IReadOnlyList<InputMethodChoice> ProfileInputChoices { get; } =
    [
        new(null),
        .. InputMethodInfo.Available.Select(info => new InputMethodChoice(info.Method)),
    ];

    /// <summary>Профили процессов. Единственный блок переменной высоты — поэтому на экране он последний.</summary>
    public ObservableCollection<ProcessProfileRowViewModel> Profiles { get; } = [];

    /// <summary>Замечания последней попытки применить. Непусто = не сохранено.</summary>
    public ObservableCollection<string> Issues { get; } = [];

    /// <summary>Результаты последней проверки среды.</summary>
    public ObservableCollection<DiagnosticRowViewModel> Diagnostics { get; } = [];

    /// <summary>Поднимается после любого изменения — по нему оболочка обновляет красную точку на рейке.</summary>
    public event Action? SettingsChanged;

    // ---- редактируемые значения --------------------------------------------------------------

    /// <summary>Как часто искать новые процессы, секунды. Строкой, потому что за ней <c>TextBox</c>.</summary>
    [AllowNull]
    public string ProcessPollSeconds
    {
        get => _processPoll;
        set => SetEdited(ref _processPoll, value ?? string.Empty);
    }

    /// <summary>Как часто проверять живость окон, секунды.</summary>
    [AllowNull]
    public string WindowPollSeconds
    {
        get => _windowPoll;
        set => SetEdited(ref _windowPoll, value ?? string.Empty);
    }

    /// <summary>
    /// Порог совпадения по умолчанию.
    ///
    /// <b>Счётчика «столько-то шагов задают его сами» здесь нет и не будет.</b> Чтобы показать
    /// такое число, панель обязана пересчитать все графы библиотеки, а устареет оно при первой же
    /// правке любого из них. Настройки не инвентаризируют содержимое макросов — это работа
    /// редактора. Остаётся пометка, что это умолчание и шаг вправе задать своё.
    /// </summary>
    public double MatchThreshold
    {
        get => _matchThreshold;
        set
        {
            if (SetEdited(ref _matchThreshold, Math.Round(value, 2)))
            {
                OnPropertyChanged(nameof(MatchThresholdText));
            }
        }
    }

    /// <summary>«0.70» — то же значение рядом с ползунком.</summary>
    public string MatchThresholdText => _matchThreshold.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Способ ввода по умолчанию.</summary>
    [AllowNull]
    public InputMethodChoice DefaultInput
    {
        get => _defaultInput;
        set
        {
            // ListBox проталкивает null, пока перетряхивается его ItemsSource.
            if (value is null || ReferenceEquals(value, _defaultInput))
            {
                return;
            }

            _defaultInput = value;
            OnPropertyChanged(nameof(DefaultInput));
            RaiseDirty();
        }
    }

    /// <summary>
    /// Минимальный уровень журнала демона. Присвоение уходит демону НЕМЕДЛЕННО — см. заметку у
    /// класса о том, почему именно это поле не копится до «Применить».
    /// </summary>
    [AllowNull]
    public LogLevelChoice LogLevel
    {
        get => _logLevel;
        set
        {
            if (value is null || ReferenceEquals(value, _logLevel))
            {
                return;
            }

            _logLevel = value;
            OnPropertyChanged(nameof(LogLevel));
            if (!_loading)
            {
                _ = SetLogLevelAsync(value.Level);
            }
        }
    }

    /// <summary>Поднимать демон вместе с сеансом.</summary>
    public bool RunAtLogon
    {
        get => _runAtLogon;
        set => SetEdited(ref _runAtLogon, value);
    }

    /// <summary>
    /// Работать с правами администратора. Интерфейс обязан сказать про эту галочку две вещи, и
    /// говорит их прямо на экране: смена требует перезапуска демона, а снятие ломает ввод в окна
    /// игры, запущенной от администратора (UIPI).
    /// </summary>
    public bool RunElevated
    {
        get => _runElevated;
        // StartupMechanismText и ShowsElevationRestartNote поднимает RaiseDirty внутри SetEdited
        // — вместе со всем остальным, что зависит от накопленных правок.
        set => SetEdited(ref _runElevated, value);
    }

    /// <summary>Имя процесса для новой строки профилей.</summary>
    [AllowNull]
    public string NewProfileName
    {
        get => _newProfileName;
        set => SetField(ref _newProfileName, value ?? string.Empty);
    }

    // ---- производное --------------------------------------------------------------------------

    /// <summary>Как приложение зарегистрирует автозапуск при текущих галочках.</summary>
    public string StartupMechanismText => (_runAtLogon, _runElevated) switch
    {
        (false, false) => "автозапуск выключен",
        (true, false) => "ключ Run в HKCU",
        (false, true) => "автозапуска нет; повышение запрашивается при ручном старте",
        (true, true) => "задача в Планировщике, наивысшие права",
    };

    /// <summary><c>true</c>, когда галочка прав отличается от той, с которой демон запущен.</summary>
    public bool ShowsElevationRestartNote =>
        _snapshot is not null && _snapshot.Settings.Startup.RunElevated != _runElevated;

    /// <summary>Сколько правок ждут «Применить».</summary>
    public int ChangeCount { get; private set; }

    /// <summary><c>true</c>, когда есть что применять.</summary>
    public bool IsDirty => ChangeCount > 0;

    /// <summary>«2 изменения не применены» — в полосе внизу экрана.</summary>
    public string ChangeText => ChangeCount switch
    {
        0 => string.Empty,
        1 => "1 изменение не применено",
        2 or 3 or 4 => string.Create(CultureInfo.CurrentCulture, $"{ChangeCount} изменения не применены"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{ChangeCount} изменений не применены"),
    };

    /// <summary><c>true</c>, пока идёт запрос: гасит кнопки, чтобы не отправить два «Применить» подряд.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetField(ref _busy, value))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    /// <summary><c>true</c>, когда «Применить» имеет смысл нажимать.</summary>
    public bool CanApply => IsDirty && !_busy;

    /// <summary>Короткая строка о том, чем кончилось последнее действие.</summary>
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary><c>true</c>, когда есть замечания и сохранение не состоялось.</summary>
    public bool HasIssues => Issues.Count > 0;

    /// <summary>Папка демона — её открывает кнопка «Открыть папку».</summary>
    public string FolderPath => _snapshot?.FolderPath ?? string.Empty;

    /// <summary>Полный путь к файлу настроек — подпись в шапке.</summary>
    public string SettingsFilePath => _snapshot?.SettingsFilePath ?? string.Empty;

    /// <summary><c>true</c>, когда демон вообще ответил хоть раз.</summary>
    public bool IsLoaded => _snapshot is not null;

    /// <summary>«проверка от 14:22:07 · 4 из 6 в порядке» либо приглашение проверить.</summary>
    public string DiagnosticsSummary
    {
        get
        {
            if (Diagnostics.Count == 0)
            {
                return "проверка не запускалась";
            }

            var ok = Diagnostics.Count(row => row.IsOk);
            var time = _checkedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";
            return string.Create(CultureInfo.CurrentCulture,
                $"проверка от {time} · {ok} из {Diagnostics.Count} в порядке");
        }
    }

    /// <summary>Сколько проверок не в порядке — красная точка на рейке.</summary>
    public int ProblemCount => Diagnostics.Count(row => row.IsProblem);

    // ---- действия -----------------------------------------------------------------------------

    /// <summary>Забирает снимок у демона и раскладывает его по полям, теряя несохранённые правки.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _client.RequestAsync<SettingsSnapshotDto>(IpcMessageTypes.GetSettings)
                .ConfigureAwait(false);
            if (snapshot is not null)
            {
                _dispatcher.Post(() => Load(snapshot));
            }
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось получить настройки демона");
        }
    }

    /// <summary>
    /// Отправляет накопленные правки. Пустой ответ = записано; непустой — не записано, и он несёт
    /// причины (та же договорённость, что у <c>SaveMacro</c>).
    /// </summary>
    public async Task ApplyAsync()
    {
        if (_snapshot is null || IsBusy)
        {
            return;
        }

        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));

        // Разбор текстовых полей — до отправки: «abc» в поле интервала это не настройка, которую
        // демон обязан отвергать, а недопечатанный ввод, и сказать о нём должен тот, кто видит
        // курсор.
        if (!TryBuild(out var edited))
        {
            Status = "не применено";
            OnPropertyChanged(nameof(HasIssues));
            return;
        }

        // Те же правила, что прогонит демон, — из общего валидатора в Contracts. Прогоняются
        // здесь ради мгновенного ответа, а не вместо демонских: реализация одна, копии нет.
        var local = AppSettingsValidator.Validate(edited);
        if (local.Count > 0)
        {
            foreach (var issue in local)
            {
                Issues.Add(issue.Message);
            }

            Status = "не применено";
            OnPropertyChanged(nameof(HasIssues));
            return;
        }

        IsBusy = true;
        try
        {
            var response = await _client
                .RequestAsync<SettingsIssue[]>(IpcMessageTypes.SaveSettings, new SaveSettingsRequest(edited))
                .ConfigureAwait(false) ?? [];

            _dispatcher.Post(() =>
            {
                foreach (var issue in response)
                {
                    Issues.Add(issue.Message);
                }

                OnPropertyChanged(nameof(HasIssues));
                Status = response.Length == 0 ? "применено" : "не применено";
                // Пуш SettingsChanged всё равно приедет и переразложит поля; здесь мы лишь
                // снимаем счётчик правок сразу, чтобы кнопка не выглядела не нажатой.
                if (response.Length == 0)
                {
                    RaiseDirty();
                }
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось сохранить настройки");
            _dispatcher.Post(() => Status = "демон не ответил");
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    /// <summary>Возвращает поля к тому, что сейчас у демона, отбрасывая накопленное.</summary>
    public void Revert()
    {
        if (_snapshot is { } snapshot)
        {
            Load(snapshot);
            Status = "правки отменены";
        }
    }

    /// <summary>Возвращает файл настроек к умолчаниям.</summary>
    public async Task ResetAsync()
    {
        try
        {
            var snapshot = await _client.RequestAsync<SettingsSnapshotDto>(IpcMessageTypes.ResetSettings)
                .ConfigureAwait(false);
            if (snapshot is not null)
            {
                _dispatcher.Post(() =>
                {
                    Load(snapshot);
                    Status = "сброшено к умолчаниям";
                });
            }
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось сбросить настройки");
        }
    }

    /// <summary>Двигает уровень журнала демона. Живёт до перезапуска демона и никуда не сохраняется.</summary>
    public async Task SetLogLevelAsync(LogLevelDto level)
    {
        try
        {
            var snapshot = await _client
                .RequestAsync<SettingsSnapshotDto>(IpcMessageTypes.SetLogLevel, new SetLogLevelRequest(level))
                .ConfigureAwait(false);
            if (snapshot is not null)
            {
                _dispatcher.Post(() => _snapshot = snapshot);
            }
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось сменить уровень журнала демона");
        }
    }

    /// <summary>
    /// Прогоняет проверки среды. Пять из шести делает демон; шестую — время ответа канала —
    /// панель, потому что демон не может честно измерить время ответа самому себе.
    /// </summary>
    public async Task RunDiagnosticsAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        DiagnosticDto[] results;
        try
        {
            results = await _client.RequestAsync<DiagnosticDto[]>(IpcMessageTypes.RunDiagnostics)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Проверка среды не выполнена");
            _dispatcher.Post(() => Fill([
                new DiagnosticDto(DiagnosticIds.Channel, DiagnosticStatus.Failed, "Канал с демоном",
                    "демон не ответил — движок не работает, макросы не запускаются"),
            ]));
            return;
        }

        stopwatch.Stop();
        var channel = new DiagnosticDto(DiagnosticIds.Channel, DiagnosticStatus.Ok, "Канал с демоном",
            string.Create(CultureInfo.CurrentCulture, $"отвечает, {stopwatch.ElapsedMilliseconds} мс"));

        _dispatcher.Post(() => Fill([channel, .. results]));
    }

    /// <summary>Открывает папку демона в проводнике.</summary>
    public void OpenFolder()
    {
        if (string.IsNullOrEmpty(FolderPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = FolderPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Не удалось открыть папку '{Folder}'", FolderPath);
        }
    }

    /// <summary>
    /// Добавляет профиль по имени из <see cref="NewProfileName"/>.
    ///
    /// <b>Здесь и только здесь применяется таблица известных сигналов побудки.</b> Узнанному
    /// имени (<c>elementclient_64</c>) значение проставляется само, вместе с паузами, которые без
    /// него не нужны; <c>notepad</c> остаётся без сигнала, и это рабочая семантика «обычный
    /// процесс, пробуждение пропускается», а не незаполненное поле. В интерфейсе числа нет вовсе:
    /// это добытый реверсом костыль под одну игру, и правят его, если уж совсем припёрло, в файле.
    /// </summary>
    public void AddProfile()
    {
        var name = _newProfileName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var row = new ProcessProfileRowViewModel(KnownActivationSignals.NewProfile(name), ProfileInputChoices);
        row.Changed += RaiseDirty;
        Profiles.Add(row);
        NewProfileName = string.Empty;
        RaiseDirty();
    }

    /// <summary>Убирает профиль. За окнами этого процесса демон перестанет следить со следующего тика.</summary>
    public void RemoveProfile(ProcessProfileRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Changed -= RaiseDirty;
        Profiles.Remove(row);
        RaiseDirty();
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.EventReceived -= OnEventReceived;
        foreach (var row in Profiles)
        {
            row.Changed -= RaiseDirty;
        }
    }

    // ---- проводка к демону ---------------------------------------------------------------------

    private void OnConnected() => _ = RefreshAsync();

    private void OnEventReceived(IpcEvent evt)
    {
        if (!string.Equals(evt.Type, IpcMessageTypes.SettingsChanged, StringComparison.Ordinal))
        {
            return;
        }

        var snapshot = IpcJson.Read<SettingsSnapshotDto>(evt.Payload);
        if (snapshot is null)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            // Несохранённые правки НЕ затираются: пуш приходит и от нашего собственного
            // «Применить», и от правки файла блокнотом, и стереть на нём наполовину заполненную
            // форму означало бы наказать пользователя за то, что он открыл файл в соседнем окне.
            // Перекладываем только тогда, когда на экране нечего терять.
            if (IsDirty)
            {
                _snapshot = snapshot;
                RaiseDirty();
                Status = "файл настроек изменился — «Отменить» покажет новое";
                return;
            }

            Load(snapshot);
        });
    }

    // ---- раскладка --------------------------------------------------------------------------

    private void Load(SettingsSnapshotDto snapshot)
    {
        _snapshot = snapshot;
        _loading = true;
        try
        {
            var settings = snapshot.Settings;
            ProcessPollSeconds = settings.Watch.ProcessPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            WindowPollSeconds = settings.Watch.WindowPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            MatchThreshold = settings.Vision.MatchThreshold;
            DefaultInput = InputChoices.FirstOrDefault(c => c.Method == settings.Input.DefaultMethod)
                           ?? InputChoices[0];
            RunAtLogon = settings.Startup.RunAtLogon;
            RunElevated = settings.Startup.RunElevated;
            LogLevel = LogLevels.FirstOrDefault(c => c.Level == snapshot.LogLevel) ?? LogLevels[2];

            foreach (var row in Profiles)
            {
                row.Changed -= RaiseDirty;
            }

            Profiles.Clear();
            foreach (var profile in settings.Profiles)
            {
                var row = new ProcessProfileRowViewModel(profile, ProfileInputChoices);
                row.Changed += RaiseDirty;
                Profiles.Add(row);
            }
        }
        finally
        {
            _loading = false;
        }

        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(SettingsFilePath));
        OnPropertyChanged(nameof(IsLoaded));
        RaiseDirty();
    }

    // Собирает то, что уйдёт демону. false = текстовые поля не разбираются; причины уже в Issues.
    private bool TryBuild([NotNullWhen(true)] out AppSettings? settings)
    {
        settings = null;
        if (_snapshot is null)
        {
            return false;
        }

        if (!TryParse(_processPoll, "Поиск новых процессов", out var processPoll)
            | !TryParse(_windowPoll, "Проверка живости окон", out var windowPoll))
        {
            return false;
        }

        var profiles = new List<ProcessProfileSettings>(Profiles.Count);
        foreach (var row in Profiles)
        {
            if (!row.TryBuild(out var profile, out var error))
            {
                Issues.Add(error);
                return false;
            }

            profiles.Add(profile);
        }

        var baseline = _snapshot.Settings;
        settings = baseline with
        {
            Watch = new WatchSettings
            {
                ProcessPollIntervalSeconds = processPoll,
                WindowPollIntervalSeconds = windowPoll,
            },
            Input = new InputSettings { DefaultMethod = _defaultInput.Method ?? InputMethod.SendMessage },
            // Только порог по умолчанию: остальное в Vision (темп опроса, область и пороги
            // распознавания класса) экран не показывает и потому обязан протащить как есть —
            // сохранение из панели не имеет права стирать то, что правили в файле.
            Vision = baseline.Vision with { MatchThreshold = _matchThreshold },
            Startup = new StartupSettings { RunAtLogon = _runAtLogon, RunElevated = _runElevated },
            Profiles = profiles,
        };
        return true;
    }

    private bool TryParse(string text, string title, out int value)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        Issues.Add($"{title}: «{text}» — это не целое число.");
        return false;
    }

    private void Fill(IReadOnlyList<DiagnosticDto> results)
    {
        Diagnostics.Clear();
        // ПРОВАЛЫ ПЕРВЫМИ, внутри — порядок демона. Найдено глазами: карточка отказа, зажатая
        // между двумя короткими строками «в порядке», теряется ровно так же, как если бы её не
        // было, — а блок диагностики существует только ради неё. Успешных проверок всегда
        // большинство, и приоритет здесь не косметика, а весь смысл полосы.
        foreach (var dto in results.OrderByDescending(r => (int)r.Status))
        {
            Diagnostics.Add(new DiagnosticRowViewModel(dto));
        }

        _checkedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(DiagnosticsSummary));
        OnPropertyChanged(nameof(ProblemCount));
        SettingsChanged?.Invoke();
    }

    private bool SetEdited<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName]
        string? name = null)
    {
        if (!SetField(ref field, value, name))
        {
            return false;
        }

        RaiseDirty();
        return true;
    }

    // Считает правки по полям, а не сравнением собранного AppSettings: список профилей —
    // IReadOnlyList, и структурного равенства у записи с ним не выходит, а «1 изменение» вместо
    // «изменения есть» — ровно то, чего просит полоса внизу экрана.
    private void RaiseDirty()
    {
        var count = 0;
        if (_snapshot is { } snapshot)
        {
            var settings = snapshot.Settings;
            if (_processPoll != settings.Watch.ProcessPollIntervalSeconds.ToString(CultureInfo.InvariantCulture))
            {
                count++;
            }

            if (_windowPoll != settings.Watch.WindowPollIntervalSeconds.ToString(CultureInfo.InvariantCulture))
            {
                count++;
            }

            if (Math.Abs(_matchThreshold - settings.Vision.MatchThreshold) > 0.0001)
            {
                count++;
            }

            if (_defaultInput.Method != settings.Input.DefaultMethod)
            {
                count++;
            }

            if (_runAtLogon != settings.Startup.RunAtLogon)
            {
                count++;
            }

            if (_runElevated != settings.Startup.RunElevated)
            {
                count++;
            }

            count += CountProfileChanges(settings.Profiles);
        }

        ChangeCount = count;
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(StartupMechanismText));
        OnPropertyChanged(nameof(ShowsElevationRestartNote));
        SettingsChanged?.Invoke();
    }

    private int CountProfileChanges(IReadOnlyList<ProcessProfileSettings> baseline)
    {
        // Добавленные и убранные считаются по одной правке за штуку; изменённые — по одной за
        // строку, а не за поле: «поправил профиль» это одно действие с точки зрения того, кто на
        // экран смотрит.
        var changes = Math.Abs(Profiles.Count - baseline.Count);
        var pairs = Math.Min(Profiles.Count, baseline.Count);
        for (var i = 0; i < pairs; i++)
        {
            if (Profiles[i].DiffersFrom(baseline[i]))
            {
                changes++;
            }
        }

        return changes;
    }
}
