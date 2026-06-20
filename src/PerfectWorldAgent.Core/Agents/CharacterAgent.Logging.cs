using Microsoft.Extensions.Logging;

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

    [LoggerMessage(LogLevel.Error, "Agent run loop failed unexpectedly")]
    partial void LogRunFailed(Exception ex);

    [LoggerMessage(LogLevel.Debug, "Inbox: '{Name}' received {MessageType}")]
    partial void LogInboxReceived(string messageType, string name);

    [LoggerMessage(LogLevel.Information, "{Action} not configured for '{Name}' — skipping silently")]
    partial void LogActionKeyEmpty(string action, string name);

    [LoggerMessage(LogLevel.Debug, "Assist skipped for '{Name}' — IsMaster=true")]
    partial void LogAssistSkippedMaster(string name);

    [LoggerMessage(LogLevel.Warning, "Character {Action} = '{KeyString}' does not parse as a known VirtualKey; skipping")]
    partial void LogUnknownActionKey(string action, string keyString);

    [LoggerMessage(LogLevel.Debug, "AssistKey not set on character; party-member-1 selected but no /assist macro fired")]
    partial void LogAssistKeyMissing();

    [LoggerMessage(LogLevel.Information, "Combat loop started for '{Name}' (macro='{MacroName}', window=10s)")]
    partial void LogCombatLoopStarted(string name, string macroName);

    [LoggerMessage(LogLevel.Information, "Combat loop stopped for '{Name}'")]
    partial void LogCombatLoopStopped(string name);

    [LoggerMessage(LogLevel.Warning, "Combat macro '{MacroName}' not found in library for '{Name}'; agent will hold InCombat without firing keys")]
    partial void LogCombatMacroMissing(string name, string macroName);

    [LoggerMessage(LogLevel.Error, "Combat loop failed unexpectedly")]
    partial void LogCombatLoopFailed(Exception ex);
}
