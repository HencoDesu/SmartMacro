using Microsoft.Extensions.Logging;
using SmartMacro.Agents;
using SmartMacro.App.Mvvm;
using SmartMacro.Models;

namespace SmartMacro.App.ViewModels;

// View-model for a single live agent row. Wraps the CharacterAgent reference and
// exposes properties for binding. CharacterAgent itself doesn't notify on changes (its
// state mutations go through messages, not C# events), so Refresh() is called externally
// when the orchestrator reports a relevant change (AgentIdentified).
//
// Also carries the operator's manual class-assignment path: SelectedClass +
// AssignClass() bypass the ClassMatcher and force-promote an unidentified agent.
// Useful when auto-id fails (missing template, weird UI state) or during initial
// template setup where the operator knows what's on screen.
public sealed class AgentRowViewModel : ObservableObject
{
    // All real classes (Unknown excluded — placeholder value with no assignment intent).
    public static IReadOnlyList<CharacterClass> AvailableClasses { get; } =
        Enum.GetValues<CharacterClass>().Where(c => c != CharacterClass.Unknown).ToArray();

    private CharacterClass _selectedClass = AvailableClasses.Count > 0 ? AvailableClasses[0] : CharacterClass.Unknown;

    public CharacterAgent Agent { get; }

    public AgentRowViewModel(CharacterAgent agent)
    {
        Agent = agent;
    }

    public string Name => Agent.Name;
    public string State => Agent.State.ToString();
    public bool IsIdentified => Agent.IsIdentified;

    public CharacterClass SelectedClass
    {
        get => _selectedClass;
        set => SetField(ref _selectedClass, value);
    }

    /// <summary>
    /// Manual class assignment for an unidentified agent. Builds a placeholder Character
    /// with the picked class as identity + class-enum's string as display name, then
    /// promotes via <see cref="CharacterAgent.Identify"/>. Returns <c>true</c> on
    /// success; <c>false</c> if the agent is already identified or the pick is Unknown.
    /// </summary>
    public bool TryAssignClass()
    {
        if (Agent.IsIdentified || _selectedClass == CharacterClass.Unknown)
        {
            return false;
        }

        var character = new Character
        {
            Name = _selectedClass.ToString(),
            Class = _selectedClass,
        };
        try
        {
            Agent.Identify(character);
        }
        catch (InvalidOperationException)
        {
            // Race with auto-identify — someone else got there first. Refresh state.
            Refresh();
            return false;
        }
        Refresh();
        return true;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsIdentified));
    }
}
