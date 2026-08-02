using SmartMacro.Models;
using SmartMacro.Native;

namespace SmartMacro.Agents;

public abstract record AgentMessage;

// Orchestrator → CharacterAgent — broadcast action intents.

// Per-character intent: agent looks up its own Character.ImmunityKey and presses it.
// Unidentified agents skip silently. Bound in-game to a 10s damage-immunity skill.
public sealed record UseImmunityMessage : AgentMessage;

// "Run this named macro" — broadcast by the Macros dialog's per-row Run button.
// Each agent (including master, excluding IgnoredClasses) looks up the macro by name
// in MacroLibrary and runs its steps fire-and-forget. Concurrent runs are guarded by
// the agent's _operationLock so a second broadcast of the same macro on a still-running
// agent silently no-ops.
public sealed record RunMacroMessage(string MacroName) : AgentMessage;

// "Identify yourself" — fired on the BroadcastIdentify hotkey. Each agent in
// AwaitingIdentification opens the in-game stats window (press C), waits, captures a
// screenshot, runs ClassMatcher, closes stats, and (on a hit) promotes itself via
// Identify(matchedCharacter). Already-identified agents ignore it.
public sealed record EnterIdentifyMessage : AgentMessage;

// "Take assist from the master". Agent sends Shift+1 (PW shortcut: select first party
// member = master) then presses its own Character.AssistKey (in-game macro bound to
// /assist current target → ends up targeting whatever master targets). Master itself
// skips this message (no one to assist).
public sealed record TakeAssistMessage : AgentMessage;

// Click at the given client-area point of the agent's window. Coord is produced once
// by the orchestrator (cursor pos → foreground window client coords) and reused
// across every agent — relies on all PW clients being the same window size.
public sealed record ClickAtMessage(ScreenPoint Point, bool DoubleClick) : AgentMessage;

// CharacterAgent → Orchestrator — lifecycle notifications. Sent into the orchestrator's
// inbox so all agent→orchestrator communication goes through one async path. The agent
// ref lets the orchestrator look up its own tracking entry without a separate id field.
public sealed record AgentIdentifiedMessage(CharacterAgent Agent, string OldName) : AgentMessage;
public sealed record AgentStoppingMessage(CharacterAgent Agent) : AgentMessage;
