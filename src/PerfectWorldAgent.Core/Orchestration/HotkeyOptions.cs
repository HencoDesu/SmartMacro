using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.Orchestration;

// Bound from the "Hotkeys" section of appsettings.json. Defaults map Ctrl+Shift+F1..F4 to
// the four orchestrator triggers — F-keys conflict with in-game shortcuts, the modifier
// combo is unlikely to be bound to anything in PW. Override in config if Stream Deck sends
// different combinations.
public sealed class HotkeyOptions
{
    public List<HotkeyBinding> Bindings { get; init; } =
    [
        new(OrchestratorTrigger.GoFollow, HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F1),
        new(OrchestratorTrigger.GoHold, HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F2),
        new(OrchestratorTrigger.GoCombat, HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F3),
        new(OrchestratorTrigger.Stop, HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F4),
        // Stream Deck emits raw F23; orchestrator broadcasts UseImmunityMessage to all
        // live agents on press. Each agent maps the message to its own
        // Character.ImmunityKey (per-character binding). No modifier; F13-F24 are
        // uncommon and unlikely to collide with the player's existing bindings.
        new(OrchestratorTrigger.BroadcastImmunity, HotkeyModifiers.None, VirtualKey.F23),

        // Mouse-broadcast hotkeys — single/double click on every agent's window at the
        // current cursor position (translated to foreground-window client coords).
        new(OrchestratorTrigger.BroadcastClick, HotkeyModifiers.None, VirtualKey.F22),
        new(OrchestratorTrigger.BroadcastDoubleClick, HotkeyModifiers.None, VirtualKey.F21),
    ];
}

// A single binding from a global input (keyboard chord OR mouse-button chord) to an
// orchestrator trigger. Exactly one of Key / MouseButton must be non-default:
//   * Key != 0 → keyboard hotkey, registered with Win32 RegisterHotKey.
//   * MouseButton != None → mouse hotkey, dispatched via WH_MOUSE_LL low-level hook.
// We keep both fields on the record (rather than a polymorphic hierarchy) so the JSON
// round-trip stays flat and additive — old hotkeys.json files without MouseButton
// deserialize fine with MouseButton defaulting to None.
public sealed record HotkeyBinding(
    OrchestratorTrigger Trigger,
    HotkeyModifiers Modifiers,
    VirtualKey Key,
    MouseButton MouseButton = MouseButton.None)
{
    public bool IsMouse => MouseButton != MouseButton.None;
    public bool IsKeyboard => Key != 0 && MouseButton == MouseButton.None;
}
