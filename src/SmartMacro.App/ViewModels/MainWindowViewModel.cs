using System.Collections.ObjectModel;
using Avalonia.Threading;
using SmartMacro.Agents;
using SmartMacro.App.Mvvm;
using SmartMacro.Orchestration;
using SmartMacro.Windows;

namespace SmartMacro.App.ViewModels;

// Bridges Orchestrator's agent set to an ObservableCollection the XAML list binds to.
//
// Two reconcile mechanisms work together:
//
//   1. Event subscriptions (AgentStarted/Stopped) — low-latency UI updates
//      for the normal flow. Mutations are posted to Dispatcher.UIThread because the
//      orchestrator fires events from arbitrary task threads.
//
//   2. Periodic reconcile timer — every 2 seconds, take a fresh snapshot from the
//      orchestrator and diff against the displayed rows. Adds missing, removes stale,
//      refreshes existing. This is the belt-and-suspenders that catches any event we
//      somehow missed (handler exception, dispatcher congestion at startup, race where
//      an agent appears between subscription and snapshot). The cap is 9 agents so the
//      O(n²) diff is trivial. Trade-off: up to 2s lag if events fail entirely, but UI
//      stays correct without anyone having to debug the event plumbing.
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly Orchestrator _orchestrator;
    private readonly WindowRegistry _registry;
    private readonly DispatcherTimer _reconcileTimer;

    public ObservableCollection<AgentRowViewModel> Agents { get; } = [];

    public MainWindowViewModel(Orchestrator orchestrator, WindowRegistry registry)
    {
        _orchestrator = orchestrator;
        _registry = registry;
        _orchestrator.AgentStarted += OnAgentStarted;
        _orchestrator.AgentStopped += OnAgentStopped;

        // Initial fill — covers agents that already exist by the time the VM is built
        // (typical case: orchestrator's hosted-service starts before Avalonia resolves
        // the main window from DI).
        ReconcileFromSnapshot();

        _reconcileTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            (_, _) => ReconcileFromSnapshot());
        _reconcileTimer.Start();
    }

    private void OnAgentStarted(CharacterAgent agent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FindRow(agent) is null)
            {
                Agents.Add(new AgentRowViewModel(agent, _registry));
            }
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

    // Sync the Agents collection to whatever the orchestrator currently has. Runs on
    // the UI thread (DispatcherTimer fires there + ctor also runs there). Idempotent.
    private void ReconcileFromSnapshot()
    {
        var snapshot = _orchestrator.SnapshotAgents();

        // Add missing + refresh existing.
        foreach (var agent in snapshot)
        {
            var row = FindRow(agent);
            if (row is null)
            {
                Agents.Add(new AgentRowViewModel(agent, _registry));
            }
            else
            {
                // Catches state changes (Idle → Following etc.) that we don't have a
                // dedicated event for. Cheap — just raises PropertyChanged.
                row.Refresh();
            }
        }

        // Remove rows whose agent is no longer in the orchestrator's set. ReferenceEquals
        // comparison via HashSet<T> needs the comparer because CharacterAgent doesn't
        // override Equals.
        var live = new HashSet<CharacterAgent>(snapshot, ReferenceEqualityComparer.Instance);
        for (var i = Agents.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(Agents[i].Agent))
            {
                Agents.RemoveAt(i);
            }
        }
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

    public void Dispose()
    {
        _reconcileTimer.Stop();
        _orchestrator.AgentStarted -= OnAgentStarted;
        _orchestrator.AgentStopped -= OnAgentStopped;
    }
}
