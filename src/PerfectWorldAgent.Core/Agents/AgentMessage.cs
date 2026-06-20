using PerfectWorldAgent.Models;
using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.Agents;

public abstract record AgentMessage;

// Orchestrator → CharacterAgent — broadcast action intents.

// Per-character intent: agent looks up its own Character.ImmunityKey and presses it.
// Unidentified agents skip silently. Bound in-game to a 10s damage-immunity skill.
public sealed record UseImmunityMessage : AgentMessage;

// "Enter combat" — each agent fires the SetCombat state-machine trigger and runs its
// CombatMacro for up to 10 seconds. Master included (per design — master may have its
// own macro too, e.g. group buffs).
public sealed record EnterCombatMessage : AgentMessage;

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
