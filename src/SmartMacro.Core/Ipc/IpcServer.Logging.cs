using Microsoft.Extensions.Logging;

namespace SmartMacro.Ipc;

public sealed partial class IpcServer
{
    [LoggerMessage(LogLevel.Information, "Сервер IPC слушает named pipe '{PipeName}' (экземпляров: {MaxInstances})")]
    partial void LogListening(string pipeName, int maxInstances);

    [LoggerMessage(LogLevel.Information, "Сервер IPC остановлен")]
    partial void LogStopped();

    [LoggerMessage(LogLevel.Warning, "Цикл приёма IPC не остановился за отведённое на слив время")]
    partial void LogAcceptLoopDidNotStop();

    [LoggerMessage(LogLevel.Warning, "IPC: не удалось открыть слушающий экземпляр '{PipeName}' — повторяем")]
    partial void LogAcceptFailed(Exception ex, string pipeName);

    [LoggerMessage(LogLevel.Information, "Клиент IPC подключился (всего {Count})")]
    partial void LogClientConnected(int count);

    [LoggerMessage(LogLevel.Information, "Клиент IPC отключился (осталось {Count})")]
    partial void LogClientDisconnected(int count);

    [LoggerMessage(LogLevel.Warning, "Соединение IPC упало вне цикла чтения")]
    partial void LogConnectionFaulted(Exception ex);

    [LoggerMessage(LogLevel.Warning, "IPC: неразбираемая строка от клиента — пропущена")]
    partial void LogMalformedLine(Exception ex);

    [LoggerMessage(LogLevel.Debug, "IPC: чтение от клиента не удалось — закрываем соединение")]
    partial void LogReadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "IPC: не удалось записать ответ на '{Type}' (#{Id}) — рвём соединение")]
    partial void LogResponseWriteFailed(Exception ex, string type, int id);

    [LoggerMessage(LogLevel.Warning, "IPC: не удалось отправить событие — рвём соединение")]
    partial void LogEventWriteFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning,
        "Клиент IPC не разбирает события (в очереди {Capacity}, сорвались на '{Type}') — закрываем его; панель переподключится и перечитает всё заново")]
    partial void LogClientBacklogged(string type, int capacity);

    [LoggerMessage(LogLevel.Warning, "IPC: после слива всё ещё открыто соединений с клиентами: {Count}")]
    partial void LogClientsDidNotDrain(int count);

    [LoggerMessage(LogLevel.Error,
        "IPC: не удалось вернуть хоткеи после разрыва соединения — глобальные аккорды могли остаться снятыми")]
    partial void LogHotkeyResumeOnDisconnectFailed(Exception ex);
}
