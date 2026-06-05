namespace PerfectWorldAgent.Models;

// Runtime model for a known character. The nameplate template image isn't stored here —
// ICharacterRoster resolves it by convention from Name (templates/{Name}.png) so the
// JSON roster file stays small and adding a character via the UI only needs metadata.
public sealed class Character
{
    public required string Name { get; init; }
    public required bool IsMaster { get; init; }

    // Game class — drives future class-specific automation (e.g. damage rotations,
    // group buffs). For placeholder/unidentified characters this is CharacterClass.Unknown.
    public required CharacterClass Class { get; init; }

    // In-game hotkey strings (e.g. "F1"). Parsed to VirtualKey at send time via
    // Enum.TryParse. Empty string means "this character has no binding for this action"
    // — the agent skips the corresponding command.
    public required string BurstBuffKey { get; init; }
    public required string DamageKey { get; init; }
    public required string ImmunityKey { get; init; }

    // Assist key — in-game macro that runs "/assist" against the currently-selected
    // target. Used together with a hardcoded Shift+1 (select party member 1 = master)
    // sent immediately before, so the agent ends up targeting whatever the master is
    // targeting. Default value is reasonable to leave empty for unidentified entries;
    // master skips assist entirely (IsMaster=true). Optional for backward-compat with
    // existing roster.json entries that pre-date the AssistKey field.
    public string AssistKey { get; init; } = string.Empty;
}
