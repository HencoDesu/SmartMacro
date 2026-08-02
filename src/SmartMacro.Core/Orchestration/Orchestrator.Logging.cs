using Microsoft.Extensions.Logging;

namespace SmartMacro.Orchestration;

// Source-generated LoggerMessage declarations for Orchestrator. Split into its own
// partial-class file so the main Orchestrator.cs reads as trigger→run logic, not logger
// boilerplate. No behavior change.
public sealed partial class Orchestrator
{
    [LoggerMessage(LogLevel.Information, "Orchestrator dispatch loop started")]
    partial void LogDispatchLoopStarted();

    [LoggerMessage(LogLevel.Information, "Orchestrator dispatch loop stopped")]
    partial void LogDispatchLoopStopped();

    [LoggerMessage(LogLevel.Debug, "Unhandled upstream message {Type}")]
    partial void LogUnhandledUpstreamMessageType(string type);

    [LoggerMessage(LogLevel.Information, "Process appeared (from monitor): pid={Pid} name='{ProcessName}' hwnd=0x{Hwnd:X}")]
    partial void LogProcessAppearedNotification(int pid, string processName, long hwnd);

    [LoggerMessage(LogLevel.Error, "Failed to create agent for pid={Pid}")]
    partial void LogAgentCreationFailed(Exception ex, int pid);

    [LoggerMessage(LogLevel.Information, "Hotkey trigger → macro '{MacroName}'")]
    partial void LogHotkeyTriggered(string macroName);

    [LoggerMessage(LogLevel.Information, "Process '{ProcessName}' appeared → starting macro '{MacroName}' on hwnd=0x{Hwnd:X}")]
    partial void LogProcessMacroStarting(string macroName, string processName, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Macro '{MacroName}' is not in the library — trigger dropped")]
    partial void LogMacroNotFound(string macroName);

    [LoggerMessage(LogLevel.Warning, "Macro '{MacroName}' aborted: {Reason}")]
    partial void LogMacroAborted(string macroName, string reason);

    [LoggerMessage(LogLevel.Error, "Macro '{MacroName}' failed unexpectedly")]
    partial void LogMacroFailed(Exception ex, string macroName);
}
