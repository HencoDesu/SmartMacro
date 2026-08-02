using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.GameWindows;
using SmartMacro.Native;

namespace SmartMacro.Input;

// Wraps the activation lifecycle around every input send to a game window. CharacterAgent
// delegates here for any keypress / chord / click — keeps the agent focused on routing
// inbox messages and lifecycle, off the concerns of "wake the window, send, drain the
// queue, put it back to sleep".
//
// All methods follow the same shape:
//   1. ActivateAsync (wake the frozen background window)
//   2. send the input via PostMessage primitives
//   3. DeactivateAsync (drain delay + put the window back to sleep, unless foreground)
//
// Exception-safe: try/finally guarantees Deactivate runs even if the inner send throws.
// Logging is structured around the action label the caller passes ("ImmunityKey",
// "AssistKey", "Click") so the operator can grep log lines for a specific action.
public sealed partial class AgentInputDispatcher
{
    private readonly ILogger<AgentInputDispatcher> _logger;
    private readonly int _interStepDelayMs;
    private readonly ScreenPoint _partySlot1;

    public AgentInputDispatcher(IOptions<ActivatingInputOptions> options, ILogger<AgentInputDispatcher> logger)
    {
        _interStepDelayMs = options.Value.InterStepDelayMs;
        _partySlot1 = options.Value.PartySlot1;
        _logger = logger;
    }

    /// <summary>
    /// Single-key send (used for immunity broadcasts and any per-character action key).
    /// </summary>
    /// <param name="actionName">Semantic label for logging ("ImmunityKey" etc.) — purely diagnostic, doesn't affect behavior.</param>
    /// <param name="agentName">Agent name used in log messages so the operator can grep per-agent activity.</param>
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
    /// Two-step assist: click party-slot 1 (master portrait) to select master, then
    /// <paramref name="assistKey"/> fires the in-game /assist macro. Both happen inside ONE
    /// activation cycle — window stays active between the click and the follow-up so PW
    /// doesn't drop the selection on intermediate deactivation. Gap between click and
    /// follow-up uses configured <see cref="ActivatingInputOptions.InterStepDelayMs"/>
    /// (200ms default — tune in appsettings.json).
    /// </summary>
    /// <remarks>
    /// Previously used Shift+1 chord but PW reads modifier state via GetKeyState — and a
    /// cross-thread SendMessage does NOT update that state. The click is unaffected by
    /// modifier-state quirks; mouse messages have always worked reliably for us.
    /// Coordinates come from <see cref="ActivatingInputOptions.PartySlot1X"/>/<see cref="ActivatingInputOptions.PartySlot1Y"/>.
    /// </remarks>
    public async Task FireAssistAsync(IGameWindow window, VirtualKey assistKey, string agentName)
    {
        try
        {
            LogAssistStarting(agentName, assistKey);
            await window.ActivateAsync().ConfigureAwait(false);
            try
            {
                LogAssistSlotClickStart(agentName, _partySlot1);
                await window.ClickAsync(_partySlot1).ConfigureAwait(false);
                LogAssistSlotClickDone(agentName);
                await Task.Delay(_interStepDelayMs).ConfigureAwait(false);
                await window.PressKeyAsync(assistKey).ConfigureAwait(false);
                LogAssistKeyDone(agentName, assistKey);
            }
            finally
            {
                await window.DeactivateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogAssistFailed(ex, agentName);
        }
    }

    /// <summary>
    /// Click broadcast — point comes pre-translated from the orchestrator (foreground
    /// window's client space → reused verbatim per agent assuming identical client sizes).
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

    [LoggerMessage(LogLevel.Error, "Assist sequence (slot click + key) failed on '{Name}'")]
    partial void LogAssistFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Information, "Assist starting on '{Name}' (assistKey={Key})")]
    partial void LogAssistStarting(string name, VirtualKey key);

    [LoggerMessage(LogLevel.Information, "Assist slot-click start on '{Name}' at {Point}")]
    partial void LogAssistSlotClickStart(string name, ScreenPoint point);

    [LoggerMessage(LogLevel.Information, "Assist slot-click done on '{Name}'")]
    partial void LogAssistSlotClickDone(string name);

    [LoggerMessage(LogLevel.Information, "Assist key done on '{Name}' (key={Key})")]
    partial void LogAssistKeyDone(string name, VirtualKey key);

    [LoggerMessage(LogLevel.Error, "Click {Point} doubleClick={DoubleClick} failed on '{Name}'")]
    partial void LogSendClickFailed(Exception ex, ScreenPoint point, bool doubleClick, string name);

    #endregion
}
