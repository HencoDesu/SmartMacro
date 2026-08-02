using System.Collections.ObjectModel;
using SmartMacro.App.Mvvm;
using SmartMacro.Hotkeys;
using SmartMacro.Macro;
using SmartMacro.Native;
using SmartMacro.Orchestration;

namespace SmartMacro.App.ViewModels;

// VM for SettingsDialog. Two sections:
//   * Triggers: one row per OrchestratorTrigger enum value (fixed, but unbound rows allowed)
//   * Macros:   dynamic list of (MacroName → hotkey) bindings — user adds/removes
//
// Save: build both lists, hand to HotkeyConfigStore.ReplaceAsync.
public sealed class SettingsDialogViewModel : ObservableObject
{
    private readonly HotkeyConfigStore _store;
    private string? _errorMessage;

    public SettingsDialogViewModel(HotkeyConfigStore store, MacroLibrary macroLibrary)
    {
        _store = store;
        AvailableMacroNames = macroLibrary.Macros.Select(m => m.Name).ToArray();

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

        MacroRows = new ObservableCollection<MacroHotkeyRow>(
            _store.MacroBindings.Select(b => new MacroHotkeyRow(
                b.MacroName,
                b.Modifiers,
                b.IsKeyboard ? b.Key.ToString() : string.Empty,
                b.MouseButton,
                AvailableMacroNames)));
    }

    public ObservableCollection<HotkeyBindingRow> Rows { get; }
    public ObservableCollection<MacroHotkeyRow> MacroRows { get; }

    /// <summary>
    /// Macro names available for the dropdown — snapshot of MacroLibrary at dialog open.
    /// User can also type a name that doesn't exist yet (e.g. planned macro); validated
    /// at fire-time by the agent's MacroLibrary lookup.
    /// </summary>
    public IReadOnlyList<string> AvailableMacroNames { get; }

    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    public void AddMacroRow()
    {
        var defaultName = AvailableMacroNames.Count > 0 ? AvailableMacroNames[0] : string.Empty;
        MacroRows.Add(new MacroHotkeyRow(defaultName, HotkeyModifiers.None, string.Empty, MouseButton.None, AvailableMacroNames));
    }

    public void RemoveMacroRow(MacroHotkeyRow row) => MacroRows.Remove(row);

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        var bindings = new List<HotkeyBinding>();
        var macroBindings = new List<MacroHotkeyBinding>();
        var seen = new HashSet<string>();

        // 1. Fixed-trigger rows
        foreach (var row in Rows)
        {
            if (!TryBuildKeyMouse(row.Key, row.Modifiers, row.MouseButton, out var key, out var mb, out var err, $"Trigger '{row.TriggerLabel}'"))
            {
                if (err is null) continue; // empty row — skip
                ErrorMessage = err;
                return false;
            }

            var binding = new HotkeyBinding(row.Trigger, row.Modifiers, key, mb);
            var fp = Fingerprint(row.Modifiers, key, mb);
            if (!seen.Add(fp))
            {
                ErrorMessage = $"Trigger '{row.TriggerLabel}': combo is already bound to another entry.";
                return false;
            }
            bindings.Add(binding);
        }

        // 2. Macro rows
        foreach (var row in MacroRows)
        {
            var name = row.MacroName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                ErrorMessage = "Macro hotkey row has no macro name selected.";
                return false;
            }

            if (!TryBuildKeyMouse(row.Key, row.Modifiers, row.MouseButton, out var key, out var mb, out var err, $"Macro '{name}'"))
            {
                if (err is null)
                {
                    ErrorMessage = $"Macro '{name}': no key/mouse binding set — remove the row or assign a hotkey.";
                    return false;
                }
                ErrorMessage = err;
                return false;
            }

            var binding = new MacroHotkeyBinding(name, row.Modifiers, key, mb);
            var fp = Fingerprint(row.Modifiers, key, mb);
            if (!seen.Add(fp))
            {
                ErrorMessage = $"Macro '{name}': combo is already bound to another entry.";
                return false;
            }
            macroBindings.Add(binding);
        }

        try
        {
            await _store.ReplaceAsync(bindings, macroBindings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save: {ex.Message}";
            return false;
        }
        return true;
    }

    // Returns true on a valid (Key XOR MouseButton) combo. On empty row returns false
    // with error=null (caller skips silently). On invalid input returns false with
    // error set (caller surfaces in UI).
    private static bool TryBuildKeyMouse(
        string keyString, HotkeyModifiers modifiers, MouseButton mouseButton,
        out VirtualKey key, out MouseButton resolvedMouse, out string? error, string label)
    {
        key = 0;
        resolvedMouse = MouseButton.None;
        error = null;

        var hasKey = !string.IsNullOrEmpty(keyString);
        var hasMouse = mouseButton != MouseButton.None;

        if (!hasKey && !hasMouse)
        {
            return false; // empty — caller decides
        }

        if (hasKey && hasMouse)
        {
            error = $"{label}: both a key and a mouse button are set; pick one.";
            return false;
        }

        if (hasMouse)
        {
            resolvedMouse = mouseButton;
            return true;
        }

        if (!Enum.TryParse<VirtualKey>(keyString, ignoreCase: false, out key))
        {
            error = $"{label}: '{keyString}' is not a recognised VirtualKey.";
            return false;
        }
        return true;
    }

    private static string Fingerprint(HotkeyModifiers modifiers, VirtualKey key, MouseButton mouseButton) =>
        mouseButton != MouseButton.None ? $"M:{modifiers}:{mouseButton}" : $"K:{modifiers}:{key}";
}

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

public sealed class MacroHotkeyRow : ObservableObject
{
    private string _macroName;
    private HotkeyModifiers _modifiers;
    private string _key;
    private MouseButton _mouseButton;

    public MacroHotkeyRow(string macroName, HotkeyModifiers modifiers, string key, MouseButton mouseButton, IReadOnlyList<string> availableMacroNames)
    {
        _macroName = macroName;
        _modifiers = modifiers;
        _key = key;
        _mouseButton = mouseButton;
        AvailableMacroNames = availableMacroNames;
    }

    public string MacroName { get => _macroName; set => SetField(ref _macroName, value); }
    public HotkeyModifiers Modifiers { get => _modifiers; set => SetField(ref _modifiers, value); }
    public string Key { get => _key; set => SetField(ref _key, value); }
    public MouseButton MouseButton { get => _mouseButton; set => SetField(ref _mouseButton, value); }

    // Snapshot of macro names at dialog open — bound directly by the AutoCompleteBox
    // in each row so we don't need parent-traversal in XAML.
    public IReadOnlyList<string> AvailableMacroNames { get; }
}

internal static class TriggerLabels
{
    private static readonly Dictionary<OrchestratorTrigger, string> Map = new()
    {
        [OrchestratorTrigger.BroadcastImmunity] = "Panic — broadcast immunity",
        [OrchestratorTrigger.BroadcastAssist] = "Take assist from master",
        [OrchestratorTrigger.BroadcastClick] = "Broadcast click at cursor",
        [OrchestratorTrigger.BroadcastDoubleClick] = "Broadcast double-click at cursor",
        [OrchestratorTrigger.BroadcastIdentify] = "Identify all agents (opens stats, reads class)",
    };

    public static string Get(OrchestratorTrigger t) =>
        Map.TryGetValue(t, out var label) ? label : t.ToString();
}
