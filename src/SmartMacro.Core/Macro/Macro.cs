using System.Text.Json.Serialization;
using SmartMacro.Models;
using SmartMacro.Native;

namespace SmartMacro.Macro;

/// <summary>
/// One step in a macro — either a single key press or a wait. Discriminated union so
/// the runner doesn't have to peek at MacroStep fields ("is DelayMs zero? is Key set?").
/// JSON tag uses "$type" with short discriminators ("key", "delay").
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(KeyPressAction), typeDiscriminator: "key")]
[JsonDerivedType(typeof(DelayAction), typeDiscriminator: "delay")]
[JsonDerivedType(typeof(ClickAction), typeDiscriminator: "click")]
public abstract record MacroAction;

/// <summary>Press <paramref name="Key"/> via AgentInputDispatcher.FireKeyAsync.</summary>
public sealed record KeyPressAction(VirtualKey Key) : MacroAction;

/// <summary>Wait <paramref name="Ms"/> milliseconds. <c>0</c> is a legal no-op.</summary>
public sealed record DelayAction(int Ms) : MacroAction;

/// <summary>Left-button click at client-space <paramref name="Point"/>. Set <paramref name="DoubleClick"/> for a double-click.</summary>
public sealed record ClickAction(ScreenPoint Point, bool DoubleClick) : MacroAction;

/// <summary>
/// A named macro with per-class action sequences. Triggered from the Macros dialog's
/// per-row Run button → broadcasts <c>RunMacroMessage(name)</c> → each live agent
/// looks up its class's actions and runs them via <see cref="MacroRunner"/>. Classes
/// absent from <see cref="ActionsByClass"/> drop the broadcast silently.
/// </summary>
/// <remarks>
/// Per-class because in-game cast times and rotations differ — a Лучник damage burst
/// is different from a Жрец one. Editor format uses <c>[ClassName]</c> headers, then
/// one action per line: a VirtualKey name (key press) or a non-negative integer (delay).
/// </remarks>
public sealed class Macro
{
    public required string Name { get; init; }
    // Concrete Dictionary / List types because System.Text.Json deserialise into
    // interface-typed `required init` properties is finicky.
    public required Dictionary<CharacterClass, List<MacroAction>> ActionsByClass { get; init; }
}
