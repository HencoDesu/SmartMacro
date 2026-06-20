using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Vision;

namespace PerfectWorldAgent.Agents;

// Source-generated LoggerMessage declarations for CharacterAgent. Split into its own
// partial-class file so the main CharacterAgent.cs reads as agent logic, not 20+ logger
// boilerplate methods. No behavior change — the generator emits the same code as if these
// methods lived in the main file.
public sealed partial class CharacterAgent
{
    [LoggerMessage(LogLevel.Information, "Agent '{Name}' run loop started (state={State}, poll={Poll})")]
    partial void LogStarted(string name, AgentState state, TimeSpan poll);

    [LoggerMessage(LogLevel.Information, "Agent '{Name}' run loop stopped")]
    partial void LogStopped(string name);

    [LoggerMessage(LogLevel.Information, "Agent promoted: '{OldName}' -> '{NewName}'")]
    partial void LogPromoted(string oldName, string newName);

    [LoggerMessage(LogLevel.Information, "Agent window no longer alive; exiting run loop")]
    partial void LogWindowGone();

    [LoggerMessage(LogLevel.Debug, "State transition: {Source} -> {Destination} (trigger {Trigger})")]
    partial void LogStateTransition(AgentState source, AgentState destination, AgentTrigger trigger);

    [LoggerMessage(LogLevel.Warning, "Unhandled trigger {Trigger} in state {State}")]
    partial void LogUnhandledTrigger(AgentTrigger trigger, AgentState state);

    [LoggerMessage(LogLevel.Information, "Identification attempt started for '{Name}' — opening stats")]
    partial void LogIdentifyAttemptStarted(string name);

    [LoggerMessage(LogLevel.Information, "Identification for '{Name}' returned no match — stats window may not have rendered, class template may be missing, or class not yet registered in roster")]
    partial void LogIdentifyNoMatch(string name);

    [LoggerMessage(LogLevel.Error, "Identification failed for agent")]
    partial void LogIdentifyFailed(Exception ex);

    [LoggerMessage(LogLevel.Debug, "Operation '{Operation}' already running on '{Name}' — concurrent trigger ignored")]
    partial void LogOperationAlreadyRunning(string name, string operation);

    [LoggerMessage(LogLevel.Information, "Boot flow starting on '{Name}' — vision-driven phase polling")]
    partial void LogBootFlowStarting(string name);

    [LoggerMessage(LogLevel.Information, "Boot flow finished on '{Name}' — in-world detected")]
    partial void LogBootFlowFinished(string name);

    [LoggerMessage(LogLevel.Information, "Boot click on '{Name}': {Label} at {Point}")]
    partial void LogBootClick(string name, string label, ScreenPoint point);

    [LoggerMessage(LogLevel.Information, "Boot skipped on '{Name}' — already in-world; going straight to identify")]
    partial void LogBootSkippedAlreadyInWorld(string name);

    [LoggerMessage(LogLevel.Information, "Boot waiting on '{Name}' for phase {Phase} to become ready…")]
    partial void LogBootWaitingForPhase(string name, BootPhase phase);

    [LoggerMessage(LogLevel.Information, "Boot phase {Phase} ready on '{Name}'")]
    partial void LogBootPhaseReady(string name, BootPhase phase);

    [LoggerMessage(LogLevel.Warning, "Boot phase {Phase} timed out after {Seconds}s on '{Name}' — screen never rendered; aborting boot")]
    partial void LogBootPhaseTimeout(string name, BootPhase phase, int seconds);

    [LoggerMessage(LogLevel.Warning, "Boot template '{Template}' for phase {Phase} not found in GameUiElementLoader on '{Name}' — check Assets/GameUiElements/{Template}.png; aborting boot")]
    partial void LogBootTemplateMissing(string name, BootPhase phase, string template);

    [LoggerMessage(LogLevel.Error, "Boot flow failed for agent")]
    partial void LogBootFlowFailed(Exception ex);

    [LoggerMessage(LogLevel.Error, "Agent run loop failed unexpectedly")]
    partial void LogRunFailed(Exception ex);

    [LoggerMessage(LogLevel.Debug, "Inbox: '{Name}' received {MessageType}")]
    partial void LogInboxReceived(string messageType, string name);

    [LoggerMessage(LogLevel.Information, "{Action} not configured for '{Name}' — skipping silently")]
    partial void LogActionKeyEmpty(string action, string name);

    [LoggerMessage(LogLevel.Warning, "{Action} key string '{KeyString}' does not parse as a known VirtualKey; skipping")]
    partial void LogUnknownActionKey(string action, string keyString);

    [LoggerMessage(LogLevel.Debug, "Assist skipped for '{Name}' — IsMaster")]
    partial void LogAssistSkippedMaster(string name);

    [LoggerMessage(LogLevel.Debug, "Broadcast {MessageType} ignored by '{Name}' — class {Cls} is in IgnoredClasses")]
    partial void LogBroadcastIgnored(string messageType, string name, CharacterClass cls);

    [LoggerMessage(LogLevel.Information, "Combat loop started for '{Name}' (macro='{MacroName}', window=10s)")]
    partial void LogCombatLoopStarted(string name, string macroName);

    [LoggerMessage(LogLevel.Information, "Combat loop stopped for '{Name}'")]
    partial void LogCombatLoopStopped(string name);

    [LoggerMessage(LogLevel.Warning, "Combat macro '{MacroName}' not found in library for '{Name}'; agent will hold InCombat without firing keys")]
    partial void LogCombatMacroMissing(string name, string macroName);

    [LoggerMessage(LogLevel.Error, "Combat loop failed unexpectedly")]
    partial void LogCombatLoopFailed(Exception ex);
}
