using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Agents;

// Mutable, persistent store of known characters. Backed by a JSON file alongside the .exe
// plus a templates/ directory of nameplate PNGs (templates/{Name}.png by convention).
// Runtime additions go through AddAsync — both updates in-memory state and persists to
// disk, then raises CharacterAdded so the orchestrator can react (e.g. promote a pending
// unknown process into a live agent).
public interface ICharacterRoster
{
    IReadOnlyList<Character> All { get; }

    Character? TryGet(string name);

    // Loads the template PNG bytes for the given character, or null if no template file
    // exists on disk. Read on demand rather than eagerly so a roster of hundreds doesn't
    // pin hundreds of images in memory.
    byte[]? GetNameplateTemplate(string name);

    Task AddAsync(Character character, byte[] nameplatePng, CancellationToken cancellationToken = default);

    event Action<Character>? CharacterAdded;
}
