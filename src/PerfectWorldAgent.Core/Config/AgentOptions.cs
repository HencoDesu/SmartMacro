using PerfectWorldAgent.Models;
using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.Config;

// Infra/runtime settings bound from the "Agent" section of appsettings.json.
// Separate from the per-character roster (handled by ICharacterRoster + roster.json) so
// runtime tuning and roster editing have different change cadences.
public sealed class AgentOptions
{
    // The OS-level process name to watch via ProcessMonitor.
    public string GameProcessName { get; init; } = "elementclient";

    // How often ProcessMonitor polls Process.GetProcessesByName to diff against the
    // previous snapshot.
    public int ProcessPollIntervalSeconds { get; init; } = 1;

    // How often each CharacterAgent's RunLoopAsync ticks for window-death detection +
    // inbox-drain wake-up. Identification is no longer polling-based (see StatsHotkey).
    public int AgentPollIntervalSeconds { get; init; } = 2;

    // In-game hotkey that toggles the stats window. Used by IdentifyAsync to open the
    // stats panel, capture the class-text region, then close again. PW default is C.
    public VirtualKey StatsHotkey { get; init; } = VirtualKey.C;

    // Wall-clock delay between sending the stats-open hotkey and capturing the
    // screenshot. PW renders the stats panel asynchronously; ~500ms covers a frozen
    // background client. Tune up if the binarised class region comes back empty.
    public int StatsOpenDelayMs { get; init; } = 500;

    // Boot-flow click targets and timing. When an agent starts (process appears or app
    // launch picks up an existing client), it tries to identify first; on miss, it
    // assumes the client is still on the server-select screen and walks the path:
    //   wait InitialLoadingDelayMs → click ServerSelectButton → wait
    //   ServerSelectAfterDelayMs → click CharacterSelectButton → wait
    //   CharacterSelectAfterDelayMs → retry identify.
    // Coords are in client space (same model as PartySlot1 in ActivatingInputOptions).
    // Use the BroadcastDoubleClick trick (cursor → hotkey → log shows client coords)
    // to find each button at your resolution / UI scale.
    public ScreenPoint ServerSelectButton { get; init; } = new(1192, 1805);
    public ScreenPoint CharacterSelectButton { get; init; } = new(1958, 2053);

    // Template-name references (filename-without-extension) into the GameUiElementLoader.
    // Must match a PNG in Assets/GameUiElements/. Lets you rename / replace templates
    // without touching code.
    public string ServerSelectTemplate { get; init; } = "ServerSelectButton";
    public string CharacterSelectTemplate { get; init; } = "CharacterSelectButton";
    public string InWorldTemplate { get; init; } = "ChatPanelButtons";

    // Search regions for the boot-phase template match. Empty (Width=0 or Height=0)
    // means fullscreen — start there, narrow down later for faster matching once you
    // know roughly where each UI element renders.
    public ScreenRect ServerSelectRegion { get; init; } = default;
    public ScreenRect CharacterSelectRegion { get; init; } = default;
    public ScreenRect InWorldRegion { get; init; } = default;

    // Per-phase timeout — total wait budget for each boot phase to become ready. Default
    // 60s covers PW's slowest legitimate transitions (initial loading after launcher
    // exit can be ~30s). Bump up if any phase reliably times out on a slow client.
    public int BootPhaseTimeoutMs { get; init; } = 60_000;

    // Default in-game key bindings — applied uniformly to every identified agent. No
    // per-character roster anymore; all PW clients are configured identically so one
    // set of defaults works for the whole party.
    public string DefaultImmunityKey { get; init; } = "F1";
    public string DefaultAssistKey { get; init; } = "F2";

    // Default combat-macro name (lookup against MacroLibrary). Empty = no macro
    // assigned; BroadcastCombat puts every agent in InCombat for 10s with no keys.
    public string DefaultCombatMacroName { get; init; } = string.Empty;

    // The class designated as the master — the one being /assist'd by everyone else.
    // Master skips TakeAssistMessage (no point assisting yourself); all other agents
    // click party-slot-1 + fire AssistKey to target whatever master targets.
    public CharacterClass MasterClass { get; init; } = CharacterClass.Лучник;

    // Classes that ignore EVERY broadcast (immunity, combat, assist, cursor-click).
    // Used for utility characters like a warehouse mule that's in-world but shouldn't
    // react to party-wide commands. Boot/identify still runs for them — they just
    // don't act on inbox messages once Idle.
    public List<CharacterClass> IgnoredClasses { get; init; } = new();
}
