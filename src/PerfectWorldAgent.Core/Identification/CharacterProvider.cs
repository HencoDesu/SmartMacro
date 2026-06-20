using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Vision;

namespace PerfectWorldAgent.Identification;

// Default ICharacterProvider — composes IClassMatcher + ClassTemplateLoader. Stateless
// beyond the dependencies; safe as a singleton in DI.
public sealed partial class CharacterProvider : ICharacterProvider
{
    private readonly IClassMatcher _matcher;
    private readonly ClassTemplateLoader _templates;
    private readonly ILogger<CharacterProvider> _logger;

    public CharacterProvider(
        IClassMatcher matcher,
        ClassTemplateLoader templates,
        ILogger<CharacterProvider> logger)
    {
        _matcher = matcher;
        _templates = templates;
        _logger = logger;
    }

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

        LogMatched(match.Class, match.Score);
        return new Character
        {
            Name = match.Class.ToString(),
            Class = match.Class,
        };
    }

    [LoggerMessage(LogLevel.Information, "Class identified: {Cls} (score {Score:F3})")]
    partial void LogMatched(CharacterClass cls, double score);
}
