using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Persistence;

// Mutable, persistent store of known characters. Backed by a single roster.json file
// alongside the .exe. The Class field is the identity key (unique per roster entry);
// Name is a free-form user-friendly label since multiple chars now share visible in-game
// names. Runtime additions go through AddAsync — updates in-memory state and persists,
// then raises CharacterAdded so the orchestrator can react.
public interface ICharacterRoster
{
    IReadOnlyList<Character> All { get; }

    /// <summary>
    /// Looks up a character by class (the new identity key). Returns the matched entry,
    /// or <c>null</c> if no character of that class is in the roster.
    /// </summary>
    Character? TryGet(CharacterClass cls);

    /// <summary>
    /// Persists a new character entry, then raises <see cref="CharacterAdded"/>. Upserts
    /// on class collision (replaces the metadata for that class). No template / image
    /// storage — class-based identification uses shared per-class templates in
    /// <c>Assets/ClassTemplates/</c>, not per-character ones.
    /// </summary>
    Task AddAsync(Character character, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires after <see cref="AddAsync"/> persists a new or updated entry.
    /// </summary>
    event Action<Character>? CharacterAdded;
}
