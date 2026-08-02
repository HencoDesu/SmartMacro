using System.Text.Json.Serialization;
using SmartMacro.Native;

namespace SmartMacro.Macros.Model;

/// <summary>
/// How a macro starts on its own. JSON-polymorphic via <c>$type</c>
/// (<c>"hotkey"</c> / <c>"process"</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HotkeyTrigger), typeDiscriminator: "hotkey")]
[JsonDerivedType(typeof(ProcessAppearedTrigger), typeDiscriminator: "process")]
public abstract record MacroTrigger;

/// <summary>
/// Global hotkey trigger: a keyboard chord OR a mouse-button chord. Exactly one of
/// <paramref name="Key"/> / <paramref name="MouseButton"/> must be non-default — the same
/// flat shape as the legacy hotkey bindings, so old chords translate 1:1:
///   * <c>Key != 0</c> → keyboard hotkey (Win32 RegisterHotKey).
///   * <c>MouseButton != None</c> → mouse hotkey (WH_MOUSE_LL low-level hook).
/// A hotkey run has no context window; the trigger seeds the <c>cursor</c> variable.
/// </summary>
public sealed record HotkeyTrigger(
    HotkeyModifiers Modifiers,
    VirtualKey Key,
    MouseButton MouseButton = MouseButton.None) : MacroTrigger
{
    /// <summary>The chord is a mouse-button chord.</summary>
    [JsonIgnore]
    public bool IsMouse => MouseButton != MouseButton.None;

    /// <summary>The chord is a keyboard chord.</summary>
    [JsonIgnore]
    public bool IsKeyboard => Key != 0 && MouseButton == MouseButton.None;
}

/// <summary>
/// Fires when a new window of <paramref name="ProcessName"/> appears. The new window
/// becomes the run's context window (boot-style macros).
/// </summary>
public sealed record ProcessAppearedTrigger(string ProcessName) : MacroTrigger;
