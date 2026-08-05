using System.Diagnostics;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Execution;

/// <summary>Идентичность обхода, передаваемая в <see cref="IMacroRunObserver.WalkStarted"/>.</summary>
/// <param name="WalkId">Свой на каждый вызов <see cref="MacroExecutor.RunAsync"/> — включая каждую ветку разветвления под-макроса.</param>
/// <param name="RunId">Отслеживаемый прогон, которому принадлежит обход; <see cref="Guid.Empty"/>, когда вызывающий работает мимо реестра (тесты).</param>
/// <param name="MacroName">
/// МАКРОС (бандл), которому принадлежит обход, — не имя обходимого графа. У обхода под-макроса
/// это по-прежнему имя родителя: по нему панель отбирает обходы открытого макроса, а сессия
/// отладчика ключует точки останова.
/// </param>
/// <param name="SubmacroId">Под-макрос, который обходят, либо <c>null</c> — граф верхнего уровня.</param>
/// <param name="SubmacroName">Его подпись либо <c>null</c> вместе с <paramref name="SubmacroId"/>.</param>
/// <param name="ContextWindow">Окно, по которому работают ноды без цели, либо <c>null</c>.</param>
/// <param name="Depth">Уровень вложенности: 0 у обхода макроса, 1 у обхода его под-макроса.</param>
public readonly record struct MacroWalkStart(
    Guid WalkId,
    Guid RunId,
    string MacroName,
    Guid? SubmacroId,
    string? SubmacroName,
    IntPtr? ContextWindow,
    int Depth);

/// <summary>
/// Канал прогресса исполнителя — тот шов, на который волна D3b повесила живую канву панели.
///
/// <b><see cref="IsEnabled"/> — это не любезность, это и есть средство от затопления.</b> Демон
/// резидентен, а панель поднимается по требованию, так что подавляющее большинство прогонов
/// происходит, когда никто не смотрит. Walker проверяет этот флаг раньше, чем засечёт время
/// ноды, соберёт строку подробностей или вообще что-нибудь выделит в памяти; когда там
/// <c>false</c>, весь съём показаний стоит одно volatile-чтение на ноду. Реализация ОБЯЗАНА
/// сделать это дёшево и ОБЯЗАНА сделать это честно: константный <c>true</c> вынес бы
/// форматирование строк на горячий путь того, что управляет живой игрой.
///
/// <see cref="WalkStarted"/> и <see cref="WalkFinished"/> — исключение: они стреляют независимо
/// от <see cref="IsEnabled"/>, потому что список живых обходов у публикатора — это то, из чего
/// панель, подключившаяся посреди прогона, вообще узнаёт, что прогон идёт. Это два вызова на
/// прогон макроса, а не два на ноду.
///
/// <b>Вызывается с потоков движка, возможно, сразу со многих</b> (разветвление
/// <c>RunSubmacroNode</c> обходит N графов параллельно). Реализации обязаны быть
/// потокобезопасными и не имеют права блокировать: вызывающий находится между двумя вводами в
/// игру.
/// </summary>
public interface IMacroRunObserver
{
    /// <summary>Слушает ли вообще кто-нибудь. Проверяется на каждой ноде, а значит, обязано быть дёшево.</summary>
    bool IsEnabled { get; }

    /// <summary>Обход начался. Вызывается всегда, даже когда <see cref="IsEnabled"/> равно <c>false</c>.</summary>
    void WalkStarted(MacroWalkStart walk);

    /// <summary>Walker вошёл в ноду. Вызывается только при <see cref="IsEnabled"/>.</summary>
    /// <param name="walkId">Обход, к которому относится событие.</param>
    /// <param name="elapsedMs">Миллисекунд с начала обхода.</param>
    /// <param name="nodeId">Нода, по которой панель адресует подсветку и точки останова.</param>
    /// <param name="nodeName">Её подпись — то, что печатает полоса лога.</param>
    void NodeEntered(Guid walkId, int elapsedMs, Guid nodeId, string nodeName);

    /// <summary>Нода отработала. Вызывается только при <see cref="IsEnabled"/>.</summary>
    /// <param name="walkId">Обход, к которому относится событие.</param>
    /// <param name="elapsedMs">Миллисекунд с начала обхода.</param>
    /// <param name="nodeId">Отработавшая нода.</param>
    /// <param name="nodeName">Её подпись.</param>
    /// <param name="outcome">Одно из <see cref="RunOutcomes"/>.</param>
    /// <param name="detail">Свободные подробности для полосы лога либо <c>null</c>.</param>
    /// <param name="durationMs">Реальное время внутри ноды, включая ожидание под-макросов.</param>
    void NodeExited(Guid walkId, int elapsedMs, Guid nodeId, string nodeName, string outcome, string? detail,
        int durationMs);

    /// <summary>Обход закончился. Вызывается всегда, даже когда <see cref="IsEnabled"/> равно <c>false</c>.</summary>
    /// <param name="walkId">Закончившийся обход.</param>
    /// <param name="elapsedMs">Сколько миллисекунд он шёл.</param>
    /// <param name="outcome"><see cref="RunOutcomes.Completed"/>, <see cref="RunOutcomes.Aborted"/> или <see cref="RunOutcomes.Cancelled"/>.</param>
    /// <param name="detail">Причина обрыва либо <c>null</c>.</param>
    void WalkFinished(Guid walkId, int elapsedMs, string outcome, string? detail);

    // ---------------------------------------------------------------------- отладчик (D5)

    /// <summary>
    /// Переменной прогона присвоили значение. Вызывается только при <see cref="IsEnabled"/>.
    ///
    /// Таких событий на обход единицы, а не два на ноду: пишут переменные сид <c>cursor</c> от
    /// триггера (докладывается один раз в начале обхода, с <paramref name="nodeId"/> равным
    /// <c>null</c>) и <c>FoundPointVar</c>/<c>ResultVar</c> условных нод.
    /// </summary>
    /// <param name="walkId">Обход, в котором произошла запись.</param>
    /// <param name="elapsedMs">Миллисекунд с начала обхода.</param>
    /// <param name="name">Имя переменной.</param>
    /// <param name="value">Её строка для показа — то, во что развернулось бы <c>{name}</c>.</param>
    /// <param name="nodeId">Нода, которая записала значение, либо <c>null</c> для сида от триггера.</param>
    /// <param name="nodeName">Её подпись, либо <c>null</c> вместе с <paramref name="nodeId"/>.</param>
    void VariableSet(Guid walkId, int elapsedMs, string name, string value, Guid? nodeId, string? nodeName);

    /// <summary>
    /// Обход припарковался перед <paramref name="nodeId"/> и ждёт, когда его отпустят.
    /// Вызывается только при <see cref="IsEnabled"/> — когда никто не подключён, поставить на
    /// паузу всё равно некому.
    /// </summary>
    /// <param name="walkId">Припаркованный обход.</param>
    /// <param name="elapsedMs">Миллисекунд с начала обхода.</param>
    /// <param name="nodeId">Нода, перед которой встали.</param>
    /// <param name="nodeName">Её подпись.</param>
    /// <param name="reason">Определяет, какой вид события получит панель и как это будет сформулировано.</param>
    void WalkPaused(Guid walkId, int elapsedMs, Guid nodeId, string nodeName, DebugPauseReason reason);

    /// <summary>Обход отпустили, и он вот-вот выполнит <paramref name="nodeId"/>. Вызывается только при <see cref="IsEnabled"/>.</summary>
    /// <param name="walkId">Отпущенный обход.</param>
    /// <param name="elapsedMs">Миллисекунд с начала обхода.</param>
    /// <param name="nodeId">Нода, которая сейчас выполнится.</param>
    /// <param name="nodeName">Её подпись.</param>
    void WalkResumed(Guid walkId, int elapsedMs, Guid nodeId, string nodeName);
}

/// <summary>
/// Состояние съёма показаний одного обхода: его id, отметка времени старта и наблюдатель,
/// которому докладывать.
///
/// Структура, спускаемая вниз по walker'у, а не поля на <see cref="MacroExecutor"/>:
/// исполнитель — синглтон, обходящий множество графов одновременно, так что пообходному
/// состоянию на нём не место. Любой метод ничего не делает, когда наблюдателя нет, — и именно
/// это избавляет места вызова в walker'е от проверок на null.
/// </summary>
internal readonly struct MacroWalkTrace
{
    private readonly IMacroRunObserver? _observer;
    private readonly long _startTimestamp;

    private MacroWalkTrace(IMacroRunObserver? observer, Guid walkId, long startTimestamp)
    {
        _observer = observer;
        WalkId = walkId;
        _startTimestamp = startTimestamp;
    }

    /// <summary>Идентичность этого обхода.</summary>
    public Guid WalkId { get; }

    /// <summary>
    /// Нужны ли ПОНОДОВЫЕ события. False и когда наблюдателя нет, и когда никто не подписан, —
    /// walker ветвится по этому свойству прежде, чем что-либо форматировать.
    /// </summary>
    public bool IsTracing => _observer is { IsEnabled: true };

    /// <summary>Сырая отметка времени для замера одной ноды; её же и скармливают обратно в <see cref="NodeExited"/>.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Открывает обход и объявляет о нём. Id выделяется всегда — списку живых обходов он нужен и без съёма показаний.</summary>
    public static MacroWalkTrace Begin(
        IMacroRunObserver? observer,
        Guid runId,
        string macroName,
        Guid? submacroId,
        string? submacroName,
        IntPtr? contextWindow,
        int depth)
    {
        var trace = new MacroWalkTrace(observer, Guid.NewGuid(), Stopwatch.GetTimestamp());
        observer?.WalkStarted(
            new MacroWalkStart(trace.WalkId, runId, macroName, submacroId, submacroName, contextWindow, depth));
        return trace;
    }

    /// <summary>Миллисекунд с начала обхода.</summary>
    public int ElapsedMs => ToMs(Stopwatch.GetTimestamp() - _startTimestamp);

    public void NodeEntered(MacroNode node)
    {
        if (_observer is { IsEnabled: true } observer)
        {
            observer.NodeEntered(WalkId, ElapsedMs, node.Id, MacroNodeNames.Display(node));
        }
    }

    public void NodeExited(MacroNode node, string outcome, string? detail, long nodeStartTimestamp)
    {
        if (_observer is { IsEnabled: true } observer)
        {
            observer.NodeExited(WalkId, ElapsedMs, node.Id, MacroNodeNames.Display(node), outcome, detail,
                ToMs(Stopwatch.GetTimestamp() - nodeStartTimestamp));
        }
    }

    public void Finished(string outcome, string? detail) =>
        _observer?.WalkFinished(WalkId, ElapsedMs, outcome, detail);

    public void VariableSet(string name, string value, MacroNode? node)
    {
        if (_observer is { IsEnabled: true } observer)
        {
            observer.VariableSet(WalkId, ElapsedMs, name, value, node?.Id,
                node is null ? null : MacroNodeNames.Display(node));
        }
    }

    public void Paused(Guid nodeId, string nodeName, DebugPauseReason reason)
    {
        if (_observer is { IsEnabled: true } observer)
        {
            observer.WalkPaused(WalkId, ElapsedMs, nodeId, nodeName, reason);
        }
    }

    public void Resumed(Guid nodeId, string nodeName)
    {
        if (_observer is { IsEnabled: true } observer)
        {
            observer.WalkResumed(WalkId, ElapsedMs, nodeId, nodeName);
        }
    }

    private static int ToMs(long ticks) => (int)(ticks * 1000 / Stopwatch.Frequency);
}
