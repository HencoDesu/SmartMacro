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
        StartNodeId = Ids.Of("key"),
        Nodes =
        [
            new KeyPressNode
            {
                Id = Ids.Of("key"), DisplayName = "key",
                Key = VirtualKey.F8,
                Target = new TargetSelector { RequireTags = ["перс"], ExcludeTags = ["МАСТЕР"] },
                Next = Ids.Of("clickLiteral"),
                Editor = new NodeEditorInfo(10.5, -20.25),
            },
            new ClickNode
                { Id = Ids.Of("clickLiteral"), DisplayName = "clickLiteral", Point = new ScreenPoint(100, 200), DoubleClick = true, Next = Ids.Of("clickVar") },
            new ClickNode { Id = Ids.Of("clickVar"), DisplayName = "clickVar", PointVar = "cursor", Next = Ids.Of("delay") },
            new DelayNode { Id = Ids.Of("delay"), DisplayName = "delay", Ms = 250, Next = Ids.Of("addTag") },
            new AddTagNode { Id = Ids.Of("addTag"), DisplayName = "addTag", Tag = "класс-{tag}", Next = Ids.Of("removeTag") },
            new RemoveTagNode
            {
                Id = Ids.Of("removeTag"), DisplayName = "removeTag",
                Tag = "боевой",
                Target = new TargetSelector { RequireTags = ["перс"] },
                Next = Ids.Of("icon"),
            },
            new SetIconNode { Id = Ids.Of("icon"), DisplayName = "icon", IconPath = "icons/{tag}.png", Next = Ids.Of("run") },
            new RunSubmacroNode
            {
                Id = Ids.Of("run"), DisplayName = "run",
                SubmacroId = Ids.Of("под-макрос"),
                Target = new TargetSelector { ExcludeTags = ["МАСТЕР"] },
                Await = false,
                Next = Ids.Of("find"),
            },
            new FindElementNode
            {
                Id = Ids.Of("find"), DisplayName = "find",
                Template = "кнопка",
                Region = new ScreenRect(1, 2, 3, 4),
                FoundPointVar = "btn",
                Found = Ids.Of("wait"),
                NotFound = null,
            },
            new WaitForElementNode
            {
                Id = Ids.Of("wait"), DisplayName = "wait",
                Template = "мир",
                TimeoutMs = 60000,
                FoundPointVar = "pt",
                Found = Ids.Of("recognize"),
                Timeout = null,
            },
            new RecognizeTagNode
            {
                Id = Ids.Of("recognize"), DisplayName = "recognize",
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
