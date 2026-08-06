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
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels;

/// <summary>Пункт выбора способа ввода. Весь текст берётся из <see cref="InputMethodInfo"/> — второй копии нет.</summary>
public sealed class InputMethodChoice
{
    internal InputMethodChoice(InputMethod? method)
    {
        Method = method;
        if (method is null)
        {
            Title = Strings.Settings_Input_Inherit;
            Badge = string.Empty;
            Detail = Strings.Settings_Input_InheritDetail;
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

/// <summary>
/// Режим «Настройки» — вариант 2c макета, из которого ушла полоса диагностики: один экран без
/// прокрутки, плитки.
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

    // Скобки пробуждения по имени процесса. Экран их НЕ ПОКАЗЫВАЕТ и потому не имеет права ими
    // распоряжаться — проносит нетронутыми, ровно как непоказываемые поля Vision. Единственная
    // правка, которую панель себе позволяет, — добавить хук новому профилю с узнанным именем
    // (см. AddProfile): это и есть правило «таблица известных сигналов применяется ПРИ СОЗДАНИИ».
    //
    // Снятый профиль хук за собой НЕ уносит: число добыто реверсом, набрать его в панели негде, а
    // «удалил профиль, добавил обратно, окна перестали просыпаться» — ровно тот молчаливый отказ,
    // от которого этот блок и убран с экрана.
    private readonly Dictionary<string, ProcessHookSettings> _hooks = new(StringComparer.OrdinalIgnoreCase);

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
    ///
    /// <b>Механизма на экране больше нет.</b> Раньше под галочками стояла строка «СПОСОБ»,
    /// называвшая одно из четырёх: ничего / ключ <c>Run</c> / повышение при ручном старте / задача
    /// в Планировщике. Половина исчезла вместе с Планировщиком, а оставшаяся половина — деталь
    /// Windows, а не решение, которое пользователь принимает: галочка «запускать при входе» и есть
    /// ключ реестра, галочка прав и есть запрос UAC при старте демона.
    /// </summary>
    public bool RunElevated
    {
        get => _runElevated;
        // ShowsElevationRestartNote поднимает RaiseDirty внутри SetEdited — вместе со всем
        // остальным, что зависит от накопленных правок.
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

    /// <summary><c>true</c>, когда галочка прав отличается от той, с которой демон запущен.</summary>
    public bool ShowsElevationRestartNote =>
        _snapshot is not null && _snapshot.Settings.Startup.RunElevated != _runElevated;

    /// <summary>Сколько правок ждут «Применить».</summary>
    public int ChangeCount { get; private set; }

    /// <summary><c>true</c>, когда есть что применять.</summary>
    public bool IsDirty => ChangeCount > 0;

    /// <summary>«2 изменения не применены» — в полосе внизу экрана.</summary>
    public string ChangeText => ChangeCount == 0
        ? string.Empty
        : PluralForms.Format(
            ChangeCount,
            Strings.Settings_Header_Changes_One,
            Strings.Settings_Header_Changes_Few,
            Strings.Settings_Header_Changes_Many);

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

    /// <summary>
    /// Вердикт демона о том, почему файл настроек не прочитан, — или пусто, если прочитан.
    ///
    /// Показывается ПОЛОСОЙ ВВЕРХУ ФОРМЫ, а не значком и не подсказкой: на экране в этот момент
    /// не содержимое файла (умолчания или последний удачный снимок), а «Применить» перезапишет
    /// файл целиком. Человек обязан прочитать это ровно там, где собирается нажать кнопку.
    ///
    /// <b>«Применить» при этом НЕ блокируется.</b> Перезаписать сломанный файл — законное
    /// намерение; опасна была тихая потеря, а не сама возможность.
    /// </summary>
    public string FileFault => _snapshot?.FileFault ?? string.Empty;

    /// <summary><c>true</c>, когда полосу «файл не прочитан» надо показать.</summary>
    public bool ShowsFileFault => FileFault.Length > 0;

    /// <summary><c>true</c>, когда демон вообще ответил хоть раз.</summary>
    public bool IsLoaded => _snapshot is not null;


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
            Status = Strings.Settings_Status_NotApplied;
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

            Status = Strings.Settings_Status_NotApplied;
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
                Status = response.Length == 0 ? Strings.Settings_Status_Applied : Strings.Settings_Status_NotApplied;
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
            _dispatcher.Post(() => Status = Strings.Settings_Status_NoAnswer);
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
            Status = Strings.Settings_Status_Reverted;
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
                    Status = Strings.Settings_Status_Reset;
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
                _dispatcher.Post(() => SetSnapshot(snapshot));
            }
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось сменить уровень журнала демона");
        }
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
    /// имени (<c>elementclient_64</c>) заводится хук — сигнал и обе паузы, которые без него не
    /// нужны; <c>notepad</c> остаётся без хука, и это рабочая семантика «обычный процесс,
    /// пробуждение пропускается», а не незаполненное поле. В интерфейсе хука нет вовсе: это
    /// добытый реверсом костыль под одну игру, и правят его, если уж совсем припёрло, в файле.
    ///
    /// Существующий хук НЕ перезаписывается: у того, кто уже поправил число под свою сборку игры,
    /// оно не имеет права взяться «само» обратно.
    /// </summary>
    public void AddProfile()
    {
        var name = _newProfileName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (!_hooks.ContainsKey(name) && KnownActivationSignals.NewHook(name) is { } hook)
        {
            _hooks[name] = hook;
        }

        var row = new ProcessProfileRowViewModel(new ProcessProfileSettings { ProcessName = name },
            ProfileInputChoices);
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
                SetSnapshot(snapshot);
                RaiseDirty();
                Status = Strings.Settings_Status_FileChanged;
                return;
            }

            Load(snapshot);
        });
    }

    // ---- раскладка --------------------------------------------------------------------------

    // Единственное место, где меняется _snapshot. Заведено ради полосы «файл не прочитан»: снимок
    // приезжает тремя дорогами (ответ на GetSettings, пуш SettingsChanged, ответ на SetLogLevel), и
    // предупреждение, появляющееся не на всех трёх, — это предупреждение, которого в нужный момент
    // может не быть.
    private void SetSnapshot(SettingsSnapshotDto snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(FileFault));
        OnPropertyChanged(nameof(ShowsFileFault));
    }

    private void Load(SettingsSnapshotDto snapshot)
    {
        SetSnapshot(snapshot);
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

            // Хуки перекладываются целиком и без разбора: экран их не показывает, так что
            // «правка» здесь может быть только одна — та, что придёт из файла.
            _hooks.Clear();
            foreach (var (name, hook) in settings.Hooks)
            {
                _hooks[name] = hook;
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

        if (!TryParse(_processPoll, Strings.Settings_Field_ProcessPoll, out var processPoll)
            | !TryParse(_windowPoll, Strings.Settings_Field_WindowPoll, out var windowPoll))
        {
            return false;
        }

        var profiles = new List<ProcessProfileSettings>(Profiles.Count);
        foreach (var row in Profiles)
        {
            profiles.Add(row.Build());
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
            // Копия, а не baseline.Hooks: AddProfile мог завести запись новому профилю. Всё
            // остальное здесь — то, что прочиталось из файла, слово в слово.
            Hooks = new Dictionary<string, ProcessHookSettings>(_hooks, StringComparer.OrdinalIgnoreCase),
        };
        return true;
    }

    private bool TryParse(string text, string title, out int value)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        Issues.Add(string.Format(CultureInfo.CurrentCulture,
            Strings.Settings_BadNumber, title, text));
        return false;
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
        OnPropertyChanged(nameof(ShowsElevationRestartNote));
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
