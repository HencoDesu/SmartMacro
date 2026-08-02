using Microsoft.Extensions.Logging;

namespace SmartMacro.Ipc;

public sealed partial class IpcServer
{
    [LoggerMessage(LogLevel.Information, "IPC server listening on named pipe '{PipeName}' ({MaxInstances} instances)")]
    partial void LogListening(string pipeName, int maxInstances);

    [LoggerMessage(LogLevel.Information, "IPC server stopped")]
    partial void LogStopped();

    [LoggerMessage(LogLevel.Warning, "IPC accept loop did not stop within the drain timeout")]
    partial void LogAcceptLoopDidNotStop();

    [LoggerMessage(LogLevel.Warning, "IPC: could not open a listening instance of '{PipeName}' — retrying")]
    partial void LogAcceptFailed(Exception ex, string pipeName);

    [LoggerMessage(LogLevel.Information, "IPC client connected ({Count} total)")]
    partial void LogClientConnected(int count);

    [LoggerMessage(LogLevel.Information, "IPC client disconnected ({Count} remaining)")]
    partial void LogClientDisconnected(int count);

    [LoggerMessage(LogLevel.Warning, "IPC connection faulted outside the read loop")]
    partial void LogConnectionFaulted(Exception ex);

    [LoggerMessage(LogLevel.Warning, "IPC: unparseable line from a client — skipped")]
    partial void LogMalformedLine(Exception ex);

    [LoggerMessage(LogLevel.Debug, "IPC: read from a client failed — closing the connection")]
    partial void LogReadFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "IPC: failed to write the reply to '{Type}' (#{Id}) — dropping the connection")]
    partial void LogResponseWriteFailed(Exception ex, string type, int id);

    [LoggerMessage(LogLevel.Warning, "IPC: failed to push an event — dropping the connection")]
    partial void LogEventWriteFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "IPC client is not draining events ({Capacity} queued, dropped at '{Type}') — closing it; the panel will reconnect and re-fetch")]
    partial void LogClientBacklogged(string type, int capacity);

    [LoggerMessage(LogLevel.Warning, "IPC: {Count} client connection(s) still open after the drain timeout")]
    partial void LogClientsDidNotDrain(int count);
}
