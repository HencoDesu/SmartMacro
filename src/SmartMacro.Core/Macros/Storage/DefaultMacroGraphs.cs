using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Vision;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// The example macro set written into an empty <c>macros/</c> folder on first run.
///
/// These are NOT built-in behavior — they are ordinary, fully editable macro files that
/// happen to reproduce what SmartMacro hard-coded before the node graph existed (Perfect
/// World boot-to-in-world, party immunity, assist, cursor broadcast, identification).
/// Every coordinate, region, key and tag below is the value that used to be hard-coded in
/// config, so a PW user gets the old behavior out of the box — and can now tune it in the
/// macro instead of in <c>appsettings.json</c>, which is the whole point of the move.
///
/// Anyone not playing PW just deletes the files.
/// </summary>
public static class DefaultMacroGraphs
{
    /// <summary>Process the boot example waits for. Matches the shipped ProcessProfiles entry.</summary>
    private const string GameProcessName = "elementclient_64";

    /// <summary>Tag of the character everyone else assists — they don't assist themselves.</summary>
    private const string MasterTag = "Лучник";

    /// <summary>Utility character (warehouse mule): in-world, but sits out party-wide commands.</summary>
    private const string IgnoredTag = "Шаман";

    /// <summary>Crop of the stats panel holding the "Класс: &lt;name&gt;" value text.</summary>
    private static readonly ScreenRect ClassNameRegion = new(3200, 1060, 160, 35);

    /// <summary>Chat-panel icons: their presence is how we know the client reached the world.</summary>
    private static readonly ScreenRect ChatPanelRegion = new(0, 2100, 160, 60);

    /// <summary>Generous per-phase budget — PW's post-launcher loading can take ~30s.</summary>
    private const int PhaseTimeoutMs = 60_000;

    /// <summary>In-game hotkey toggling the stats panel.</summary>
    private const VirtualKey StatsHotkey = VirtualKey.C;

    /// <summary>Wall-clock wait for PW to actually render the stats panel before capturing.</summary>
    private const int StatsOpenDelayMs = 500;

    /// <summary>All example graphs, in no particular order.</summary>
    public static IReadOnlyList<MacroGraph> Build() =>
    [
        Boot(),
        Immunity(),
        Assist(),
        CursorClick(),
        Identify(),
        IdentifyOne(),
    ];

    /// <summary>
    /// Drives a freshly launched client from server-select to in-world, then identifies
    /// the character. The showcase graph: process trigger, branch-per-outcome, a variable
    /// written by a conditional node and read back through <c>{tag}</c> interpolation.
    /// Every timeout branch simply ends the run — the operator re-triggers by hand.
    /// </summary>
    private static MacroGraph Boot() => new()
    {
        Name = "pw-boot",
        Triggers = [new ProcessAppearedTrigger(GameProcessName)],
        StartNodeId = "wait-server-select",
        Nodes =
        [
            // Region left empty = search the whole client area: the button's position
            // shifts with resolution, so a full-frame search is the portable default.
            new WaitForElementNode
            {
                Id = "wait-server-select",
                Template = "ServerSelectButton",
                TimeoutMs = PhaseTimeoutMs,
                Found = "click-server-select",
                Timeout = null,
            },
            new ClickNode { Id = "click-server-select", Point = new ScreenPoint(1192, 1805), Next = "wait-character-select" },
            new WaitForElementNode
            {
                Id = "wait-character-select",
                Template = "CharacterSelectButton",
                TimeoutMs = PhaseTimeoutMs,
                Found = "click-character-select",
                Timeout = null,
            },
            new ClickNode { Id = "click-character-select", Point = new ScreenPoint(1958, 2053), Next = "wait-in-world" },
            new WaitForElementNode
            {
                Id = "wait-in-world",
                Template = "ChatPanelButtons",
                Region = ChatPanelRegion,
                TimeoutMs = PhaseTimeoutMs,
                Found = "open-stats",
                Timeout = null,
            },
            // Identification tail, spelled out rather than delegated to pw-identify-one so
            // this file stays readable and self-contained as an example.
            new KeyPressNode { Id = "open-stats", Key = StatsHotkey, Next = "await-stats" },
            new DelayNode { Id = "await-stats", Ms = StatsOpenDelayMs, Next = "recognize-class" },
            new RecognizeTagNode
            {
                Id = "recognize-class",
                TemplateSet = TemplateSetProvider.ClassesSetName,
                Region = ClassNameRegion,
                ApplyTag = true,
                ResultVar = "tag",
                Matched = "set-icon",
                // No match still closes the panel — leaving it open would break later macros.
                NotMatched = "close-stats",
            },
            new SetIconNode { Id = "set-icon", IconPath = "Assets/ClassIcons/{tag}.png", Next = "close-stats" },
            new KeyPressNode { Id = "close-stats", Key = StatsHotkey, Next = null },
        ],
    };

    /// <summary>Panic button: fire the shared damage-immunity key on the whole party at once.</summary>
    private static MacroGraph Immunity() => new()
    {
        Name = "pw-immunity",
        Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)],
        StartNodeId = "immunity",
        Nodes =
        [
            new KeyPressNode
            {
                Id = "immunity",
                Key = VirtualKey.F8,
                Target = new TargetSelector { ExcludeTags = [IgnoredTag] },
            },
        ],
    };

    /// <summary>
    /// Every follower selects the master's party portrait, then fires its in-game
    /// <c>/assist</c> macro — the whole party ends up on the master's target. The master
    /// is excluded (nobody assists themselves) along with the utility character.
    /// </summary>
    private static MacroGraph Assist()
    {
        var followers = new TargetSelector { ExcludeTags = [MasterTag, IgnoredTag] };
        return new MacroGraph
        {
            Name = "pw-assist",
            Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F19)],
            StartNodeId = "select-master",
            Nodes =
            [
                new ClickNode { Id = "select-master", Point = new ScreenPoint(285, 456), Target = followers, Next = "settle" },
                // PW needs wall-clock time to apply the selection before /assist runs,
                // otherwise the macro assists the previous target.
                new DelayNode { Id = "settle", Ms = 200, Next = "assist-key" },
                new KeyPressNode { Id = "assist-key", Key = VirtualKey.F2, Target = followers },
            ],
        };
    }

    /// <summary>
    /// Replays the cursor position as a click on every client at once. Reads the
    /// <c>cursor</c> variable the trigger layer seeds on every run.
    /// </summary>
    private static MacroGraph CursorClick() => new()
    {
        Name = "pw-cursor-click",
        Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F22)],
        StartNodeId = "click",
        Nodes =
        [
            new ClickNode
            {
                Id = "click",
                PointVar = MacroVariables.CursorVariableName,
                Target = new TargetSelector { ExcludeTags = [IgnoredTag] },
            },
        ],
    };

    /// <summary>
    /// Fan-out identification: one sub-run of <c>pw-identify-one</c> per window, each with
    /// that window as its context. This is how a hotkey (which has no context window of
    /// its own) reaches conditional nodes at all.
    /// </summary>
    private static MacroGraph Identify() => new()
    {
        Name = "pw-identify",
        Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F20)],
        StartNodeId = "identify-all",
        Nodes =
        [
            new RunMacroNode
            {
                Id = "identify-all",
                MacroName = "pw-identify-one",
                Target = new TargetSelector { ExcludeTags = [IgnoredTag] },
                Await = true,
            },
        ],
    };

    /// <summary>
    /// Identifies the character in ONE window: open stats, recognise the class name, tag
    /// the window, apply the taskbar icon, close stats. Trigger-less on purpose — it's a
    /// library routine called by <c>pw-identify</c> (or from the UI against a window).
    /// </summary>
    private static MacroGraph IdentifyOne() => new()
    {
        Name = "pw-identify-one",
        StartNodeId = "open-stats",
        Nodes =
        [
            new KeyPressNode { Id = "open-stats", Key = StatsHotkey, Next = "await-stats" },
            new DelayNode { Id = "await-stats", Ms = StatsOpenDelayMs, Next = "recognize-class" },
            new RecognizeTagNode
            {
                Id = "recognize-class",
                TemplateSet = TemplateSetProvider.ClassesSetName,
                Region = ClassNameRegion,
                ApplyTag = true,
                ResultVar = "tag",
                Matched = "set-icon",
                NotMatched = "close-stats",
            },
            new SetIconNode { Id = "set-icon", IconPath = "Assets/ClassIcons/{tag}.png", Next = "close-stats" },
            new KeyPressNode { Id = "close-stats", Key = StatsHotkey, Next = null },
        ],
    };
}
