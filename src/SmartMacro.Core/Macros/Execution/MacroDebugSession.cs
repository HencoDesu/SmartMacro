using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Состояние отладчика: какие ноды объявлены точками останова, какие обходы припаркованы и
/// сколько панелей подключено. По одному экземпляру на демон.
///
/// ────────────────────────────────────────────────────────────────────────────────────────
/// <b>Где живут точки останова и почему здесь, а не в файле макроса.</b>
///
/// План спрашивал «в графе или в сессии?» и оставлял вопрос открытым. Они живут ЗДЕСЬ, в памяти
/// демона, всю жизнь его процесса — и ни минутой дольше.
///
///   · Точка останова — это факт о сессии отладки, а не о макросе. Сохранив её в
///     <c>macros/*.json</c>, мы положили бы её в тот самый артефакт, который пользователь
///     правит, смотрит диффом и — особенно это касается примеров — отдаёт другим людям.
///     «Почему у меня макрос встаёт на третьей ноде» — вопрос, на который никому не следует
///     отвечать.
///   · Сохранение означало бы ещё и то, что точка останова ПАЧКАЕТ редактор: поставил красную
///     точку на чистом графе — изволь сохранять, забыл сохранить — она молча пропала. И то и
///     другое хуже, чем потерять её при перезапуске демона.
///   · А та эргономическая выгода, ради которой люди на самом деле и хотят сохранения, —
///     «мои точки останова на месте, когда я снова открываю панель», — достаётся здесь даром,
///     потому что демон по замыслу переживает панель. В этом и весь смысл разделения.
///
/// Чем за это платим: точки останова не переживают «Выход» из трея. И это правильный размен —
/// перезапуск демона есть заодно и тот момент, когда пересобирается каждый прогон макроса,
/// каждый тег и каждая регистрация хоткея, так что от сессии всё равно ничего не остаётся.
/// ────────────────────────────────────────────────────────────────────────────────────────
///
/// <b>Припаркованный обход не должен пережить свою аудиторию.</b> Именно ради этого режима
/// отказа и существует весь счёт подключений. Обход, ждущий внутри
/// <see cref="MacroDebugGate.WaitAsync"/>, держит слот single-flight своего прогона в
/// <see cref="MacroRunRegistry"/>, то есть хоткей этого макроса мёртв, пока обход не сдвинется.
/// Демон резидентен и управляет живой игрой — «пока пользователь его не перезапустит» здесь не
/// ответ. Поэтому:
///
///   · Каждый подключённый отладчик есть подписчик событий прогона
///     (<c>SubscribeRunEvents</c>), а его сервер и так освобождает при отключении, в том числе
///     при падении.
///   · Когда отцепляется ПОСЛЕДНИЙ, <see cref="Release"/> распускает все обходы и забывает все
///     запросы на паузу. Точки останова остаются лежать, но перестают кусаться: точка,
///     остановившая обход, который некому распустить, воспроизвела бы тот же самый клин.
///   · Распустить, а не прервать — это консервативный выбор: прогон запустили законно (обычно
///     хоткеем), а брошенный на полпути макрос способен оставить игру в худшем состоянии, чем
///     если бы он доработал.
///
/// <b>Таймаута по бездействию нет, и это намеренно.</b> Единственный случай, который таймаут
/// бы покрыл, — «панель открыта, а пользователь отошёл», и там пауза делает ровно свою работу:
/// возобновить автоматизацию живой игры под человеком, который читает экран, хуже того, от чего
/// таймаут защищал бы. Отмена («■ Стоп», выключение) и так распускает парковку.
/// </summary>
public sealed partial class MacroDebugSession : IMacroDebugger
{
    private readonly Lock _lock = new();

    // Имя макроса → id нод. Имена макросов сравниваются порядково (ordinal), как и во всей
    // остальной системе; ноды адресуются guid'ом, поэтому у них вопроса регистра нет вовсе.
    private readonly Dictionary<string, HashSet<Guid>> _breakpoints = new(StringComparer.Ordinal);

    private readonly Dictionary<Guid, WalkState> _walks = [];

    /// <summary>
    /// Обходы, у которых сессия видела хотя бы одну ноду, — чтобы «Паузе», нацеленной на уже
    /// завершившийся обход, можно было отказать, а не заводить состояние, которое никто никогда
    /// не заберёт.
    /// </summary>
    private readonly HashSet<Guid> _live = [];

    private readonly ILogger<MacroDebugSession> _logger;

    private int _attached;

    public MacroDebugSession(ILogger<MacroDebugSession> logger) => _logger = logger;

    /// <inheritdoc />
    /// <remarks>
    /// Становится true, как только подключена хотя бы одна панель, независимо от того, есть ли
    /// вообще точки останова: «Паузу» могут запросить в любой момент, а значит, до затвора надо
    /// суметь дотянуться. Когда свойство истинно, а ничего не взведено, цена — одна блокировка и
    /// два поиска по словарю на ноду, против walker'а, у которого самая дешёвая нода стоит
    /// round trip по Win32.
    /// </remarks>
    public bool IsActive => Volatile.Read(ref _attached) > 0;

    /// <summary>Сколько панелей подключено. Диагностика и тесты.</summary>
    public int AttachedCount => Volatile.Read(ref _attached);

    // -------------------------------------------------------- подключение и отключение

    /// <summary>
    /// Ещё один подключённый отладчик. <c>IpcServer</c> спаривает это с <see cref="Release"/> по
    /// тому же фронту, что и подписку на события прогона, включая путь отключения.
    /// </summary>
    public void Acquire()
    {
        if (Interlocked.Increment(ref _attached) == 1)
        {
            LogAttached();
        }
    }

    /// <summary>
    /// Одним меньше. Последний уходящий распускает всё — см. комментарий к классу; это и есть
    /// ответ на «панель умерла, а обход остался на паузе».
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _attached) > 0)
        {
            return;
        }

        List<MacroDebugGate> stranded;
        lock (_lock)
        {
            stranded = [.. _walks.Values.Select(state => state.Gate).OfType<MacroDebugGate>()];
            _walks.Clear();
            // И список тоже: всё, что ещё идёт, само перерегистрируется, как только отладчик
            // подключится снова, а хранить мёртвые id — значит течь по Guid на каждый прогон.
            _live.Clear();
        }

        foreach (var gate in stranded)
        {
            gate.Release();
        }

        if (stranded.Count > 0)
        {
            LogAutoResumed(stranded.Count);
        }

        LogDetached();
    }

    // ------------------------------------------------------------------ точки останова

    /// <summary>
    /// Заменяет точки останова одного макроса. Пустой список убирает макрос из отображения
    /// целиком, поэтому <see cref="Breakpoints"/> никогда не сообщает о пустом наборе.
    /// </summary>
    public void SetBreakpoints(string macroName, IReadOnlyList<Guid> nodeIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        ArgumentNullException.ThrowIfNull(nodeIds);

        var wanted = nodeIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        lock (_lock)
        {
            if (wanted.Count == 0)
            {
                _breakpoints.Remove(macroName);
            }
            else
            {
                _breakpoints[macroName] = wanted;
            }
        }

        LogBreakpointsSet(macroName, wanted.Count);
    }

    /// <summary>Все макросы, у которых есть точки останова, по алфавиту. Ответ на <c>GetBreakpoints</c>.</summary>
    public IReadOnlyList<BreakpointSetDto> Breakpoints()
    {
        lock (_lock)
        {
            return
            [
                .. _breakpoints
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new BreakpointSetDto(pair.Key, [.. pair.Value.Order()]))
            ];
        }
    }

    // ------------------------------------------------------------------------- команды

    /// <summary>
    /// Применяет одну команду панели к одному обходу.
    /// </summary>
    /// <param name="walkId">Обход, к которому команда адресована.</param>
    /// <param name="command">Что от него требуется.</param>
    /// <param name="nodeId">Целевая нода для <see cref="DebugCommand.RunToNode"/>; остальными командами не читается.</param>
    /// <returns>
    /// Подтверждение приёма, которое возвращает протокол. <c>Accepted = false</c> означает, что
    /// обход неизвестен: он завершился либо принадлежит прогону демона, который старше этой
    /// сессии.
    /// </returns>
    public DebugAckDto Command(Guid walkId, DebugCommand command, Guid? nodeId)
    {
        MacroDebugGate? release = null;
        DebugAckDto ack;

        lock (_lock)
        {
            // «Пауза» — единственная команда, которая вправе адресовать обход, ни разу нами не
            // затворённый: обход идёт как ни в чём не бывало, а мы просим его встать на
            // следующей ноде. Все прочие команды работают по состоянию, которое обязано уже
            // существовать.
            if (!_walks.TryGetValue(walkId, out var state))
            {
                if (command != DebugCommand.Pause || !_live.Contains(walkId))
                {
                    return new DebugAckDto(Accepted: false, Paused: false, PauseRequested: false);
                }

                state = new WalkState();
                _walks[walkId] = state;
            }

            switch (command)
            {
                case DebugCommand.Pause:
                    // Уже припаркован ⇒ просить нечего. Иначе это ЗАПРОС, который исполнится на
                    // ближайшей границе нод, а до неё может быть шестидесятисекундный
                    // WaitForElement — панель пишет «пауза…», пока событие Paused это не
                    // подтвердит.
                    if (state.Gate is null)
                    {
                        state.Pending = DebugPauseReason.Requested;
                        state.RunToNodeId = null;
                    }

                    break;

                case DebugCommand.Resume:
                    state.Pending = null;
                    state.RunToNodeId = null;
                    release = Take(state);
                    break;

                case DebugCommand.Step:
                    state.Pending = DebugPauseReason.Step;
                    state.RunToNodeId = null;
                    release = Take(state);
                    break;

                case DebugCommand.RunToNode:
                    // Без id ноды это означало бы «идти в никуда», то есть обычный Resume под
                    // вводящим в заблуждение именем. Лучше отказать.
                    if (nodeId is not { } target || target == Guid.Empty)
                    {
                        return new DebugAckDto(Accepted: false, state.Gate is not null, state.Pending is not null);
                    }

                    state.Pending = null;
                    state.RunToNodeId = target;
                    release = Take(state);
                    break;

                default:
                    return new DebugAckDto(Accepted: false, state.Gate is not null, state.Pending is not null);
            }

            ack = new DebugAckDto(Accepted: true, state.Gate is not null,
                state.Pending is not null || state.RunToNodeId is not null);
        }

        // Снаружи блокировки: отпускание запускает продолжение walker'а, а оно на следующей
        // ноде позовёт Arm обратно.
        release?.Release();
        LogCommand(command.ToString(), walkId);
        return ack;
    }

    // ------------------------------------------------------------------- IMacroDebugger

    /// <inheritdoc />
    public MacroDebugGate? Arm(Guid walkId, string macroName, Guid nodeId, string nodeName)
    {
        // Перепроверять внутри блокировки незачем: отключение, бегущее с нами наперегонки,
        // отпустит затвор в ту же секунду, как его увидит, — Release() выгребает _walks.
        if (!IsActive)
        {
            return null;
        }

        MacroDebugGate gate;
        lock (_lock)
        {
            _live.Add(walkId);
            var state = _walks.TryGetValue(walkId, out var existing) ? existing : null;

            // Точка останова первой: явная красная точка старше шага, который случайно сюда
            // приземлился, да и панель рисует её иначе.
            DebugPauseReason? reason =
                HasBreakpoint(macroName, nodeId)
                    ? DebugPauseReason.Breakpoint
                    : state?.Pending is { } pending
                        ? pending
                        : state?.RunToNodeId == nodeId
                            ? DebugPauseReason.Cursor
                            : null;

            if (reason is not { } pauseReason)
            {
                return null;
            }

            state ??= new WalkState();
            _walks[walkId] = state;
            // Израсходовано: шаг — это одна нода, «до курсора» — одно прибытие.
            state.Pending = null;
            state.RunToNodeId = null;
            gate = new MacroDebugGate(walkId, nodeId, nodeName, pauseReason);
            state.Gate = gate;
        }

        LogPaused(walkId, nodeName, gate.Reason.ToString());
        return gate;
    }

    /// <inheritdoc />
    public void Disarm(MacroDebugGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        lock (_lock)
        {
            if (_walks.TryGetValue(gate.WalkId, out var state) && ReferenceEquals(state.Gate, gate))
            {
                state.Gate = null;
            }
        }
    }

    /// <inheritdoc />
    public void WalkFinished(Guid walkId)
    {
        lock (_lock)
        {
            _live.Remove(walkId);
            _walks.Remove(walkId);
        }
    }

    private bool HasBreakpoint(string macroName, Guid nodeId) =>
        _breakpoints.TryGetValue(macroName, out var nodes) && nodes.Contains(nodeId);

    private static MacroDebugGate? Take(WalkState state)
    {
        var gate = state.Gate;
        state.Gate = null;
        return gate;
    }

    /// <summary>Состояние отладчика по одному обходу. Под замком сессии; наружу выходит только в виде затвора.</summary>
    private sealed class WalkState
    {
        /// <summary>Причина припарковаться на СЛЕДУЮЩЕЙ ноде, какой бы она ни оказалась. Расходуется при использовании.</summary>
        public DebugPauseReason? Pending { get; set; }

        /// <summary>Припарковаться, когда дойдём до этой ноды. Расходуется по прибытии; не дойти вовсе — законно.</summary>
        public Guid? RunToNodeId { get; set; }

        /// <summary>Затвор, которым обход сейчас удерживают, либо <c>null</c>, когда он идёт.</summary>
        public MacroDebugGate? Gate { get; set; }
    }

    [LoggerMessage(LogLevel.Debug, "Отладчик подключён")]
    partial void LogAttached();

    [LoggerMessage(LogLevel.Debug, "Отладчик отключён")]
    partial void LogDetached();

    [LoggerMessage(LogLevel.Warning,
        "Отцепился последний отладчик — распущено припаркованных обходов: {Count}, иначе они держали бы свои слоты single-flight вечно")]
    partial void LogAutoResumed(int count);

    [LoggerMessage(LogLevel.Debug, "Точек останова у '{MacroName}': {Count}")]
    partial void LogBreakpointsSet(string macroName, int count);

    [LoggerMessage(LogLevel.Debug, "Команда отладчика {Command} обходу {WalkId}")]
    partial void LogCommand(string command, Guid walkId);

    [LoggerMessage(LogLevel.Information, "Обход {WalkId} припаркован перед нодой '{NodeName}' ({Reason})")]
    partial void LogPaused(Guid walkId, string nodeName, string reason);
}
