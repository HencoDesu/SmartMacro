using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Execution;
using SmartMacro.Windows;

namespace SmartMacro.Ipc;

/// <summary>
/// Проецирует живые доменные объекты Core на проводные DTO из Contracts.
///
/// Живут в Core, а не рядом с самими DTO, потому что зависимость идёт только в одну сторону:
/// Contracts не видит ни <see cref="ManagedWindowInfo"/>, ни <see cref="MacroRunSnapshot"/>.
/// (Исключение — маппер <c>ValidationIssue</c>: у него оба конца суть типы Contracts, поэтому
/// он лежит там.)
///
/// Через это место проходит каждая нагрузка про окна и прогоны, которую демон выкладывает в
/// провод: <c>GetWindows</c>, <c>GetRunningMacros</c> и пуши <c>Window*</c> /
/// <c>RunningMacrosChanged</c>.
/// </summary>
public static class DtoMappers
{
    /// <summary>Проецирует снимок окна на его проводную форму.</summary>
    public static WindowDto ToDto(this ManagedWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new WindowDto(window.Hwnd.ToInt64(), window.ProcessName, [.. window.Tags]);
    }

    /// <summary>Проецирует последовательность снимков окон на их проводную форму.</summary>
    public static IReadOnlyList<WindowDto> ToDto(this IEnumerable<ManagedWindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return [.. windows.Select(window => window.ToDto())];
    }

    /// <summary>Проецирует снимок прогона на его проводную форму.</summary>
    public static RunningMacroDto ToDto(this MacroRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunningMacroDto(run.RunId, run.MacroName, ToUtcOffset(run.StartedUtc), run.CurrentNodeName);
    }

    /// <summary>Проецирует последовательность снимков прогонов на их проводную форму.</summary>
    public static IReadOnlyList<RunningMacroDto> ToDto(this IEnumerable<MacroRunSnapshot> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return [.. runs.Select(run => run.ToDto())];
    }

    // Реестр штампует DateTime.UtcNow, так что значение и ЕСТЬ UTC, — но DateTime, попавший
    // туда через round trip, может нести Kind.Unspecified, и тогда DateTimeOffset применил бы
    // МЕСТНОЕ смещение и молча сдвинул отметку времени. Сначала прибиваем вид гвоздями.
    private static DateTimeOffset ToUtcOffset(DateTime startedUtc) =>
        new(DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc));
}
