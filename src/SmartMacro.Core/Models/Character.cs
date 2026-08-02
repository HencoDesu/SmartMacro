namespace SmartMacro.Models;

// Identity model for a known character. Name is a display label (typically the class
// enum.ToString() — "Лучник", "Жрец"); Class is the identification key from the
// stats-window template match. All runtime bindings (immunity, assist, combat macro)
// come from AgentOptions defaults — uniform across agents.
public sealed class Character
{
    public required string Name { get; init; }

    // Game class — drives class-specific UI (taskbar icon via ClassIconService) and is
    // the identity key produced by ClassMatcher. CharacterClass.Unknown for the
    // pre-identification placeholder.
    public required CharacterClass Class { get; init; }
}
