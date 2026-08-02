using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

/// <summary>
/// One graph exercising EVERY node type, every trigger type, selectors, editor
/// coordinates and both ClickNode shapes. Shared so that each serialization dialect
/// (file JSON via <see cref="MacroGraphJson"/>, wire JSON via
/// <c>IpcJson</c>) is tested against the same worst case — a node type added to the model
/// and forgotten here goes untested in both places at once, which is the failure mode
/// worth making loud.
/// </summary>
public static class FullMacroGraphFixture
{
    /// <summary>Index of each node inside <see cref="Build"/>'s <c>Nodes</c> list, for spot checks.</summary>
    public const int KeyPressIndex = 0;
    public const int ClickLiteralIndex = 1;
    public const int ClickVarIndex = 2;
    public const int RunMacroIndex = 7;
    public const int FindIndex = 8;
    public const int RecognizeIndex = 10;

    /// <summary>Total node count — asserted so that adding a node type forces this file to be revisited.</summary>
    public const int NodeCount = 11;

    /// <summary>Total trigger count.</summary>
    public const int TriggerCount = 3;

    /// <param name="name">Graph name; parameterized so a test can build a two-macro library.</param>
    public static MacroGraph Build(string name = "полный") => new()
    {
        Name = name,
        Triggers =
        [
            new HotkeyTrigger(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F5),
            new HotkeyTrigger(HotkeyModifiers.None, default, MouseButton.XButton1),
            new ProcessAppearedTrigger("elementclient"),
        ],
        StartNodeId = "key",
        Nodes =
        [
            new KeyPressNode
            {
                Id = "key",
                Key = VirtualKey.F8,
                Target = new TargetSelector { RequireTags = ["перс"], ExcludeTags = ["МАСТЕР"] },
                Next = "clickLiteral",
                Editor = new NodeEditorInfo(10.5, -20.25),
            },
            new ClickNode { Id = "clickLiteral", Point = new ScreenPoint(100, 200), DoubleClick = true, Next = "clickVar" },
            new ClickNode { Id = "clickVar", PointVar = "cursor", Next = "delay" },
            new DelayNode { Id = "delay", Ms = 250, Next = "addTag" },
            new AddTagNode { Id = "addTag", Tag = "класс-{tag}", Next = "removeTag" },
            new RemoveTagNode
            {
                Id = "removeTag",
                Tag = "боевой",
                Target = new TargetSelector { RequireTags = ["перс"] },
                Next = "icon",
            },
            new SetIconNode { Id = "icon", IconPath = "icons/{tag}.png", Next = "run" },
            new RunMacroNode
            {
                Id = "run",
                MacroName = "под-макрос",
                Target = new TargetSelector { ExcludeTags = ["МАСТЕР"] },
                Await = false,
                Next = "find",
            },
            new FindElementNode
            {
                Id = "find",
                Template = "кнопка",
                Region = new ScreenRect(1, 2, 3, 4),
                FoundPointVar = "btn",
                Found = "wait",
                NotFound = null,
            },
            new WaitForElementNode
            {
                Id = "wait",
                Template = "мир",
                TimeoutMs = 60000,
                FoundPointVar = "pt",
                Found = "recognize",
                Timeout = null,
            },
            new RecognizeTagNode
            {
                Id = "recognize",
                TemplateSet = "классы",
                Region = new ScreenRect(5, 6, 7, 8),
                ApplyTag = false,
                ResultVar = "класс",
                Matched = null,
                NotMatched = null,
            },
        ],
    };
}
