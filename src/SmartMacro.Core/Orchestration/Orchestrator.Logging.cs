using Microsoft.Extensions.Logging;

namespace SmartMacro.Orchestration;

// Source-generated LoggerMessage declarations for Orchestrator. Split into its own
// partial-class file so the main Orchestrator.cs reads as dispatcher logic, not 15+
// logger boilerplate methods. No behavior change.
public sealed partial class Orchestrator
{
    [LoggerMessage(LogLevel.Information, "Orchestrator dispatch loop started")]
    partial void LogDispatchLoopStarted();

    [LoggerMessage(LogLevel.Debug, "Unhandled upstream message {Type}")]
    partial void LogUnhandledUpstreamMessageType(string type);

    [LoggerMessage(LogLevel.Information, "Process appeared (from monitor): pid={Pid} name='{ProcessName}' hwnd=0x{Hwnd:X}")]
    partial void LogProcessAppearedNotification(int pid, string processName, long hwnd);

    [LoggerMessage(LogLevel.Information, "Orchestrator dispatch loop stopped")]
    partial void LogDispatchLoopStopped();

    [LoggerMessage(LogLevel.Error, "Failed to create agent for pid={Pid}")]
    partial void LogAgentCreationFailed(Exception ex, int pid);

    [LoggerMessage(LogLevel.Information, "Agent promoted: '{OldName}' -> '{NewName}'")]
    partial void LogAgentIdentified(string oldName, string newName);

    [LoggerMessage(LogLevel.Information, "BroadcastImmunity hotkey pressed — broadcasting UseImmunityMessage to all agents")]
    partial void LogBroadcastImmunity();

    [LoggerMessage(LogLevel.Information, "BroadcastAssist hotkey pressed — broadcasting TakeAssistMessage to non-master agents")]
    partial void LogBroadcastAssist();

    [LoggerMessage(LogLevel.Information, "BroadcastMacro requested — broadcasting RunMacroMessage('{MacroName}') to all agents")]
    partial void LogBroadcastMacro(string macroName);

    [LoggerMessage(LogLevel.Information, "BroadcastIdentify hotkey pressed — broadcasting EnterIdentifyMessage to all agents")]
    partial void LogBroadcastIdentify();

    [LoggerMessage(LogLevel.Information, "Broadcast {MessageType} starting → {Count} agent(s): [{Recipients}]")]
    partial void LogBroadcastStarting(string messageType, int count, string recipients);

    [LoggerMessage(LogLevel.Information, "Broadcast {MessageType} delivered to {Delivered}/{Total} agent(s)")]
    partial void LogBroadcastDelivered(string messageType, int delivered, int total);

    [LoggerMessage(LogLevel.Warning, "Broadcast {MessageType} skipped — no live agents")]
    partial void LogBroadcastEmpty(string messageType);

    [LoggerMessage(LogLevel.Warning, "Broadcast {MessageType} dropped for '{Agent}' — channel closed (agent stopped mid-broadcast)")]
    partial void LogBroadcastChannelClosed(string messageType, string agent);
}
