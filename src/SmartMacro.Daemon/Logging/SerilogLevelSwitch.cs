using Serilog.Core;
using Serilog.Events;
using SmartMacro.Contracts.Dto;
using SmartMacro.Settings;

namespace SmartMacro.Daemon.Logging;

/// <summary>
/// Реализация <see cref="ILogLevelSwitch"/> поверх <c>LoggingLevelSwitch</c> из Serilog.
///
/// Живёт в демоне, а не в Core, ровно по той же причине, что и <c>IpcLogSink</c> рядом: <b>у Core
/// нет ссылки на Serilog</b>, и заводить её ради одного перечисления не стоит. Core объявляет
/// шов, демон вставляет в него свой конвейер журналирования.
///
/// Один и тот же <see cref="LoggingLevelSwitch"/> объект отдан и конвейеру Serilog, и сюда,
/// поэтому присвоение действует НЕМЕДЛЕННО и сразу на все стоки: консоль, файл и ленту режима
/// «Лог» — им всем управляет один <c>MinimumLevel.ControlledBy</c>.
/// </summary>
internal sealed class SerilogLevelSwitch : ILogLevelSwitch
{
    private readonly LoggingLevelSwitch _switch;

    public SerilogLevelSwitch(LoggingLevelSwitch levelSwitch) => _switch = levelSwitch;

    /// <inheritdoc />
    public LogLevelDto Current
    {
        get => ToDto(_switch.MinimumLevel);
        set => _switch.MinimumLevel = ToSerilog(value);
    }

    // LogLevelDto повторяет LogEventLevel член в член и по порядку (это записано контрактом у
    // самого перечисления), так что приведение было бы верным. Пишем явно: числовое совпадение
    // двух перечислений из разных сборок — ровно тот вид неявной связи, который однажды тихо
    // разъезжается, а здесь ценой ошибки будет «Debug молча включил Fatal».
    private static LogLevelDto ToDto(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevelDto.Verbose,
        LogEventLevel.Debug => LogLevelDto.Debug,
        LogEventLevel.Information => LogLevelDto.Information,
        LogEventLevel.Warning => LogLevelDto.Warning,
        LogEventLevel.Error => LogLevelDto.Error,
        LogEventLevel.Fatal => LogLevelDto.Fatal,
        _ => LogLevelDto.Information,
    };

    private static LogEventLevel ToSerilog(LogLevelDto level) => level switch
    {
        LogLevelDto.Verbose => LogEventLevel.Verbose,
        LogLevelDto.Debug => LogEventLevel.Debug,
        LogLevelDto.Information => LogEventLevel.Information,
        LogLevelDto.Warning => LogEventLevel.Warning,
        LogLevelDto.Error => LogEventLevel.Error,
        LogLevelDto.Fatal => LogEventLevel.Fatal,
        // Незнакомый уровень от панели новее демона: остаёмся на Information, а не глушим
        // журнал. Правило то же, что у незнакомого исхода прогона, — терять возможность, а не
        // диагностику.
        _ => LogEventLevel.Information,
    };
}
