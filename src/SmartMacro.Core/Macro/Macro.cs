using System.Text.Json.Serialization;
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
/// A named macro with per-tag action sequences. Triggered from the Macros dialog's
/// per-row Run button → broadcasts <c>RunMacroMessage(name)</c> → each live agent
/// picks the first key its window's tag set contains and runs those actions via
/// <see cref="MacroRunner"/>. Windows with no matching tag drop the broadcast silently.
/// </summary>
/// <remarks>
/// Per-tag because in-game cast times and rotations differ — a Лучник damage burst
/// is different from a Жрец one.
/// </remarks>
public sealed class Macro
{
    public required string Name { get; init; }

    // Concrete Dictionary / List types because System.Text.Json deserialise into
    // interface-typed `required init` properties is finicky.
    //
    // TODO(W0.2): transitional bridge — dies with the node-graph macro model. The JSON
    // property name stays "ActionsByClass" so pre-tag macros.json files keep loading:
    // the old enum keys were serialized as their names ("Лучник"), which now read in
    // as plain tag strings unchanged.
    [JsonPropertyName("ActionsByClass")]
    public required Dictionary<string, List<MacroAction>> ActionsByTag { get; init; }
}
