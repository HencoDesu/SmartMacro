namespace SmartMacro.Agents;

/// <summary>
/// Base of the agent → orchestrator notification channel. What used to be a two-way
/// command bus is now one-way: every command became a macro run against window handles,
/// so the only traffic left is lifecycle.
/// </summary>
public abstract record AgentMessage;

/// <summary>
/// The agent's run loop has exited (its window died, or the app is shutting down). The
/// orchestrator drops it from the tracked set and notifies the UI. The agent reference
/// doubles as the identity key — no separate id field needed.
/// </summary>
public sealed record AgentStoppingMessage(CharacterAgent Agent) : AgentMessage;
