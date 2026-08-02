namespace SmartMacro.Vision;

// The three boot screens we walk through from launcher exit → in-world. BootDetector
// uses one template (PNG) per phase to answer "is this screen rendered RIGHT NOW".
// Identical enum names map to filenames under Assets/BootTemplates/{phase}.png:
//   ServerSelect.png, CharacterSelect.png, InWorld.png
public enum BootPhase
{
    // The server-list screen with the "Confirm server" button rendered.
    ServerSelect,

    // The character-list screen with the "Enter game" button rendered.
    CharacterSelect,

    // The in-game world with the HUD visible — used both to detect end-of-boot AND
    // (as a one-shot pre-check) to short-circuit the whole flow when an already-in-
    // world client is picked up by the agent.
    InWorld,
}
