using SmartMacro.Native;

namespace SmartMacro.Agents;

public abstract record AgentMessage;

// Orchestrator → CharacterAgent — broadcast action intents.

// Per-window intent: agent presses the shared DefaultImmunityKey binding. Untagged
// agents skip silently. Bound in-game to a 10s damage-immunity skill.
public sealed record UseImmunityMessage : AgentMessage;

// "Run this named macro" — broadcast by the Macros dialog's per-row Run button.
// Each agent (excluding windows carrying an IgnoredTags tag) looks up the macro by name
// in MacroLibrary, picks the action list whose tag key its window carries, and runs it
// fire-and-forget. Concurrent runs are guarded by the agent's _operationLock so a second
// broadcast of the same macro on a still-running agent silently no-ops.
public sealed record RunMacroMessage(string MacroName) : AgentMessage;

// "Identify yourself" — fired on the BroadcastIdentify hotkey. Each untagged agent opens
// the in-game stats window (press C), waits, captures a screenshot, runs ClassMatcher
// against the tag templates, closes stats, and (on a hit) promotes itself via
// Identify(tag). Already-tagged agents ignore it.
public sealed record EnterIdentifyMessage : AgentMessage;

// "Take assist from the master". Agent clicks party-slot-1 (selects the master) then
// presses the shared DefaultAssistKey (in-game macro bound to /assist current target →
// ends up targeting whatever master targets). The window carrying MasterTag skips this
// message (no one to assist).
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
