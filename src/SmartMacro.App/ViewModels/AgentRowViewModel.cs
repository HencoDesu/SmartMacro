using SmartMacro.Agents;
using SmartMacro.App.Mvvm;

namespace SmartMacro.App.ViewModels;

// View-model for a single live agent row. Wraps the CharacterAgent reference and
// exposes properties for binding. CharacterAgent itself doesn't notify on changes (its
// state mutations go through messages, not C# events), so Refresh() is called externally
// when the orchestrator reports a relevant change (AgentIdentified).
//
// Also carries the operator's manual tag-assignment path: TagText + TryAssignTag()
// bypass the ClassMatcher and tag an unidentified window by hand. Useful when auto-id
// fails (missing template, weird UI state) or during initial template setup where the
// operator knows what's on screen. Transitional UI — W0.3 replaces the row with a
// windows+tag-chips view.
public sealed class AgentRowViewModel : ObservableObject
{
    private string _tagText = string.Empty;

    public CharacterAgent Agent { get; }

    public AgentRowViewModel(CharacterAgent agent)
    {
        Agent = agent;
    }

    public string Name => Agent.Name;
    public string State => Agent.State;
    public bool IsIdentified => Agent.IsIdentified;

    /// <summary>Free-form tag typed by the operator for manual assignment.</summary>
    public string TagText
    {
        get => _tagText;
        set => SetField(ref _tagText, value);
    }

    /// <summary>
    /// Manual tag assignment for an unidentified window. Promotes via
    /// <see cref="CharacterAgent.Identify"/>, which applies the tag through
    /// WindowRegistry, applies the taskbar icon, and notifies the orchestrator.
    /// Returns <c>true</c> on success; <c>false</c> if the agent is already identified
    /// or the tag is blank.
    /// </summary>
    public bool TryAssignTag()
    {
        var tag = _tagText.Trim();
        if (Agent.IsIdentified || tag.Length == 0)
        {
            return false;
        }

        try
        {
            Agent.Identify(tag);
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
