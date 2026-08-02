using SmartMacro.Models;

namespace SmartMacro.Identification;

// Facade over the identification stack (IClassMatcher + ClassTemplateLoader).
// CharacterAgent.IdentifyAsync calls Identify(screenshot) after opening the in-game
// stats window; on a class match the agent transitions out of AwaitingIdentification.
// No persistent roster — the Character is built on the fly from the matched class
// plus shared AgentOptions defaults.
public interface ICharacterProvider
{
    /// <summary>
    /// Matches the screenshot's stats-window class-value region against the loaded class
    /// templates. On a hit, returns a Character whose Class is the matched value and
    /// Name is the class enum string ("Лучник", "Жрец", ...).
    /// </summary>
    /// <returns>The matched character, or <c>null</c> if no template scored above threshold.</returns>
    Character? Identify(byte[] screenshot);
}
