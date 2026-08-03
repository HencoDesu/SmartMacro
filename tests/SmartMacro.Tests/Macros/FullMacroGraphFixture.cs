using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

/// <summary>
/// Один граф, задействующий КАЖДЫЙ тип ноды, каждый тип триггера, селекторы, координаты
/// редактора и обе формы ClickNode. Общий на всех, чтобы каждый диалект сериализации (файловый
/// JSON через <see cref="MacroGraphJson"/>, проводной через <c>IpcJson</c>) проверялся на одном
/// и том же худшем случае: тип ноды, добавленный в модель и забытый здесь, разом остаётся без
/// проверки в обоих местах — а это ровно тот режим отказа, о котором стоит кричать.
/// </summary>
public static class FullMacroGraphFixture
{
    /// <summary>Индекс каждой ноды в списке <c>Nodes</c> у <see cref="Build"/> — для выборочных проверок.</summary>
    public const int KeyPressIndex = 0;

    public const int ClickLiteralIndex = 1;
    public const int ClickVarIndex = 2;
    public const int RunMacroIndex = 7;
    public const int FindIndex = 8;
    public const int RecognizeIndex = 10;

    /// <summary>
    /// Всего нод — проверяется утверждением, чтобы добавление нового типа ноды заставило
    /// вернуться в этот файл.
    /// </summary>
    public const int NodeCount = 11;

    /// <summary>Всего триггеров.</summary>
    public const int TriggerCount = 3;

    /// <param name="name">Имя графа; вынесено в параметр, чтобы тест мог собрать библиотеку из двух макросов.</param>
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
            new ClickNode
                { Id = "clickLiteral", Point = new ScreenPoint(100, 200), DoubleClick = true, Next = "clickVar" },
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
