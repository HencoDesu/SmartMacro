using Serilog.Core;
using Serilog.Events;
using SmartMacro.Contracts.Dto;
using SmartMacro.Ipc;

namespace SmartMacro.Daemon.Logging;

/// <summary>
/// Сток Serilog, который отводит журнал демона в <see cref="LogEventPublisher"/>, — третий
/// адресат рядом с консолью и файлом.
///
/// <b>Почему он живёт здесь, а не в Core.</b> Весь <c>SmartMacro.Core</c> логирует через
/// <c>Microsoft.Extensions.Logging</c> (источники <c>[LoggerMessage]</c>) и ссылки на Serilog не
/// имеет — это единственная сборка движка, и тащить в неё конкретную реализацию логирования
/// только ради стока было бы шагом назад. Поэтому граница проведена так: перекладывание
/// <see cref="LogEvent"/> в пять примитивов — работа этого класса, в проекте, где Serilog и так
/// есть, а кольцо, очередь, склейка и рассылка — работа публикатора в Core, где живёт весь
/// остальной IPC.
///
/// <b>Класс намеренно не содержит ни одной строчки логирования</b>, и это первый из трёх срезов
/// защиты от рекурсии (остальные два описаны у <see cref="LogEventPublisher"/>). Сток,
/// пишущий в лог, вызывал бы сам себя из <see cref="Emit"/> — то есть при первой же записи.
/// Здесь брать логгер попросту неоткуда: он конструируется из того самого конвейера Serilog, в
/// который вставлен этот объект.
/// </summary>
internal sealed class IpcLogSink : ILogEventSink
{
    private readonly LogEventPublisher _publisher;

    public IpcLogSink(LogEventPublisher publisher) => _publisher = publisher;

    /// <summary>
    /// Одна запись. Зовётся Serilog синхронно, на потоке того, кто логировал, — а логируют здесь
    /// в том числе обходы макросов между двумя сообщениями живой игре, так что ждать нельзя ни
    /// микросекунды. <see cref="LogEventPublisher.Append"/> это обещание держит.
    /// </summary>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        _publisher.Append(
            logEvent.Timestamp,
            Map(logEvent.Level),
            SourceOf(logEvent),
            // Отрисовка на стороне демона: иначе панели пришлось бы носить с собой Serilog и его
            // форматтер, чтобы собрать ту же строку из шаблона и свойств, — ровно та
            // зависимость, от которой процесс интерфейса избавляла стадия 3. Обоснование —
            // у LogEntryDto.
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());
    }

    /// <summary>
    /// Полное имя типа, который писал, либо <c>null</c>. <c>SourceContext</c> проставляет
    /// <c>ILogger&lt;T&gt;</c>; у записей через статический <c>Log</c> (так пишет <c>Program</c>
    /// на старте) его нет, и выдумывать источник не нужно — панель просто оставит колонку пустой.
    /// </summary>
    private static string? SourceOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
        && value is ScalarValue { Value: string source }
            ? source
            : null;

    // Один в один по членам и по порядку — LogLevelDto ровно затем и объявлен, чтобы Contracts
    // не знали про Serilog. default здесь недостижим при любом значении перечисления Serilog и
    // существует только ради исчерпывающего switch.
    private static LogLevelDto Map(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevelDto.Verbose,
        LogEventLevel.Debug => LogLevelDto.Debug,
        LogEventLevel.Information => LogLevelDto.Information,
        LogEventLevel.Warning => LogLevelDto.Warning,
        LogEventLevel.Error => LogLevelDto.Error,
        LogEventLevel.Fatal => LogLevelDto.Fatal,
        _ => LogLevelDto.Information,
    };
}
