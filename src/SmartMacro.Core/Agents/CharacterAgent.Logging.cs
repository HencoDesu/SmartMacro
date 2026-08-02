using Microsoft.Extensions.Logging;

namespace SmartMacro.Agents;

// Source-generated LoggerMessage declarations for CharacterAgent. Kept in its own
// partial-class file for symmetry with the rest of the codebase, even though the agent
// shrank to a window-lifetime shell in W0.2b.
public sealed partial class CharacterAgent
{
    [LoggerMessage(LogLevel.Information, "Agent hwnd=0x{Hwnd:X} watching its window (poll={Poll})")]
    partial void LogStarted(long hwnd, TimeSpan poll);

    [LoggerMessage(LogLevel.Information, "Agent hwnd=0x{Hwnd:X} run loop stopped")]
    partial void LogStopped(long hwnd);

    [LoggerMessage(LogLevel.Information, "Agent window no longer alive; exiting run loop")]
    partial void LogWindowGone();

    [LoggerMessage(LogLevel.Error, "Agent run loop failed unexpectedly")]
    partial void LogRunFailed(Exception ex);
}
