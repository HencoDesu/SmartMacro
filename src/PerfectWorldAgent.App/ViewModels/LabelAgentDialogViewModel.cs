using PerfectWorldAgent.Agents;
using PerfectWorldAgent.App.Mvvm;
using PerfectWorldAgent.Combat;
using PerfectWorldAgent.Identification;
using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.App.ViewModels;

// Backing VM for the LabelAgentDialog window.
//
// Class is now the identity key for roster lookup — the user picks it explicitly from
// a dropdown. Name is a free-form user-friendly label (because all PW characters share
// the same in-game name now, by user preference). No nameplate screenshot, no template
// harvesting — class-based identification uses shared per-class templates in
// Assets/ClassTemplates/.
public sealed class LabelAgentDialogViewModel : ObservableObject
{
    // Sentinel item in the macro dropdown for "no macro assigned". Stored as empty
    // string on the Character; serves as a visible "(none)" entry in the picker.
    private const string NoMacroOption = "(none)";

    private readonly CharacterAgent _agent;
    private readonly ICharacterProvider _provider;

    private string _name = string.Empty;
    private bool _isMaster;
    private CharacterClass _class = CharacterClass.Unknown;
    private string _immunityKey = string.Empty;
    private string _assistKey = string.Empty;
    private string _combatMacroName = NoMacroOption;
    private string? _errorMessage;

    public static IReadOnlyList<CharacterClass> ClassOptions { get; } = Enum.GetValues<CharacterClass>();

    // Macro names available in the picker. Built from the live MacroLibrary at dialog
    // open with "(none)" prepended so the user can deassign without typing.
    public IReadOnlyList<string> MacroOptions { get; }

    public LabelAgentDialogViewModel(
        CharacterAgent agent,
        ICharacterProvider provider,
        MacroLibrary macroLibrary)
    {
        _agent = agent;
        _provider = provider;

        var macros = new List<string> { NoMacroOption };
        foreach (var m in macroLibrary.Macros)
        {
            macros.Add(m.Name);
        }
        MacroOptions = macros;

        // If the agent is already identified, prefill all fields with its current
        // Character so the user can edit (fix misidentification, tweak keys, etc.)
        // without retyping everything.
        if (agent.IsIdentified)
        {
            var c = agent.Character;
            _name = c.Name;
            _isMaster = c.IsMaster;
            _class = c.Class;
            _immunityKey = c.ImmunityKey;
            _assistKey = c.AssistKey;
            _combatMacroName = string.IsNullOrEmpty(c.CombatMacroName) ? NoMacroOption : c.CombatMacroName;
        }
    }

    public string CurrentName => _agent.Name;

    public string Name { get => _name; set => SetField(ref _name, value); }
    public bool IsMaster { get => _isMaster; set => SetField(ref _isMaster, value); }
    public CharacterClass Class { get => _class; set => SetField(ref _class, value); }
    public string ImmunityKey { get => _immunityKey; set => SetField(ref _immunityKey, value); }
    public string AssistKey { get => _assistKey; set => SetField(ref _assistKey, value); }
    public string CombatMacroName { get => _combatMacroName; set => SetField(ref _combatMacroName, value); }
    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            ErrorMessage = "Name is required.";
            return false;
        }

        if (_class == CharacterClass.Unknown)
        {
            ErrorMessage = "Class is required — it's the roster identity key.";
            return false;
        }

        // "(none)" maps to empty string on the Character; the agent's combat loop
        // handles missing/empty macro names gracefully (just holds InCombat for 10s).
        var macroName = _combatMacroName == NoMacroOption ? string.Empty : _combatMacroName;

        Character character;
        try
        {
            character = await _provider.RegisterAsync(
                _name.Trim(),
                _isMaster,
                _class,
                _immunityKey.Trim(),
                _assistKey.Trim(),
                macroName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to register: {ex.Message}";
            return false;
        }

        try
        {
            if (_agent.IsIdentified)
            {
                _agent.UpdateCharacter(character);
            }
            else
            {
                _agent.Identify(character);
            }
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = $"Failed to apply to agent: {ex.Message}";
            return false;
        }

        return true;
    }
}
