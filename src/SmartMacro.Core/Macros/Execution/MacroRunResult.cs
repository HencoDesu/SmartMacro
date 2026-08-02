namespace SmartMacro.Macros.Execution;

/// <summary>How a macro run ended.</summary>
public enum MacroRunStatus
{
    /// <summary>The walk reached a <c>null</c> edge — a clean end.</summary>
    Completed,

    /// <summary>The run hit an error (missing variable, no context window, cycle, depth limit, …). See <see cref="MacroRunResult.Error"/>.</summary>
    Aborted,

    /// <summary>The run was cancelled (Stop from UI, shutdown). Silent — not an error.</summary>
    Cancelled,
}

/// <summary>Outcome of one <see cref="MacroExecutor.RunAsync"/> call.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Error">Human-readable failure description; non-null only when <paramref name="Status"/> is <see cref="MacroRunStatus.Aborted"/>.</param>
public sealed record MacroRunResult(MacroRunStatus Status, string? Error = null)
{
    /// <summary>Shared "clean end" result.</summary>
    public static MacroRunResult Completed { get; } = new(MacroRunStatus.Completed);

    /// <summary>Shared "cancelled" result.</summary>
    public static MacroRunResult Cancelled { get; } = new(MacroRunStatus.Cancelled);

    /// <summary>Builds an aborted result carrying the failure description.</summary>
    public static MacroRunResult Aborted(string error) => new(MacroRunStatus.Aborted, error);
}
