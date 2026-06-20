using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.Macro;

/// <summary>
/// A single macro step: press <see cref="Key"/>, then wait <see cref="DelayMs"/>
/// before the next step. <c>DelayMs = 0</c> means "no wait" — useful for the last step
/// (e.g. start in-game damage macro that handles its own loop, then immediately let
/// the 10-second combat window run out).
/// </summary>
public sealed record MacroStep(VirtualKey Key, int DelayMs);

/// <summary>
/// A named, ordered sequence of keypresses with inter-step delays. Bound to a character
/// via <c>Character.CombatMacroName</c>; runs on <c>BroadcastCombat</c> hotkey within
/// the 10-second combat window enforced by the agent's state machine.
/// </summary>
public sealed class Macro
{
    public required string Name { get; init; }
    public required IReadOnlyList<MacroStep> Steps { get; init; }
}
