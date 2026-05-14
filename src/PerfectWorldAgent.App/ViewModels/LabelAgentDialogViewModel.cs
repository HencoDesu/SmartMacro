using Avalonia.Media.Imaging;
using PerfectWorldAgent.Agents;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Vision;

namespace PerfectWorldAgent.App.ViewModels;

// Backing VM for the LabelAgentDialog window.
//
// On construction we snap the agent's window ONCE and crop the nameplate region — both
// for the preview the user sees (so they know which character they're labeling) and as
// the screenshot we'll hand to ICharacterProvider.RegisterAsync on save. Using the same
// captured frame for both means "what you saw in the dialog is what gets stored as the
// template" — no race with the live game state between preview and save.
//
// Save flow: send the captured screenshot to RegisterAsync (which crops/persists the
// template + roster entry), then promote the live agent in-place via agent.Identify.
public sealed class LabelAgentDialogViewModel : ObservableObject
{
    private readonly CharacterAgent _agent;
    private readonly ICharacterProvider _provider;
    private readonly byte[] _capturedScreenshot;

    private string _name = string.Empty;
    private bool _isMaster;
    private CharacterClass _class = CharacterClass.Unknown;
    private string _burstBuffKey = string.Empty;
    private string _damageKey = string.Empty;
    private string _immunityKey = string.Empty;
    private string? _errorMessage;

    // Snapshot of the enum for ComboBox.ItemsSource — taken once, shared across all
    // dialog instances. Includes Unknown so the user can save without picking a class,
    // though future class-specific logic will treat that as "no rotation available".
    public static IReadOnlyList<CharacterClass> ClassOptions { get; } = Enum.GetValues<CharacterClass>();

    public LabelAgentDialogViewModel(
        CharacterAgent agent,
        ICharacterProvider provider,
        INameMatcher matcher)
    {
        _agent = agent;
        _provider = provider;

        _capturedScreenshot = agent.CaptureScreenshot();
        var cropBytes = matcher.CropNameRegion(_capturedScreenshot);
        using var ms = new MemoryStream(cropBytes);
        NameplatePreview = new Bitmap(ms);

        // Sanity-check the template the matcher will see. If the nameplate text is
        // rendered in a colour that doesn't pass binarisation (PW colours low-level /
        // store characters in blue, which falls below the white-threshold), the saved
        // template will be near-empty and produce garbage matches against OTHER agents'
        // windows. Surface this in the dialog so the user can re-snap from a normal-
        // text location before saving.
        var (bright, total) = matcher.GetNameplatePixelStats(_capturedScreenshot);
        if (bright < total * 0.01)
        {
            NameplateWarning = $"Nameplate barely visible ({bright}/{total} bright pixels in the search region). " +
                "Likely blue text (low-level / store character) or HUD not yet rendered. " +
                "Auto-id won't work well with this template — consider re-snapping in a normal location, " +
                "or save anyway if you intend to label this agent manually each session.";
        }

        // If the agent is already identified, prefill all fields with its current
        // Character so the user can edit (fix misidentification, tweak keys, etc.)
        // without retyping everything.
        if (agent.IsIdentified)
        {
            var c = agent.Character;
            _name = c.Name;
            _isMaster = c.IsMaster;
            _class = c.Class;
            _burstBuffKey = c.BurstBuffKey;
            _damageKey = c.DamageKey;
            _immunityKey = c.ImmunityKey;
        }
    }

    // Cropped nameplate region from the snap taken at dialog-open. Read-only — set in
    // ctor, displayed in XAML via Image binding.
    public Bitmap NameplatePreview { get; }

    // Populated in the ctor when the captured nameplate fails the sparsity check.
    // Display-only; doesn't block save.
    public string? NameplateWarning { get; }

    public string CurrentName => _agent.Name;

    public string Name { get => _name; set => SetField(ref _name, value); }
    public bool IsMaster { get => _isMaster; set => SetField(ref _isMaster, value); }
    public CharacterClass Class { get => _class; set => SetField(ref _class, value); }
    public string BurstBuffKey { get => _burstBuffKey; set => SetField(ref _burstBuffKey, value); }
    public string DamageKey { get => _damageKey; set => SetField(ref _damageKey, value); }
    public string ImmunityKey { get => _immunityKey; set => SetField(ref _immunityKey, value); }
    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    // Returns true if save succeeded (caller closes dialog). Returns false on validation
    // failure or persistence error — ErrorMessage is populated for display.
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            ErrorMessage = "Name is required.";
            return false;
        }

        Character character;
        try
        {
            character = await _provider.RegisterAsync(
                _name.Trim(),
                _isMaster,
                _class,
                _burstBuffKey.Trim(),
                _damageKey.Trim(),
                _immunityKey.Trim(),
                _capturedScreenshot,
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
                // Re-label / edit existing — replaces Character without state transition.
                _agent.UpdateCharacter(character);
            }
            else
            {
                _agent.Identify(character);
            }
        }
        catch (InvalidOperationException ex)
        {
            // Race: agent's identification state changed between dialog open and save.
            // The roster entry was persisted — fine — but the in-memory agent didn't
            // pick up the update. User can retry.
            ErrorMessage = $"Failed to apply to agent: {ex.Message}";
            return false;
        }

        return true;
    }
}
