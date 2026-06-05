using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Vision;

namespace PerfectWorldAgent.Agents;

// Default ICharacterProvider — composes INameMatcher (which UI region to compare) with
// ICharacterRoster (where templates live + roster persistence). Stateless beyond the
// dependencies; safe as a singleton in DI.
public sealed partial class CharacterProvider : ICharacterProvider
{
    private readonly INameMatcher _matcher;
    private readonly ICharacterRoster _roster;
    private readonly ILogger<CharacterProvider> _logger;

    public event Action<Character>? CharacterRegistered;

    public CharacterProvider(
        INameMatcher matcher,
        ICharacterRoster roster,
        ILogger<CharacterProvider> logger)
    {
        _matcher = matcher;
        _roster = roster;
        _logger = logger;
    }

    public IReadOnlyList<Character> All => _roster.All;

    public Character? Identify(byte[] screenshot)
    {
        var templates = BuildTemplateDict();
        if (templates.Count == 0)
        {
            return null;
        }

        var match = _matcher.Match(screenshot, templates);
        if (match is null)
        {
            return null;
        }

        var character = _roster.TryGet(match.Name);
        if (character is not null)
        {
            return character;
        }

        // Template was matched but the roster entry got removed between the matcher
        // looking and us looking it up. Don't crash, just decline to promote — the
        // next poll will try again with whatever's in the roster then.
        LogMatchedMissing(match.Name, match.Score);
        return null;

    }

    public async Task<Character> RegisterAsync(
        string name,
        bool isMaster,
        CharacterClass characterClass,
        string burstBuffKey,
        string damageKey,
        string immunityKey,
        string assistKey,
        byte[] screenshot,
        CancellationToken cancellationToken = default)
    {
        var template = _matcher.CropNameRegion(screenshot);
        var character = new Character
        {
            Name = name,
            IsMaster = isMaster,
            Class = characterClass,
            BurstBuffKey = burstBuffKey,
            DamageKey = damageKey,
            ImmunityKey = immunityKey,
            AssistKey = assistKey,
        };

        await _roster.AddAsync(character, template, cancellationToken).ConfigureAwait(false);
        LogRegistered(name, isMaster);
        CharacterRegistered?.Invoke(character);
        return character;
    }

    // Cheap to load from disk on every Identify call for tens of characters; cache with
    // ICharacterRoster.CharacterAdded-driven invalidation is a TODO once the roster grows
    // enough to make this hurt.
    private Dictionary<string, byte[]> BuildTemplateDict()
    {
        var roster = _roster.All;
        var dict = new Dictionary<string, byte[]>(roster.Count);
        foreach (var character in roster)
        {
            var bytes = _roster.GetNameplateTemplate(character.Name);
            if (bytes is not null)
            {
                dict[character.Name] = bytes;
            }
        }
        return dict;
    }

    #region Logging

    [LoggerMessage(LogLevel.Warning, "Matcher returned '{Name}' (score {Score:F3}) but the roster no longer has it; declining to promote")]
    partial void LogMatchedMissing(string name, double score);

    [LoggerMessage(LogLevel.Information, "Character '{Name}' registered (isMaster={IsMaster})")]
    partial void LogRegistered(string name, bool isMaster);

    #endregion
}
