using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Agents;

public abstract record AgentMessage;

// Orchestrator → CharacterAgent
public sealed record ModeChangedMessage(AgentMode Mode) : AgentMessage;
public sealed record ExecuteActionMessage(AgentAction Action) : AgentMessage;
public sealed record ShutdownMessage : AgentMessage;

// Per-character action intents. The orchestrator expresses *what* the squad should do
// (use immunity / burst / damage / etc.); each agent looks up the corresponding key on
// its own Character record and presses it on its window. Unidentified agents have empty
// key fields and skip the action.
public sealed record UseImmunityMessage : AgentMessage;

// Click at the given client-area coordinates of the agent's window. Coords are produced
// once by the orchestrator (cursor pos → foreground window client coords) and reused
// across every agent — relies on all PW clients being the same window size.
public sealed record ClickAtMessage(int X, int Y, bool DoubleClick) : AgentMessage;

// CharacterAgent → Orchestrator
public sealed record StatusUpdateMessage(string CharacterName, Coordinates Position) : AgentMessage;
public sealed record StuckDetectedMessage(string CharacterName, Coordinates LastPosition) : AgentMessage;

// Lifecycle notifications — sent by the agent into the orchestrator's inbox so all
// agent→orchestrator communication goes through one async path. The agent ref lets the
// orchestrator look up its own tracking entry without a separate id field.
public sealed record AgentIdentifiedMessage(CharacterAgent Agent, string OldName) : AgentMessage;
public sealed record AgentStoppingMessage(CharacterAgent Agent) : AgentMessage;
