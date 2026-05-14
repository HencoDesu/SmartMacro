using System.Collections.ObjectModel;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.App.ViewModels;

// VM for SettingsDialog. One row per OrchestratorTrigger, even ones currently unbound —
// so the user can assign a hotkey to any trigger we expose (including CombatFinished,
// which has no default but is a valid manual-completion signal).
//
// Save flow: collect rows with a non-empty binding (Key OR MouseButton) into HotkeyBinding
// records, hand the list to HotkeyConfigStore.ReplaceAsync. The store persists hotkeys.json
// and raises BindingsChanged, which HotkeyListener picks up to re-register both monitors.
public sealed class SettingsDialogViewModel : ObservableObject
{
    private readonly HotkeyConfigStore _store;
    private string? _errorMessage;

    public SettingsDialogViewModel(HotkeyConfigStore store)
    {
        _store = store;

        var existing = _store.Bindings.ToDictionary(b => b.Trigger);

        Rows = new ObservableCollection<HotkeyBindingRow>(
            Enum.GetValues<OrchestratorTrigger>().Select(t =>
            {
                if (existing.TryGetValue(t, out var binding))
                {
                    return new HotkeyBindingRow(
                        t,
                        binding.Modifiers,
                        binding.IsKeyboard ? binding.Key.ToString() : string.Empty,
                        binding.MouseButton);
                }
                return new HotkeyBindingRow(t, HotkeyModifiers.None, string.Empty, MouseButton.None);
            }));
    }

    public ObservableCollection<HotkeyBindingRow> Rows { get; }

    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        var bindings = new List<HotkeyBinding>();
        var seen = new HashSet<string>();

        foreach (var row in Rows)
        {
            var hasKey = !string.IsNullOrEmpty(row.Key);
            var hasMouse = row.MouseButton != MouseButton.None;

            if (!hasKey && !hasMouse)
            {
                continue;
            }

            // Defensive — picker enforces this (mouse capture clears Key, key capture
            // clears MouseButton). Catch hand-edited state just in case.
            if (hasKey && hasMouse)
            {
                ErrorMessage = $"Trigger '{row.TriggerLabel}': both a key and a mouse button are set; pick one.";
                return false;
            }

            HotkeyBinding binding;
            string fingerprint;
            if (hasMouse)
            {
                binding = new HotkeyBinding(row.Trigger, row.Modifiers, Key: 0, MouseButton: row.MouseButton);
                fingerprint = $"M:{row.Modifiers}:{row.MouseButton}";
            }
            else
            {
                if (!Enum.TryParse<VirtualKey>(row.Key, ignoreCase: false, out var key))
                {
                    ErrorMessage = $"Trigger '{row.TriggerLabel}': '{row.Key}' is not a recognised VirtualKey.";
                    return false;
                }
                binding = new HotkeyBinding(row.Trigger, row.Modifiers, key);
                fingerprint = $"K:{row.Modifiers}:{key}";
            }

            if (!seen.Add(fingerprint))
            {
                ErrorMessage = $"Trigger '{row.TriggerLabel}': combo is already bound to another trigger.";
                return false;
            }

            bindings.Add(binding);
        }

        try
        {
            await _store.ReplaceAsync(bindings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save: {ex.Message}";
            return false;
        }

        return true;
    }
}

// One row in the settings dialog — represents the current edit state of a hotkey binding
// for a single OrchestratorTrigger. Modifiers + Key + MouseButton are TwoWay-bound to a
// KeyBindingPicker in CaptureModifiers mode. Exactly one of Key / MouseButton is non-empty
// at any time (picker invariant).
public sealed class HotkeyBindingRow : ObservableObject
{
    private HotkeyModifiers _modifiers;
    private string _key;
    private MouseButton _mouseButton;

    public HotkeyBindingRow(OrchestratorTrigger trigger, HotkeyModifiers modifiers, string key, MouseButton mouseButton)
    {
        Trigger = trigger;
        _modifiers = modifiers;
        _key = key;
        _mouseButton = mouseButton;
    }

    public OrchestratorTrigger Trigger { get; }

    public string TriggerLabel => TriggerLabels.Get(Trigger);

    public HotkeyModifiers Modifiers { get => _modifiers; set => SetField(ref _modifiers, value); }

    public string Key { get => _key; set => SetField(ref _key, value); }

    public MouseButton MouseButton { get => _mouseButton; set => SetField(ref _mouseButton, value); }
}

// Human-friendly labels for each trigger. Centralised so the Settings dialog (and any
// future status bar / log decoration) shows the same wording.
internal static class TriggerLabels
{
    private static readonly Dictionary<OrchestratorTrigger, string> Map = new()
    {
        [OrchestratorTrigger.GoFollow] = "Follow master",
        [OrchestratorTrigger.GoHold] = "Hold position",
        [OrchestratorTrigger.GoCombat] = "Enter combat",
        [OrchestratorTrigger.CombatFinished] = "Combat finished",
        [OrchestratorTrigger.Stop] = "Stop / reset to Hold",
        [OrchestratorTrigger.BroadcastImmunity] = "Panic — broadcast immunity",
        [OrchestratorTrigger.BroadcastClick] = "Broadcast click at cursor",
        [OrchestratorTrigger.BroadcastDoubleClick] = "Broadcast double-click at cursor",
    };

    public static string Get(OrchestratorTrigger t) =>
        Map.TryGetValue(t, out var label) ? label : t.ToString();
}
