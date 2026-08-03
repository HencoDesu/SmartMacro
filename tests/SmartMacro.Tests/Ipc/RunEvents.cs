using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Ipc;

/// <summary>
/// Builders for the run-event stream, so a view-model test can stage a plausible walk
/// without spelling out the DTO's eight positional arguments each time.
///
/// Deliberately dumb: no state, no ordering rules. The point of these tests is that the
/// PANEL survives whatever the daemon sends — including a node exit whose entry was dropped,
/// or an event for a walk it never saw start — so the helper must not quietly fix things up.
/// </summary>
internal static class RunEvents
{
    /// <summary>A walk descriptor. <paramref name="hwnd"/> of 0 means "no context window".</summary>
    public static RunWalkDto Walk(string macroName, long hwnd = 0, Guid? runId = null, int depth = 0) =>
        new(Guid.NewGuid(), runId ?? Guid.NewGuid(), macroName, hwnd, depth, DateTimeOffset.UtcNow, FromStart: true);

    public static RunEventDto Started(RunWalkDto walk) =>
        new(walk.WalkId, RunEventKind.WalkStarted, ElapsedMs: 0, Walk: walk);

    public static RunEventDto Entered(RunWalkDto walk, int elapsedMs, string nodeId) =>
        new(walk.WalkId, RunEventKind.NodeEntered, elapsedMs, nodeId);

    public static RunEventDto Exited(RunWalkDto walk, int elapsedMs, string nodeId, string outcome, string? detail, int durationMs) =>
        new(walk.WalkId, RunEventKind.NodeExited, elapsedMs, nodeId, outcome, detail, durationMs);

    public static RunEventDto Finished(RunWalkDto walk, int elapsedMs, string outcome, string? detail = null) =>
        new(walk.WalkId, RunEventKind.WalkFinished, elapsedMs, Outcome: outcome, Detail: detail);

    // ---- debugger (D5) ----------------------------------------------------------------

    /// <summary>Parked at a breakpoint. <paramref name="reason"/> is the daemon's Russian word.</summary>
    public static RunEventDto Breakpoint(RunWalkDto walk, int elapsedMs, string nodeId) =>
        new(walk.WalkId, RunEventKind.BreakpointHit, elapsedMs, nodeId, Detail: "брейкпоинт");

    /// <summary>Parked for any other reason.</summary>
    public static RunEventDto Paused(RunWalkDto walk, int elapsedMs, string nodeId, string reason = "пауза") =>
        new(walk.WalkId, RunEventKind.Paused, elapsedMs, nodeId, Detail: reason);

    public static RunEventDto Resumed(RunWalkDto walk, int elapsedMs, string nodeId) =>
        new(walk.WalkId, RunEventKind.Resumed, elapsedMs, nodeId);

    /// <summary>A variable assignment. <paramref name="nodeId"/> is <c>null</c> for the trigger seed.</summary>
    public static RunEventDto Variable(RunWalkDto walk, int elapsedMs, string name, string value, string? nodeId = null) =>
        new(walk.WalkId, RunEventKind.VariableSet, elapsedMs, nodeId, Detail: value, Variable: name);
}

/// <summary>Pushing run events at a view-model the way the daemon's pump does — in batches.</summary>
internal static class FakeIpcClientRunExtensions
{
    /// <summary>Delivers one batch. Goes through <c>IpcJson</c>, so the enum round trip is real.</summary>
    public static void Push(this FakeIpcClient client, params RunEventDto[] events)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.RaiseEvent(IpcMessageTypes.RunEvents, new RunEventBatch(events, Dropped: 0));
    }
}
