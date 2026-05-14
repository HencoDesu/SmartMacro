using PerfectWorldAgent.Agents;

namespace PerfectWorldAgent.App.ViewModels;

// View-model for a single live agent row. Wraps the CharacterAgent reference and
// exposes properties for binding. CharacterAgent itself doesn't notify on changes (its
// state mutations go through messages, not C# events), so Refresh() is called externally
// when the orchestrator reports a relevant change (AgentIdentified). For continuous
// state-machine transitions (HOLD/FOLLOW/COMBAT/Stuck) we'll add a periodic poll or
// agent-side events in a later iteration.
public sealed class AgentRowViewModel : ObservableObject
{
    public CharacterAgent Agent { get; }

    public AgentRowViewModel(CharacterAgent agent)
    {
        Agent = agent;
    }

    public string Name => Agent.Name;
    public string State => Agent.State.ToString();
    public bool IsIdentified => Agent.IsIdentified;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsIdentified));
    }
}
