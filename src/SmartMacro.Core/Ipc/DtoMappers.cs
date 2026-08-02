using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Execution;
using SmartMacro.Windows;

namespace SmartMacro.Ipc;

/// <summary>
/// Projects Core's live domain objects onto the Contracts wire DTOs.
///
/// These live in Core rather than next to the DTOs because the dependency only runs one
/// way: Contracts cannot see <see cref="ManagedWindowInfo"/> or
/// <see cref="MacroRunSnapshot"/>. (The <c>ValidationIssue</c> mapper is the exception —
/// both of its ends are Contracts types, so it lives there.)
///
/// Nothing consumes this yet; the Stage 2 IPC server is its first caller.
/// </summary>
public static class DtoMappers
{
    /// <summary>Projects a window snapshot onto its wire form.</summary>
    public static WindowDto ToDto(this ManagedWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new WindowDto(window.Hwnd.ToInt64(), window.ProcessName, [.. window.Tags]);
    }

    /// <summary>Projects a sequence of window snapshots onto their wire form.</summary>
    public static IReadOnlyList<WindowDto> ToDto(this IEnumerable<ManagedWindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return [.. windows.Select(window => window.ToDto())];
    }

    /// <summary>Projects a run snapshot onto its wire form.</summary>
    public static RunningMacroDto ToDto(this MacroRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunningMacroDto(run.RunId, run.MacroName, ToUtcOffset(run.StartedUtc), run.CurrentNodeId);
    }

    /// <summary>Projects a sequence of run snapshots onto their wire form.</summary>
    public static IReadOnlyList<RunningMacroDto> ToDto(this IEnumerable<MacroRunSnapshot> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return [.. runs.Select(run => run.ToDto())];
    }

    /// <summary>Projects a live run handle onto its wire form (same shape as the snapshot).</summary>
    public static RunningMacroDto ToDto(this MacroRunHandle run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunningMacroDto(run.RunId, run.MacroName, ToUtcOffset(run.StartedUtc), run.CurrentNodeId);
    }

    // The registry stamps DateTime.UtcNow, so the value IS UTC — but a DateTime that got
    // there through a round-trip can carry Kind.Unspecified, and DateTimeOffset would then
    // apply the LOCAL offset and silently shift the timestamp. Pin the kind first.
    private static DateTimeOffset ToUtcOffset(DateTime startedUtc) =>
        new(DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc));
}
