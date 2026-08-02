using Microsoft.Extensions.Logging;
using SmartMacro.Vision;

namespace SmartMacro.Identification;

// Default ICharacterProvider — composes IClassMatcher + ClassTemplateLoader. No state
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

    public string? Identify(byte[] screenshot)
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

        LogMatched(match.Tag, match.Score);
        return match.Tag;
    }

    [LoggerMessage(LogLevel.Information, "Tag identified: {Tag} (score {Score:F3})")]
    partial void LogMatched(string tag, double score);
}
