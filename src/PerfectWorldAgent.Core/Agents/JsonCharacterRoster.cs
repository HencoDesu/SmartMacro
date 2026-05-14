using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Models;

namespace PerfectWorldAgent.Agents;

// JSON-backed roster with templates/{Name}.png alongside. Paths resolved from
// AppContext.BaseDirectory so the layout sits next to the .exe — easy to back up or
// version-control the whole folder.
//
// File layout:
//   <BaseDirectory>/
//     roster.json
//     templates/
//       Alice.png
//       Bob.png
//
// Concurrency: AddAsync serialised with a SemaphoreSlim so two simultaneous adds don't
// race the JSON file. Reads (All, TryGet) take a snapshot of the immutable list and are
// lock-free.
public sealed partial class JsonCharacterRoster : ICharacterRoster
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    // Path characters that NTFS forbids in filenames, plus path separators. Any character
    // name containing one of these is rejected by AddAsync.
    private static readonly char[] InvalidNameChars =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    private readonly string _rosterPath;
    private readonly string _templatesDir;
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
        _templatesDir = Path.Combine(baseDirectory, "templates");
        _characters = LoadFromDisk();
    }

    public IReadOnlyList<Character> All => _characters;

    public Character? TryGet(string name) =>
        _characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    public byte[]? GetNameplateTemplate(string name)
    {
        var path = TemplatePathFor(name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public async Task AddAsync(Character character, byte[] nameplatePng, CancellationToken cancellationToken = default)
    {
        ValidateName(character.Name);

        bool wasUpdate;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Upsert: if a character with the same name already exists, replace its
            // metadata AND overwrite its template. This is what the "Label" dialog
            // expects in the edit/re-label flow — same name = update the entry.
            var existing = _characters.FirstOrDefault(c => string.Equals(c.Name, character.Name, StringComparison.Ordinal));
            wasUpdate = existing is not null;

            Directory.CreateDirectory(_templatesDir);
            await File.WriteAllBytesAsync(TemplatePathFor(character.Name), nameplatePng, cancellationToken).ConfigureAwait(false);

            var updated = wasUpdate
                ? _characters.Replace(existing!, character)
                : _characters.Add(character);
            await PersistAsync(updated, cancellationToken).ConfigureAwait(false);
            _characters = updated;

            if (wasUpdate)
            {
                LogCharacterUpdated(character.Name, updated.Count);
            }
            else
            {
                LogCharacterAdded(character.Name, updated.Count);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        // Raised outside the lock so handlers can't deadlock if they re-enter the roster.
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

    private string TemplatePathFor(string name) => Path.Combine(_templatesDir, $"{name}.png");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Character name must be non-empty.", nameof(name));
        }

        if (name.IndexOfAny(InvalidNameChars) >= 0)
        {
            throw new ArgumentException(
                $"Character name '{name}' contains characters not allowed in filenames.",
                nameof(name));
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

    [LoggerMessage(LogLevel.Information, "Character '{Name}' added to roster (total {Count})")]
    partial void LogCharacterAdded(string name, int count);

    [LoggerMessage(LogLevel.Information, "Character '{Name}' updated in roster (total {Count})")]
    partial void LogCharacterUpdated(string name, int count);
}
