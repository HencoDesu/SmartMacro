namespace SmartMacro.Identification;

// Facade over the identification stack (IClassMatcher + ClassTemplateLoader).
// CharacterAgent.IdentifyAsync calls Identify(screenshot) after opening the in-game
// stats window; on a match the returned tag is applied to the window via WindowRegistry.
// No persistent roster — the tag string (template filename stem) IS the identity.
public interface ICharacterProvider
{
    /// <summary>
    /// Matches the screenshot's stats-window class-value region against the loaded tag
    /// templates.
    /// </summary>
    /// <returns>The matched tag ("Лучник", "Жрец", ...), or <c>null</c> if no template scored above threshold.</returns>
    string? Identify(byte[] screenshot);
}
