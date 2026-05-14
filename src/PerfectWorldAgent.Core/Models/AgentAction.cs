namespace PerfectWorldAgent.Models;

public enum AgentActionType
{
    ActivateFollow,
    DeactivateFollow,
    PressBurstBuff,
    PressDamage,
    Jump
}

public sealed record AgentAction(AgentActionType Type);
