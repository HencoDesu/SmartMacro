using PerfectWorldAgent.Native;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Hotkeys;

// Bound from the "Hotkeys" section of appsettings.json. Defaults map Stream-Deck-friendly
// F-keys to the four broadcast triggers. Used only as seed values if hotkeys.json is
// missing on first run — once HotkeyConfigStore persists, this is ignored.
public sealed class HotkeyOptions
{
    public List<HotkeyBinding> Bindings { get; init; } =
    [
        // Stream Deck emits raw F23/F22/F21 — F13-F24 are uncommon and unlikely to
        // collide with the player's existing in-game bindings. Each agent maps the
        // broadcast to its own Character key (BurstBuff / Damage / Immunity / Assist).
        new(OrchestratorTrigger.BroadcastImmunity, HotkeyModifiers.None, VirtualKey.F23),
        new(OrchestratorTrigger.BroadcastClick, HotkeyModifiers.None, VirtualKey.F22),
        new(OrchestratorTrigger.BroadcastDoubleClick, HotkeyModifiers.None, VirtualKey.F21),
        new(OrchestratorTrigger.BroadcastIdentify, HotkeyModifiers.None, VirtualKey.F20),
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

// User-defined macro hotkey. Same key/modifier/mouse shape as HotkeyBinding but the
// trigger is a free-form macro name (looked up in MacroLibrary at fire time).
public sealed record MacroHotkeyBinding(
    string MacroName,
    HotkeyModifiers Modifiers,
    VirtualKey Key,
    MouseButton MouseButton = MouseButton.None)
{
    public bool IsMouse => MouseButton != MouseButton.None;
    public bool IsKeyboard => Key != 0 && MouseButton == MouseButton.None;
}
