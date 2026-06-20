namespace PerfectWorldAgent.Models;

// Runtime model for a known character. The nameplate template image isn't stored here —
// ICharacterRoster resolves it by convention from Name (templates/{Name}.png) so the
// JSON roster file stays small and adding a character via the UI only needs metadata.
public sealed class Character
{
    public required string Name { get; init; }
    public required bool IsMaster { get; init; }

    // Game class — drives class-specific UI (taskbar icon via ClassIconService) and
    // future class-specific automation. CharacterClass.Unknown for placeholder/unidentified.
    public required CharacterClass Class { get; init; }

    // Per-character "panic" key — in-game binding to the 10s damage-immunity skill.
    // Broadcast via BroadcastImmunity hotkey. Empty string = no binding, agent skips.
    public required string ImmunityKey { get; init; }

    // In-game macro that runs /assist against the currently-selected target. Pressed
    // immediately after a hardcoded Shift+1 (select party member 1 = master), so the
    // agent ends up targeting whatever the master is targeting. Master skips assist
    // entirely (IsMaster=true). Defaults to empty for older roster.json entries.
    public string AssistKey { get; init; } = string.Empty;

    // Name of the macro this character runs on BroadcastCombat. Resolved against the
    // global MacroLibrary at combat time. Empty string = no macro assigned, agent's
    // combat run is a no-op (still spends 10s in InCombat then returns to Idle).
    public string CombatMacroName { get; init; } = string.Empty;
}
