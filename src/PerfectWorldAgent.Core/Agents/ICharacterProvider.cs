using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Agents;

// Facade over the identification stack (INameMatcher + ICharacterRoster). Two callers,
// two intents:
//   * CharacterAgent.RunAsync uses Identify(screenshot) on every poll to auto-promote
//     itself once the player enters the world with a known nameplate.
//   * UI uses RegisterAsync(...) to teach the system about a new character when the user
//     labels an unidentified agent. UI then calls agent.Identify(returnedCharacter) to
//     promote the live agent in-place.
//
// Both methods are read-or-write atomic — the provider owns coordination between matcher
// and roster (e.g. cropping the screenshot to a template before persisting).
public interface ICharacterProvider
{
    IReadOnlyList<Character> All { get; }

    // Returns the matched roster entry if the screenshot's HUD name region matches any
    // known template above the matcher's confidence threshold; null otherwise.
    Character? Identify(byte[] screenshot);

    // Persists a new character + the nameplate template cropped from the given screenshot.
    // Throws if the name collides with an existing roster entry.
    Task<Character> RegisterAsync(
        string name,
        bool isMaster,
        CharacterClass characterClass,
        string burstBuffKey,
        string damageKey,
        string immunityKey,
        string assistKey,
        byte[] screenshot,
        CancellationToken cancellationToken = default);

    // Fires after RegisterAsync persists a new entry. Lets UI components refresh their
    // "known characters" lists without polling.
    event Action<Character>? CharacterRegistered;
}
