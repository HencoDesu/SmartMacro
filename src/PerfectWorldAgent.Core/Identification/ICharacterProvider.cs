using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Identification;

// Facade over the identification stack (IClassMatcher + ClassTemplateLoader +
// ICharacterRoster). Two callers, two intents:
//   * CharacterAgent.IdentifyAsync uses Identify(screenshot) on demand (triggered by the
//     BroadcastIdentify hotkey) after opening the in-game stats window. Returns the
//     matched roster entry by class.
//   * UI uses RegisterAsync(...) to add a new character when the user labels an
//     unidentified agent. The label dialog now picks the class explicitly from a
//     dropdown — no screenshot harvesting needed.
public interface ICharacterProvider
{
    IReadOnlyList<Character> All { get; }

    /// <summary>
    /// Matches the screenshot's stats-window class-value region against known class
    /// templates. Returns the roster entry whose <see cref="Character.Class"/> matches.
    /// </summary>
    /// <returns>The matched roster entry, or <c>null</c> on no match or no roster entry.</returns>
    Character? Identify(byte[] screenshot);

    /// <summary>
    /// Persists a new character. The Class field is the identity key; collisions upsert.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if <see cref="Character.Class"/> is <see cref="CharacterClass.Unknown"/>.</exception>
    Task<Character> RegisterAsync(
        string name,
        bool isMaster,
        CharacterClass characterClass,
        string immunityKey,
        string assistKey,
        string combatMacroName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires after <see cref="RegisterAsync"/> persists a new entry.
    /// </summary>
    event Action<Character>? CharacterRegistered;
}
