using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Живое состояние в том виде, в каком его сообщает демон: каждое отслеживаемое окно со своими
/// чипами тегов и каждый прогон макроса в полёте. До D2 это был <c>MainWindowViewModel</c>, и
/// он БЫЛ окном; теперь это одна из view-model'ей режимов оболочки, кормящая «Окна», «Прогоны»
/// и полосу прогонов разом, — отсюда переименование и отсюда же производные разбиения ниже.
///
/// Стадия 3 вынесла источник данных за пределы процесса. Форма не изменилась — сперва
/// подписаться, потом снять снимок, сверить по ключу, — но события теперь приезжают от демона,
/// а снимок стал запросом:
///
///   * <b>Перезапрашивать на <see cref="IIpcClient.Connected"/>, а не только при создании.</b>
///     Сервер выбрасывает клиента, переставшего вычерпывать, поэтому переподключение — событие
///     обычное, и всё, что пушили во время разрыва, потеряно. Засеяться заново — единственная
///     дорога обратно к истине, и именно поэтому <see cref="RefreshAsync"/> СВЕРЯЕТ (выбрасывая
///     строки, о которых демон больше не сообщает), а не просто добавляет-обновляет.
///   * <b>Каждый обработчик перекладывается через <see cref="IUiDispatcher"/>.</b> События
///     поднимаются в потоке чтения клиента, а <c>ObservableCollection</c> трогать позволительно
///     только из потока UI.
///
/// <b>Производное состояние (D2).</b> <see cref="TaggedWindows"/> и
/// <see cref="UntaggedWindows"/> — проекции <see cref="Windows"/>, которые держатся свежими в
/// тех же точках, где срабатывает <see cref="WindowsChanged"/>, а не по перезапросу: раскладка
/// 1b кладёт окна без тегов отдельной группой внизу, а сводке по тегам в боковой полосе нужен
/// тот же сигнал. Они именно СВЕРЯЮТСЯ, а не пересобираются, — так строка, в которую
/// пользователь набирает тег, сохраняет свой контейнер (а значит, и фокус), когда появляется
/// постороннее окно.
/// </summary>
public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IIpcClient _client;
    private readonly IUiDispatcher _dispatcher;

    public WorkspaceViewModel(IIpcClient client, IUiDispatcher? dispatcher = null)
    {
        _client = client;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;

        // Соединение обычно устанавливается раньше, чем появляется Avalonia (а значит, и эта
        // VM), так что первый Connected уже пришёл и ушёл. Засеваемся от живого соединения;
        // последующие переподключения идут через OnConnected.
        if (_client.IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Поднимается после любого изменения списка окон ИЛИ тегов любого окна — тот единственный
    /// сигнал, по которому оболочка пересчитывает свои счётчики и сводку по тегам, ничего не
    /// спрашивая у демона.
    /// </summary>
    public event Action? WindowsChanged;

    /// <summary>Зарегистрированные сейчас окна, в том порядке, в каком их сообщает демон.</summary>
    public ObservableCollection<WindowRowViewModel> Windows { get; } = [];

    /// <summary>Окна, несущие хотя бы один тег, в порядке <see cref="Windows"/>.</summary>
    public ObservableCollection<WindowRowViewModel> TaggedWindows { get; } = [];

    /// <summary>Окна без тегов — подчинённая группа внизу в раскладке 1b.</summary>
    public ObservableCollection<WindowRowViewModel> UntaggedWindows { get; } = [];

    /// <summary>Прогоны макросов, которые демон отслеживает прямо сейчас.</summary>
    public ObservableCollection<RunningMacroRowViewModel> Runs { get; } = [];

    /// <summary>Сколько окон опознано (= несут хотя бы один тег).</summary>
    public int IdentifiedCount => TaggedWindows.Count;

    /// <summary>Сколько окон всё ещё ждут тега.</summary>
    public int UntaggedCount => UntaggedWindows.Count;

    /// <summary>Строка шапки режима «Окна»: «8 опознано · 3 без тегов».</summary>
    public string WindowsSummaryText => Windows.Count == 0
        ? Strings.Windows_Header_Empty
        : UntaggedCount == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Windows_Header_Identified, IdentifiedCount)
            : string.Format(
                CultureInfo.CurrentCulture,
                Strings.Windows_Header_IdentifiedAndUntagged,
                IdentifiedCount,
                UntaggedCount);

    /// <summary>Разделитель над группой без тегов. Капсом, потому что подпись рисуется надзаголовком.</summary>
    public string UntaggedHeaderText =>
        string.Format(CultureInfo.CurrentCulture, Strings.Windows_Untagged_Header, UntaggedCount);

    /// <summary><c>true</c>, пока хотя бы одно окно без тегов, — этим включается вся группа.</summary>
    public bool HasUntagged => UntaggedWindows.Count > 0;

    /// <summary><c>true</c>, пока демон не сообщает вообще ни одного окна, — пустое состояние режима.</summary>
    public bool HasNoWindows => Windows.Count == 0;

    /// <summary>Шапка режима «Прогоны»; она же служит текстом пустого состояния.</summary>
    public string RunsHeaderText => Runs.Count == 0
        ? Strings.Runs_Header_Empty
        : string.Format(CultureInfo.CurrentCulture, Strings.Runs_Header_Count, Runs.Count);

    /// <summary><c>true</c>, пока отслеживается хотя бы один прогон, — этим включается кнопка «Стоп всё».</summary>
    public bool HasRuns => Runs.Count > 0;

    /// <summary>
    /// Пересевает оба списка от демона. Вызывается при создании и после каждого
    /// переподключения; вызывать безопасно в любой момент.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            var windows = await _client.RequestAsync<WindowDto[]>(IpcMessageTypes.GetWindows).ConfigureAwait(false);
            var runs = await _client.RequestAsync<RunningMacroDto[]>(IpcMessageTypes.GetRunningMacros)
                .ConfigureAwait(false);
            // Пишем в лог, потому что это единственный внешне видимый признак жизни панели:
            // если список выглядит неправильно, эта строка говорит, таким ли его сообщил демон
            // или его покорёжил UI.
            Log.Information(
                "Снимок от демона: окон {Windows}, запусков {Runs}",
                windows?.Length ?? 0,
                runs?.Length ?? 0);
            _dispatcher.Post(() =>
            {
                SyncWindows(windows ?? []);
                SyncRuns(runs ?? []);
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // Обрыв между подключением и запросом. Поддерживающий цикл переподключится, снова
            // сработает Connected, и он это повторит, — восстанавливаться здесь нечему.
            Log.Warning(ex, "Не удалось получить снимок состояния демона");
        }
    }

    /// <summary>Добавляет тег, набранный в поле строки <paramref name="row"/>.</summary>
    public Task<bool> AddTagAsync(WindowRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.AddTagAsync();
    }

    /// <summary>Снимает с окна один тег.</summary>
    public Task<bool> RemoveTagAsync(WindowRowViewModel row, string tag)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.RemoveTagAsync(tag);
    }

    /// <summary>Отменяет один прогон. Строка исчезнет, когда демон пришлёт новый список прогонов.</summary>
    public Task StopRunAsync(RunningMacroRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return StopAsync(row.RunId);
    }

    /// <summary>Отменяет все отслеживаемые прогоны (кнопка паники).</summary>
    public async Task StopAllRunsAsync()
    {
        // По запросу на прогон, а не одно пакетное сообщение: StopMacro уже есть, в списке от
        // силы несколько записей, а демон отвечает на каждый запрос только после того, как
        // исполнитель подтвердит.
        foreach (var runId in Runs.Select(row => row.RunId).ToList())
        {
            await StopAsync(runId).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Перерисовывает колонку прошедшего времени. Её тикает односекундный таймер окна — своего
    /// таймера VM не держит, чтобы оставаться свободной от типов Avalonia.
    /// </summary>
    public void RefreshElapsed()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in Runs)
        {
            row.Refresh(now);
        }
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.EventReceived -= OnEventReceived;
    }

    // ---- проводка к демону ---------------------------------------------------------------

    private void OnConnected() => _ = RefreshAsync();

    private void OnEventReceived(IpcEvent evt)
    {
        switch (evt.Type)
        {
            case IpcMessageTypes.WindowAppeared:
            case IpcMessageTypes.WindowTagsChanged:
                // Оба несут ПОЛНОЕ новое состояние, так что одна вставка-обновление годится для
                // обоих: появление — это та же вставка-обновление, которая просто ничего не
                // нашла.
                if (IpcJson.Read<WindowDto>(evt.Payload) is { } window)
                {
                    _dispatcher.Post(() =>
                    {
                        Upsert(window);
                        NotifyWindowsChanged();
                    });
                }

                break;

            case IpcMessageTypes.WindowClosed:
                if (IpcJson.Read<WindowClosedEvent>(evt.Payload) is { } closed)
                {
                    _dispatcher.Post(() => Remove(closed.Hwnd));
                }

                break;

            case IpcMessageTypes.RunningMacrosChanged:
                // Это событие несёт весь новый список целиком, так что round trip через
                // GetRunningMacros не нужен.
                var runs = IpcJson.Read<RunningMacroDto[]>(evt.Payload) ?? [];
                _dispatcher.Post(() => SyncRuns(runs));
                break;

            default:
                break; // MacrosChanged и ActivateWindow принадлежат другим слушателям
        }
    }

    private async Task StopAsync(Guid runId)
    {
        try
        {
            await _client.RequestAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(runId)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось остановить запуск {RunId}", runId);
        }
    }

    // ---- сверка коллекций -----------------------------------------------------------------

    // Добавить или обновить, ключ — hwnd. Идемпотентно, чтобы порядок запуска «сперва
    // подписаться, потом снять снимок» не мог породить строку-дубликат для окна, появившегося
    // между этими двумя шагами. NotifyWindowsChanged() — на совести вызывающего; SyncWindows
    // делает это одним разом на всю пачку.
    private void Upsert(WindowDto window)
    {
        if (FindRow(window.Hwnd) is { } existing)
        {
            existing.ApplyTags(window.Tags);
            return;
        }

        Windows.Add(new WindowRowViewModel(_client, window));
    }

    private void Remove(long hwnd)
    {
        if (FindRow(hwnd) is { } row)
        {
            Windows.Remove(row);
            NotifyWindowsChanged();
        }
    }

    // Полная сверка: за время переподключения мог потеряться WindowClosed, поэтому всё, чего в
    // снимке нет, должно уйти, а уцелевшие окна сохраняют свою строку (и недонабранное поле
    // тега в ней).
    private void SyncWindows(IReadOnlyList<WindowDto> snapshot)
    {
        var seen = new HashSet<long>();
        foreach (var window in snapshot)
        {
            seen.Add(window.Hwnd);
            Upsert(window);
        }

        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Windows[i].Hwnd))
            {
                Windows.RemoveAt(i);
            }
        }

        NotifyWindowsChanged();
    }

    // Прогоны приходят и уходят целыми списками, но строки сопоставляются по RunId, так что
    // уцелевший прогон сохраняет свой объект строки — а вместе с ним и нарисованное прошедшее
    // время — через обновление.
    private void SyncRuns(IReadOnlyList<RunningMacroDto> snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<Guid>();

        foreach (var run in snapshot)
        {
            seen.Add(run.RunId);
            if (FindRun(run.RunId) is { } existing)
            {
                existing.Refresh(now, run.CurrentNodeName);
            }
            else
            {
                Runs.Add(new RunningMacroRowViewModel(run));
            }
        }

        for (var i = Runs.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Runs[i].RunId))
            {
                Runs.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(RunsHeaderText));
        OnPropertyChanged(nameof(HasRuns));
    }

    // Единственное место, где обновляется производное состояние. Дёшево по замыслу: в списке
    // около десяти строк, так что от сверки за O(n²) уворачиваться не стоит, а делая её сразу,
    // мы гарантируем, что ни один вид никогда не увидит устаревшего разбиения.
    private void NotifyWindowsChanged()
    {
        Repartition();
        OnPropertyChanged(nameof(IdentifiedCount));
        OnPropertyChanged(nameof(UntaggedCount));
        OnPropertyChanged(nameof(WindowsSummaryText));
        OnPropertyChanged(nameof(UntaggedHeaderText));
        OnPropertyChanged(nameof(HasUntagged));
        OnPropertyChanged(nameof(HasNoWindows));
        WindowsChanged?.Invoke();
    }

    private void Repartition()
    {
        var tagged = new List<WindowRowViewModel>(Windows.Count);
        var untagged = new List<WindowRowViewModel>();
        foreach (var row in Windows)
        {
            (row.HasTags ? tagged : untagged).Add(row);
        }

        Reconcile(TaggedWindows, tagged);
        Reconcile(UntaggedWindows, untagged);

        // Чередующийся фон строк — из макета, а у ItemsControl в Avalonia нет индекса
        // чередования, поэтому индекс живёт на самой строке. Чередуется только помеченная
        // группа; группа без тегов приглушена равномерно.
        for (var i = 0; i < tagged.Count; i++)
        {
            tagged[i].IsAlternate = i % 2 == 1;
        }

        foreach (var row in untagged)
        {
            row.IsAlternate = false;
        }
    }

    // Сначала убрать, потом расставить, а не очистить и залить заново: пересборка коллекции
    // пересоздала бы все контейнеры и выбила бы фокус из поля тега посреди набора.
    private static void Reconcile(
        ObservableCollection<WindowRowViewModel> target,
        IReadOnlyList<WindowRowViewModel> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var current = target.IndexOf(desired[i]);
            if (current < 0)
            {
                target.Insert(i, desired[i]);
            }
            else if (current != i)
            {
                target.Move(current, i);
            }
        }
    }

    private WindowRowViewModel? FindRow(long hwnd)
    {
        foreach (var row in Windows)
        {
            if (row.Hwnd == hwnd)
            {
                return row;
            }
        }

        return null;
    }

    private RunningMacroRowViewModel? FindRun(Guid runId)
    {
        foreach (var row in Runs)
        {
            if (row.RunId == runId)
            {
                return row;
            }
        }

        return null;
    }
}
