using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

/// <summary>
/// Конструкторы событий прогона, чтобы тест view-model мог разыграть правдоподобный обход, не
/// выписывая каждый раз восемь позиционных аргументов DTO.
///
/// Намеренно бестолковые: ни состояния, ни правил порядка. Смысл этих тестов в том, что ПАНЕЛЬ
/// переживает всё, что бы демон ни прислал, — в том числе выход из ноды, вход в которую потерялся,
/// или событие обхода, начала которого она не видела, — так что помощник не имеет права втихую
/// приводить данные в порядок.
/// </summary>
internal static class RunEvents
{
    /// <summary>Описатель обхода. <paramref name="hwnd"/>, равный 0, означает «контекстного окна нет».</summary>
    public static RunWalkDto Walk(string macroName, long hwnd = 0, Guid? runId = null, int depth = 0) =>
        new(Guid.NewGuid(), runId ?? Guid.NewGuid(), macroName, hwnd, depth, DateTimeOffset.UtcNow, FromStart: true);

    public static RunEventDto Started(RunWalkDto walk) =>
        new(walk.WalkId, RunEventKind.WalkStarted, ElapsedMs: 0, Walk: walk);

    public static RunEventDto Entered(RunWalkDto walk, int elapsedMs, string nodeId) =>
        new(walk.WalkId, RunEventKind.NodeEntered, elapsedMs, nodeId);

    public static RunEventDto Exited(RunWalkDto walk, int elapsedMs, string nodeId, string outcome, string? detail,
        int durationMs) =>
        new(walk.WalkId, RunEventKind.NodeExited, elapsedMs, nodeId, outcome, detail, durationMs);

    public static RunEventDto Finished(RunWalkDto walk, int elapsedMs, string outcome, string? detail = null) =>
        new(walk.WalkId, RunEventKind.WalkFinished, elapsedMs, Outcome: outcome, Detail: detail);

    // ---- отладчик (D5) ------------------------------------------------------------------

    /// <summary>
    /// Припаркован на точке останова. В <c>Detail</c> уезжает то самое русское слово, которое
    /// кладёт туда демон, — панель показывает его как есть, поэтому подделка обязана совпадать.
    /// </summary>
    public static RunEventDto Breakpoint(RunWalkDto walk, int elapsedMs, string nodeId) =>
        new(walk.WalkId, RunEventKind.BreakpointHit, elapsedMs, nodeId, Detail: "брейкпоинт");

    /// <summary>Припаркован по любой другой причине.</summary>
    public static RunEventDto Paused(RunWalkDto walk, int elapsedMs, string nodeId, string reason = "пауза") =>
        new(walk.WalkId, RunEventKind.Paused, elapsedMs, nodeId, Detail: reason);

    public static RunEventDto Resumed(RunWalkDto walk, int elapsedMs, string nodeId) =>
        new(walk.WalkId, RunEventKind.Resumed, elapsedMs, nodeId);

    /// <summary>Присваивание переменной. <paramref name="nodeId"/> равен <c>null</c> для затравки от триггера.</summary>
    public static RunEventDto Variable(RunWalkDto walk, int elapsedMs, string name, string value,
        string? nodeId = null) =>
        new(walk.WalkId, RunEventKind.VariableSet, elapsedMs, nodeId, Detail: value, Variable: name);
}

/// <summary>Подача событий прогона во view-model так же, как это делает насос демона, — пачками.</summary>
internal static class FakeIpcClientRunExtensions
{
    /// <summary>Доставляет одну пачку. Идёт через <c>IpcJson</c>, так что round trip перечисления настоящий.</summary>
    public static void Push(this FakeIpcClient client, params RunEventDto[] events)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.RaiseEvent(IpcMessageTypes.RunEvents, new RunEventBatch(events, Dropped: 0));
    }
}
