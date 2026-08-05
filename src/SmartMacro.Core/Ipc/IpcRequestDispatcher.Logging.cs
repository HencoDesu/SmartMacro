using Microsoft.Extensions.Logging;

namespace SmartMacro.Ipc;

public sealed partial class IpcRequestDispatcher
{
    [LoggerMessage(LogLevel.Warning, "Запрос IPC '{Type}' (#{Id}) отклонён: {Reason}")]
    partial void LogRejected(string type, int id, string reason);

    [LoggerMessage(LogLevel.Error, "Обработчик IPC для '{Type}' (#{Id}) бросил исключение")]
    partial void LogHandlerFailed(Exception ex, string type, int id);

    [LoggerMessage(LogLevel.Warning, "Запрос IPC неизвестного типа '{Type}' (#{Id}) — клиент новее демона?")]
    partial void LogUnknownType(string type, int id);

    [LoggerMessage(LogLevel.Information, "По IPC запрошено выключение — останавливаем хост, как только ответ уйдёт")]
    partial void LogShutdownRequested();
}
