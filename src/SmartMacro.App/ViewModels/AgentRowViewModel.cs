using SmartMacro.Agents;
using SmartMacro.App.Mvvm;
using SmartMacro.Windows;

namespace SmartMacro.App.ViewModels;

// View-model for a single live agent row. Wraps the CharacterAgent reference and
// exposes properties for binding. CharacterAgent itself doesn't notify on changes (its
// state mutations go through messages, not C# events), so Refresh() is called externally
// when the orchestrator reports a relevant change (AgentIdentified).
//
// Also carries the operator's manual tag-assignment path: TagText + TryAssignTag() tag a
// window by hand, straight through WindowRegistry. Useful when the identification macro
// fails (missing template, weird UI state) or during initial template setup where the
// operator knows what's on screen. Transitional UI — W0.3 replaces the row with a
// windows+tag-chips view.
public sealed class AgentRowViewModel : ObservableObject
{
    private readonly WindowRegistry _registry;
    private string _tagText = string.Empty;

    public CharacterAgent Agent { get; }

    public AgentRowViewModel(CharacterAgent agent, WindowRegistry registry)
    {
        Agent = agent;
        _registry = registry;
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
    /// Manual tag assignment: adds the typed tag to the window through
    /// <see cref="WindowRegistry"/>, the sole owner of tag state. No icon is applied —
    /// that's a SetIconNode's job in whichever macro cares.
    /// </summary>
    /// <returns><c>true</c> when a tag was added; <c>false</c> for a blank entry or a duplicate tag.</returns>
    public bool TryAssignTag()
    {
        var tag = _tagText.Trim();
        if (tag.Length == 0)
        {
            return false;
        }

        var added = _registry.AddTag(Agent.Handle, tag);
        Refresh();
        return added;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsIdentified));
    }
}
