using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// Один обход в том виде, в каком его показывает переключатель прогонов: чип в шапке полосы
/// лога, который макет рисует как <c>прогон 0x140804 ◂ ▸</c>.
///
/// Подпись — это КОНТЕКСТНОЕ ОКНО, а не id прогона, потому что различает записи именно оно, а
/// ради этого переключатель и существует: десять окон, загружающихся по одному графу, — это
/// десять обходов с одним id прогона и десятью хэндлами. Обход без контекстного окна (корневой
/// обход прогона по хоткею, который маршрутизируется исключительно селектором) подписывается
/// вместо этого своим макросом.
/// </summary>
public sealed class MacroRunViewModel : ObservableObject
{
    private bool _isFinished;
    private string? _finalOutcome;
    private bool _isPaused;
    private bool _pauseRequested;
    private bool _pausedAtBreakpoint;
    private string? _pauseReason;
    private int _elapsedMs;

    internal MacroRunViewModel(RunWalkDto walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        Walk = walk;
        Label = walk.Hwnd != 0
            ? string.Create(CultureInfo.InvariantCulture, $"0x{walk.Hwnd:X}")
            : walk.MacroName;
    }

    /// <summary>Обход, за которым следит эта строка.</summary>
    public RunWalkDto Walk { get; }

    /// <summary>Личность — то, по чему коррелирует каждое событие.</summary>
    public Guid WalkId => Walk.WalkId;

    /// <summary>Граф, по которому идут.</summary>
    public string MacroName => Walk.MacroName;

    /// <summary>Текст чипа: хэндл окна либо имя макроса, когда окна у обхода нет.</summary>
    public string Label { get; }

    /// <summary>Строки полосы лога для этого обхода, старые первыми.</summary>
    public ObservableCollection<RunLogRowViewModel> Log { get; } = [];

    /// <summary>
    /// <c>false</c>, когда панель начала слушать уже после начала этого обхода, так что голова
    /// <see cref="Log"/> потеряна. Показывается в полосе — молчать об этом нельзя.
    /// </summary>
    public bool FromStart => Walk.FromStart;

    /// <summary>Нода, на которой стоит walker, или <c>null</c>, когда обход уже закончился.</summary>
    public Guid? CurrentNodeId { get; private set; }

    /// <summary>Её подпись — то, что называет пилюля паузы. Приезжает вместе с событием, а не резолвится по графу.</summary>
    public string? CurrentNodeName { get; private set; }

    /// <summary>Обход закончился. Он остаётся в переключателе, чтобы его лог можно было дочитать.</summary>
    public bool IsFinished
    {
        get => _isFinished;
        private set => SetField(ref _isFinished, value);
    }

    /// <summary><c>true</c>, пока обход ещё идёт, — управляет живой точкой на чипе.</summary>
    public bool IsLive => !_isFinished;

    /// <summary>Чем закончился, по-русски, или <c>null</c>, пока он ещё идёт.</summary>
    public string? FinalOutcome
    {
        get => _finalOutcome;
        private set => SetField(ref _finalOutcome, value);
    }

    /// <summary>Сколько строк держим на обход. Застрявший цикл не имеет права раздувать панель без предела.</summary>
    public const int MaxRows = 500;

    // ---- состояние отладчика (D5) ---------------------------------------------------

    /// <summary>
    /// Ноды, которые этот обход уже прошёл, с тем, сколько каждая заняла и куда ушла. Управляет
    /// притушенными коробками с галочкой из макета.
    ///
    /// Словарь, а не просмотр <see cref="Log"/>, потому что лог подрезается по
    /// <see cref="MaxRows"/> и длинный обход начал бы снимать галочки со своих же самых ранних
    /// коробок. Цикл записывает ноду заново, так что отметка всегда от ПОСЛЕДНЕГО прохода — а
    /// именно его и хочет читать тот, кто смотрит на цикл.
    /// </summary>
    public Dictionary<Guid, (string Time, string Outcome)> Passed { get; } = [];

    /// <summary>
    /// Живое значение каждой переменной прогона, по имени, — правая колонка панели переменных.
    /// На каждый обход своё, потому что при веере у каждого окна собственный <c>{tag}</c>.
    /// </summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>Во сколько нод этот обход ВОШЁЛ — левая половина «3 / 11» из макета.</summary>
    public int NodesEntered { get; private set; }

    /// <summary>Припаркован прямо сейчас, ждёт команды отладчика.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set => SetField(ref _isPaused, value);
    }

    /// <summary>
    /// «Паузу» нажали, но обход всё ещё внутри ноды. Честное промежуточное состояние: пауза —
    /// это ПРОСЬБА, а <c>WaitForElement</c> способен продержать её минуту.
    /// </summary>
    public bool PauseRequested
    {
        get => _pauseRequested;
        internal set => SetField(ref _pauseRequested, value);
    }

    /// <summary>Припаркован точкой останова, а не шагом или кнопкой, — красная пилюля из макета.</summary>
    public bool PausedAtBreakpoint
    {
        get => _pausedAtBreakpoint;
        private set => SetField(ref _pausedAtBreakpoint, value);
    }

    /// <summary>Почему припаркован, в формулировках демона: «брейкпоинт», «шаг», «до курсора», «пауза».</summary>
    public string? PauseReason
    {
        get => _pauseReason;
        private set => SetField(ref _pauseReason, value);
    }

    /// <summary>
    /// Миллисекунды с начала обхода на момент последнего события. Пока обход жив, «0:12.4» на
    /// панели инструментов экстраполирует отсюда — см. <c>MacroEditorViewModel.TickElapsed</c>.
    /// </summary>
    public int ElapsedMs => _elapsedMs;

    /// <summary>Когда пришло последнее событие, чтобы живые часы могли добавить прошедшее с тех пор.</summary>
    public DateTimeOffset ElapsedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    internal void Paused(RunEventDto evt)
    {
        CurrentNodeId = evt.NodeId;
        CurrentNodeName = evt.NodeName;
        IsPaused = true;
        PauseRequested = false;
        PausedAtBreakpoint = evt.Kind == RunEventKind.BreakpointHit;
        PauseReason = evt.Detail;
        Touch(evt);
    }

    internal void Resumed(RunEventDto evt)
    {
        IsPaused = false;
        PausedAtBreakpoint = false;
        PauseReason = null;
        Touch(evt);
    }

    internal void VariableSet(RunEventDto evt)
    {
        if (evt.Variable is { Length: > 0 } name)
        {
            Variables[name] = evt.Detail ?? string.Empty;
        }

        Touch(evt);
    }

    internal void NodeEntered(RunEventDto evt)
    {
        // Строка для ноды, которая так и не сообщила о своём выходе (обход отменили внутри неё),
        // остаётся неполной, а не дозаполняется догадкой.
        CurrentRow()?.Settle();
        Log.Add(new RunLogRowViewModel(evt.ElapsedMs, evt.NodeId, evt.NodeName ?? string.Empty));
        CurrentNodeId = evt.NodeId;
        CurrentNodeName = evt.NodeName;
        NodesEntered++;
        Touch(evt);
        Trim();
    }

    internal void NodeExited(RunEventDto evt)
    {
        if (evt.NodeId is { } nodeId)
        {
            Passed[nodeId] = (
                RunLogRowViewModel.FormatDuration(evt.DurationMs),
                RunLogRowViewModel.DescribeOutcome(evt.Outcome));
        }

        Touch(evt);
        // Сопоставляем по id ноды с хвоста: обход последователен, поэтому открытая строка этой
        // ноды — последняя. Если только пару не разорвала выброшенная пачка — тогда завершать
        // нечего, и строка входа просто остаётся висеть незакрытой.
        for (var i = Log.Count - 1; i >= 0; i--)
        {
            var row = Log[i];
            if (row.IsCurrent && row.NodeId == evt.NodeId)
            {
                row.Complete(evt.Outcome, evt.Detail, evt.DurationMs);
                return;
            }
        }
    }

    internal void Finished(RunEventDto evt)
    {
        CurrentRow()?.Settle();
        CurrentNodeId = null;
        CurrentNodeName = null;
        IsFinished = true;
        FinalOutcome = RunLogRowViewModel.DescribeOutcome(evt.Outcome);
        // Завершившийся обход не может стоять на паузе, а оставленный флаг зажёг бы кнопку
        // «Дальше» для того, что уже закончилось.
        IsPaused = false;
        PauseRequested = false;
        PausedAtBreakpoint = false;
        PauseReason = null;
        Touch(evt);
        OnPropertyChanged(nameof(IsLive));
    }

    private void Touch(RunEventDto evt)
    {
        _elapsedMs = Math.Max(_elapsedMs, evt.ElapsedMs);
        ElapsedAtUtc = DateTimeOffset.UtcNow;
    }

    private RunLogRowViewModel? CurrentRow() =>
        Log.Count > 0 && Log[^1].IsCurrent ? Log[^1] : null;

    private void Trim()
    {
        while (Log.Count > MaxRows)
        {
            Log.RemoveAt(0);
        }
    }
}

/// <summary>
/// Все обходы, о которых панель слышала, и правило, за каким из них следует canvas.
///
/// <b>Почему один обход, а не все сразу.</b> По графу могут идти десять окон одновременно;
/// подсветка ноды на каждый обход зажигает девять коробок, о которых пользователь не просил.
/// Макет отвечает на это чипом прогона на панели инструментов отладчика — один выбранный
/// прогон, — и этот класс и есть тот самый выбор. <c>ExecutingNodeId</c> — это то, на чём стоит
/// ВЫБРАННЫЙ обход, а полоса лога показывает строки этого обхода и ничьи больше.
///
/// <b>Правила выбора, все до единого продиктованные принципом «следуй за тем, что живо»:</b>
///
///   * ничего не выбрано и начинается обход открытого макроса ⇒ выбираем его;
///   * выбранный обход ЗАВЕРШЁН и начинается новый ⇒ переключаемся на него, потому что
///     пользователь смотрит в лог, который перестал двигаться;
///   * выбранный обход ЖИВ и начинается ещё один ⇒ не трогаем его. Украсть выбор посреди
///     прогона — единственное, что сделало бы переключатель бесполезным как раз во время веера;
///   * пользователь открывает другой макрос ⇒ пересобираем список для ТОГО макроса. Обходы
///     других макросов остаются отслеживаемыми (с ограничением), так что возврат назад
///     восстанавливает лог, а не выбрасывает его;
///   * соединение с демоном обрывается ⇒ выбрасываем всё. Что произошло за время разрыва,
///     узнать невозможно, а лог с невидимой дырой хуже пустого.
/// </summary>
public sealed class MacroRunTracker
{
    /// <summary>
    /// Сколько обходов держим суммарно по всем макросам. Десять окон плюс их родитель — это
    /// одиннадцать; истории на три прогона более чем достаточно, чтобы было куда переключиться,
    /// а потолок не даёт долгой сессии копить их бесконечно.
    /// </summary>
    internal const int MaxTrackedWalks = 40;

    private readonly Dictionary<Guid, MacroRunViewModel> _byId = [];
    private readonly List<MacroRunViewModel> _order = [];

    /// <summary>Все отслеживаемые обходы, старые первыми.</summary>
    public IReadOnlyList<MacroRunViewModel> All => _order;

    /// <summary>Забывает всё. Путь переподключения — см. комментарий к классу.</summary>
    public void Clear()
    {
        _byId.Clear();
        _order.Clear();
    }

    /// <summary>Заводит обход, который уже шёл, когда мы подписались.</summary>
    public MacroRunViewModel Add(RunWalkDto walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        if (_byId.TryGetValue(walk.WalkId, out var existing))
        {
            return existing;
        }

        var run = new MacroRunViewModel(walk);
        _byId[walk.WalkId] = run;
        _order.Add(run);
        Evict();
        return run;
    }

    /// <summary>Применяет одно событие. Возвращает затронутый обход или <c>null</c> для незнакомого.</summary>
    /// <remarks>
    /// Событие обхода, начала которого мы не видели, ВЫБРАСЫВАЕТСЯ, а не достраивается. Такое
    /// возможно лишь тогда, когда <c>WalkStarted</c> ехал в пачке, которую демону пришлось
    /// выбросить, а у обхода, выдуманного из события ноды, не было бы имени макроса — то есть
    /// его нельзя было бы отнести к графу, а это ровно то единственное, что нужно переключателю.
    /// </remarks>
    public MacroRunViewModel? Apply(RunEventDto evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt.Kind == RunEventKind.WalkStarted)
        {
            return evt.Walk is { } walk ? Add(walk) : null;
        }

        if (!_byId.TryGetValue(evt.WalkId, out var run))
        {
            return null;
        }

        switch (evt.Kind)
        {
            case RunEventKind.NodeEntered:
                run.NodeEntered(evt);
                break;
            case RunEventKind.NodeExited:
                run.NodeExited(evt);
                break;
            case RunEventKind.WalkFinished:
                run.Finished(evt);
                break;
            case RunEventKind.Paused:
            case RunEventKind.BreakpointHit:
                run.Paused(evt);
                break;
            case RunEventKind.Resumed:
                run.Resumed(evt);
                break;
            case RunEventKind.VariableSet:
                run.VariableSet(evt);
                break;
            default:
                // Вид события, появившийся уже после этой панели. Игнорируем — так лог остаётся
                // честным, а не подписывает событие неверно; это свойство D5 досталось по
                // наследству, и его нужно беречь.
                break;
        }

        return run;
    }

    /// <summary>Обходы одного макроса, старые первыми, — то, что предлагает переключатель, пока открыт этот граф.</summary>
    public IReadOnlyList<MacroRunViewModel> For(string? macroName) => macroName is null
        ? []
        : [.. _order.Where(run => string.Equals(run.MacroName, macroName, StringComparison.Ordinal))];

    // Завершённые обходы уходят первыми и старые раньше, чтобы живой веер никогда не вытеснили
    // из-под пользователя его же собственные братья.
    private void Evict()
    {
        while (_order.Count > MaxTrackedWalks)
        {
            var victim = _order.FirstOrDefault(run => run.IsFinished) ?? _order[0];
            _order.Remove(victim);
            _byId.Remove(victim.WalkId);
        }
    }
}
