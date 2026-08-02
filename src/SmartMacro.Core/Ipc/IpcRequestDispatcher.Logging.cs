using Microsoft.Extensions.Logging;

namespace SmartMacro.Ipc;

public sealed partial class IpcRequestDispatcher
{
    [LoggerMessage(LogLevel.Warning, "IPC request '{Type}' (#{Id}) rejected: {Reason}")]
    partial void LogRejected(string type, int id, string reason);

    [LoggerMessage(LogLevel.Error, "IPC handler for '{Type}' (#{Id}) threw")]
    partial void LogHandlerFailed(Exception ex, string type, int id);

    [LoggerMessage(LogLevel.Warning, "IPC request of unknown type '{Type}' (#{Id}) — client newer than daemon?")]
    partial void LogUnknownType(string type, int id);

    [LoggerMessage(LogLevel.Information, "SaveMacro '{Macro}' rejected — {ErrorCount} validation error(s), nothing written")]
    partial void LogSaveRejected(string macro, int errorCount);

    [LoggerMessage(LogLevel.Information, "Shutdown requested over IPC — stopping the host after the reply is flushed")]
    partial void LogShutdownRequested();
}
