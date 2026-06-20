using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Persistence;

// JSON-backed roster keyed by CharacterClass. Path resolved from AppContext.BaseDirectory
// so the file sits next to the .exe — easy to back up or version-control.
//
// File layout:
//   <BaseDirectory>/roster.json
//
// No sidecar templates/ dir — class-based identification uses shared per-class templates
// in Assets/ClassTemplates/ (see ClassTemplateLoader).
//
// Concurrency: AddAsync serialised with a SemaphoreSlim. Reads (All, TryGet) take a
// snapshot of the immutable list and are lock-free.
public sealed partial class JsonCharacterRoster : ICharacterRoster
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rosterPath;
    private readonly ILogger<JsonCharacterRoster> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ImmutableList<Character> _characters;

    public event Action<Character>? CharacterAdded;

    public JsonCharacterRoster(ILogger<JsonCharacterRoster> logger)
        : this(AppContext.BaseDirectory, logger)
    {
    }

    public JsonCharacterRoster(string baseDirectory, ILogger<JsonCharacterRoster> logger)
    {
        _logger = logger;
        _rosterPath = Path.Combine(baseDirectory, "roster.json");
        _characters = LoadFromDisk();
    }

    public IReadOnlyList<Character> All => _characters;

    public Character? TryGet(CharacterClass cls) =>
        _characters.FirstOrDefault(c => c.Class == cls);

    public async Task AddAsync(Character character, CancellationToken cancellationToken = default)
    {
        if (character.Class == CharacterClass.Unknown)
        {
            throw new ArgumentException("Cannot persist a character with Class=Unknown — class is the roster identity key.", nameof(character));
        }

        bool wasUpdate;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Upsert: class collision = replace metadata. This is what the Label dialog
            // expects when the user relabels an already-known class (e.g. updating
            // ImmunityKey or CombatMacroName).
            var existing = _characters.FirstOrDefault(c => c.Class == character.Class);
            wasUpdate = existing is not null;

            var updated = wasUpdate
                ? _characters.Replace(existing!, character)
                : _characters.Add(character);
            await PersistAsync(updated, cancellationToken).ConfigureAwait(false);
            _characters = updated;

            if (wasUpdate)
            {
                LogCharacterUpdated(character.Class, character.Name, updated.Count);
            }
            else
            {
                LogCharacterAdded(character.Class, character.Name, updated.Count);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        CharacterAdded?.Invoke(character);
    }

    private async Task PersistAsync(ImmutableList<Character> characters, CancellationToken cancellationToken)
    {
        var dto = new RosterFile { Characters = characters.ToList() };
        await using var stream = File.Create(_rosterPath);
        await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private ImmutableList<Character> LoadFromDisk()
    {
        if (!File.Exists(_rosterPath))
        {
            LogRosterFileMissing(_rosterPath);
            return ImmutableList<Character>.Empty;
        }

        try
        {
            using var stream = File.OpenRead(_rosterPath);
            var dto = JsonSerializer.Deserialize<RosterFile>(stream, JsonOptions);
            var characters = dto?.Characters ?? new List<Character>();
            LogRosterLoaded(characters.Count, _rosterPath);
            return characters.ToImmutableList();
        }
        catch (Exception ex)
        {
            LogRosterLoadFailed(ex, _rosterPath);
            return ImmutableList<Character>.Empty;
        }
    }

    private sealed class RosterFile
    {
        public List<Character> Characters { get; set; } = new();
    }

    [LoggerMessage(LogLevel.Information, "Roster loaded: {Count} characters from {Path}")]
    partial void LogRosterLoaded(int count, string path);

    [LoggerMessage(LogLevel.Information, "Roster file not found at {Path}; starting with empty roster")]
    partial void LogRosterFileMissing(string path);

    [LoggerMessage(LogLevel.Error, "Failed to load roster from {Path}; starting with empty roster")]
    partial void LogRosterLoadFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Information, "Character class={Cls} name='{Name}' added to roster (total {Count})")]
    partial void LogCharacterAdded(CharacterClass cls, string name, int count);

    [LoggerMessage(LogLevel.Information, "Character class={Cls} name='{Name}' updated in roster (total {Count})")]
    partial void LogCharacterUpdated(CharacterClass cls, string name, int count);
}
