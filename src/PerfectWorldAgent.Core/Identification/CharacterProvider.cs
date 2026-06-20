using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Persistence;
using PerfectWorldAgent.Vision;

namespace PerfectWorldAgent.Identification;

// Default ICharacterProvider — composes IClassMatcher + ClassTemplateLoader + ICharacterRoster.
// Stateless beyond the dependencies; safe as a singleton in DI.
public sealed partial class CharacterProvider : ICharacterProvider
{
    private readonly IClassMatcher _matcher;
    private readonly ClassTemplateLoader _templates;
    private readonly ICharacterRoster _roster;
    private readonly ILogger<CharacterProvider> _logger;

    public event Action<Character>? CharacterRegistered;

    public CharacterProvider(
        IClassMatcher matcher,
        ClassTemplateLoader templates,
        ICharacterRoster roster,
        ILogger<CharacterProvider> logger)
    {
        _matcher = matcher;
        _templates = templates;
        _roster = roster;
        _logger = logger;
    }

    public IReadOnlyList<Character> All => _roster.All;

    public Character? Identify(byte[] screenshot)
    {
        if (_templates.Templates.Count == 0)
        {
            return null;
        }

        var match = _matcher.Match(screenshot, _templates.Templates);
        if (match is null)
        {
            return null;
        }

        var character = _roster.TryGet(match.Class);
        if (character is not null)
        {
            return character;
        }

        // Class was matched against a template, but no roster entry has this class. The
        // user hasn't labeled this character yet. Log and decline.
        LogMatchedMissing(match.Class, match.Score);
        return null;
    }

    public async Task<Character> RegisterAsync(
        string name,
        bool isMaster,
        CharacterClass characterClass,
        string immunityKey,
        string assistKey,
        string combatMacroName,
        CancellationToken cancellationToken = default)
    {
        if (characterClass == CharacterClass.Unknown)
        {
            throw new ArgumentException("Cannot register a character with Class=Unknown.", nameof(characterClass));
        }

        var character = new Character
        {
            Name = name,
            IsMaster = isMaster,
            Class = characterClass,
            ImmunityKey = immunityKey,
            AssistKey = assistKey,
            CombatMacroName = combatMacroName,
        };

        await _roster.AddAsync(character, cancellationToken).ConfigureAwait(false);
        LogRegistered(characterClass, name, isMaster);
        CharacterRegistered?.Invoke(character);
        return character;
    }

    [LoggerMessage(LogLevel.Warning, "Matcher returned class={Cls} (score {Score:F3}) but no roster entry has this class; declining to promote")]
    partial void LogMatchedMissing(CharacterClass cls, double score);

    [LoggerMessage(LogLevel.Information, "Character class={Cls} name='{Name}' registered (isMaster={IsMaster})")]
    partial void LogRegistered(CharacterClass cls, string name, bool isMaster);
}
