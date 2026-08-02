using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;
using SmartMacro.Native;

namespace SmartMacro.Input;

// Wraps the activation lifecycle around every input send to a game window. The macro
// primitives layer delegates here for any keypress / click — keeps the walker focused on
// graph traversal, off the concerns of "wake the window, send, drain the queue, put it
// back to sleep".
//
// All methods follow the same shape:
//   1. ActivateAsync (wake the frozen background window)
//   2. send the input via PostMessage primitives
//   3. DeactivateAsync (drain delay + put the window back to sleep, unless foreground)
//
// Exception-safe: try/finally guarantees Deactivate runs even if the inner send throws,
// and send failures are logged rather than thrown — one unreachable window must not abort
// a macro that legitimately targeted eight others.
//
// Logging is structured around the action label the caller passes ("Key(F8)", "Click") so
// the operator can grep log lines for a specific action.
public sealed partial class AgentInputDispatcher
{
    private readonly ILogger<AgentInputDispatcher> _logger;

    public AgentInputDispatcher(ILogger<AgentInputDispatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>Single-key send, wrapped in one activation cycle.</summary>
    /// <param name="actionName">Semantic label for logging ("Key(F8)") — purely diagnostic, doesn't affect behavior.</param>
    /// <param name="agentName">Window label used in log messages so the operator can grep per-window activity.</param>
    public async Task FireKeyAsync(IGameWindow window, VirtualKey key, string actionName, string agentName)
    {
        try
        {
            LogActionFiring(actionName, key, agentName);
            await window.ActivateAsync().ConfigureAwait(false);
            try
            {
                await window.PressKeyAsync(key).ConfigureAwait(false);
            }
            finally
            {
                await window.DeactivateAsync().ConfigureAwait(false);
            }
            LogActionFired(actionName, key, agentName);
        }
        catch (Exception ex)
        {
            LogSendKeyFailed(ex, key, agentName);
        }
    }

    /// <summary>
    /// Single left/double click at a client-space point. Multi-boxing setups reuse one
    /// point across identically-sized clients, which is what makes a cursor-broadcast
    /// macro work.
    /// </summary>
    public async Task FireClickAsync(IGameWindow window, ScreenPoint point, bool doubleClick, string agentName)
    {
        try
        {
            await window.ActivateAsync().ConfigureAwait(false);
            try
            {
                if (doubleClick)
                {
                    await window.DoubleClickAsync(point).ConfigureAwait(false);
                }
                else
                {
                    await window.ClickAsync(point).ConfigureAwait(false);
                }
            }
            finally
            {
                await window.DeactivateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogSendClickFailed(ex, point, doubleClick, agentName);
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Firing {Action}={Key} on '{Name}'")]
    partial void LogActionFiring(string action, VirtualKey key, string name);

    [LoggerMessage(LogLevel.Information, "Fired {Action}={Key} on '{Name}' OK")]
    partial void LogActionFired(string action, VirtualKey key, string name);

    [LoggerMessage(LogLevel.Error, "PressKeyAsync({Key}) failed on '{Name}'")]
    partial void LogSendKeyFailed(Exception ex, VirtualKey key, string name);

    [LoggerMessage(LogLevel.Error, "Click {Point} doubleClick={DoubleClick} failed on '{Name}'")]
    partial void LogSendClickFailed(Exception ex, ScreenPoint point, bool doubleClick, string name);

    #endregion
}
