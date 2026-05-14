using System.Collections.ObjectModel;
using Avalonia.Threading;
using PerfectWorldAgent.Agents;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.App.ViewModels;

// Bridges Orchestrator's agent lifecycle events to an ObservableCollection the XAML
// list binds to. All mutations to Agents go through Dispatcher.UIThread.Post — the
// orchestrator fires events from arbitrary task threads (HandleProcessAppearedAsync,
// dispatch loop for AgentIdentified/AgentStopped messages), but ObservableCollection
// mutations must happen on the UI thread to keep bindings consistent.
public sealed class MainWindowViewModel : ObservableObject
{
    private readonly Orchestrator _orchestrator;

    public ObservableCollection<AgentRowViewModel> Agents { get; } = [];

    public MainWindowViewModel(Orchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _orchestrator.AgentStarted += OnAgentStarted;
        _orchestrator.AgentIdentified += OnAgentIdentified;
        _orchestrator.AgentStopped += OnAgentStopped;

        // Catch up on agents that already exist — happens when the VM is constructed
        // after the orchestrator has already started (e.g. eager resolution from
        // Program.cs, or whenever the host's hosted-services tick before the UI is up).
        // FindRow guards against double-adding if a Started event raced with the snapshot.
        foreach (var agent in _orchestrator.SnapshotAgents())
        {
            if (FindRow(agent) is null)
            {
                Agents.Add(new AgentRowViewModel(agent));
            }
        }
    }

    private void OnAgentStarted(CharacterAgent agent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FindRow(agent) is null)
            {
                Agents.Add(new AgentRowViewModel(agent));
            }
        });
    }

    private void OnAgentIdentified(CharacterAgent agent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var row = FindRow(agent);
            row?.Refresh();
        });
    }

    private void OnAgentStopped(CharacterAgent agent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var row = FindRow(agent);
            if (row is not null)
            {
                Agents.Remove(row);
            }
        });
    }

    private AgentRowViewModel? FindRow(CharacterAgent agent)
    {
        foreach (var row in Agents)
        {
            if (ReferenceEquals(row.Agent, agent))
            {
                return row;
            }
        }
        return null;
    }
}
