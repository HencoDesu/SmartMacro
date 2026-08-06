using System.Text.Json;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.Tests.ViewModels;

// W0.3: отображение «граф ↔ строка редактора». Это код с самой высокой ставкой во всей волне:
// каждое сохранение прогоняет граф пользователя через NodeRowViewModel.FromNode/ToNode, так что
// потерянное здесь поле — это молчаливая потеря данных в файле, который человек руками
// настраивал месяцами.
//
// Равенство сверяется по каноническому JSON, а не по равенству записей: записи модели держат
// List<string> (теги TargetSelector), а их равенство по умолчанию — по ссылке, так что `==`
// прошёл бы и на различающихся графах. К тому же именно JSON и ложится на диск.
public class NodeRowRoundTripTests
{
    /// <summary>Прогоняет одну ноду через строки редактора и возвращает обе формы JSON.</summary>
    private static (string Before, string After) RoundTrip(MacroNode node)
    {
        var row = NodeRowViewModel.FromNode(node);
        var result = row.ToNode();
        return (
            JsonSerializer.Serialize(node, MacroGraphJson.Options),
            JsonSerializer.Serialize(result, MacroGraphJson.Options));
    }

    /// <summary>Граф, задействующий каждый тип ноды, обе формы селектора и координаты на канве.</summary>
    /// <summary>Личность под-макроса, которого зовёт <see cref="EveryNodeType"/>.</summary>
    public static Guid SubmacroId { get; } = Ids.Of("pw-identify-one");

    /// <summary>
    /// Тот самый под-макрос — чтобы граф был не просто разбираемым, а ВАЛИДНЫМ: ссылка в никуда
    /// с волны F4 ошибка, и сохранить такой бандл нельзя.
    /// </summary>
    public static MacroSubmacro TheSubmacro()
    {
        var only = new KeyPressNode { Id = Ids.Of("sub-key"), DisplayName = "sub-key", Key = VirtualKey.C };
        return new MacroSubmacro(
            SubmacroId,
            new MacroGraph { Name = "pw-identify-one", StartNodeId = only.Id, Nodes = [only] });
    }

    public static MacroGraph EveryNodeType() => new()
    {
        Name = "полный-граф",
        Triggers =
        [
            new HotkeyTrigger(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F19),
            new HotkeyTrigger(HotkeyModifiers.None, default, MouseButton.XButton1),
            new ProcessAppearedTrigger("elementclient_64"),
        ],
        StartNodeId = Ids.Of("key"),
        Nodes =
        [
            new KeyPressNode
            {
                Id = Ids.Of("key"), DisplayName = "key",
                Key = VirtualKey.F8,
                Target = new TargetSelector { RequireTags = ["Лучник", "в бою"], ExcludeTags = ["Шаман"] },
                Next = Ids.Of("click"),
                Editor = new NodeEditorInfo(12.5, -40),
            },
            // Селектор есть, но он пустой — это «каждое зарегистрированное окно», и это НЕ то же
            // самое, что отсутствующий селектор; различает их на протяжении round trip флаг
            // UseSelector в редакторе.
            new ClickNode
            {
                Id = Ids.Of("click"), DisplayName = "click",
                Point = new ScreenPoint(285, 456),
                DoubleClick = true,
                Target = new TargetSelector(),
                Next = Ids.Of("click-var"),
            },
            new ClickNode { Id = Ids.Of("click-var"), DisplayName = "click-var", PointVar = "cursor", Next = Ids.Of("delay") },
            new DelayNode { Id = Ids.Of("delay"), DisplayName = "delay", Ms = 1500, Next = Ids.Of("add") },
            new AddTagNode
            {
                Id = Ids.Of("add"), DisplayName = "add",
                Tag = "Лучник",
                Target = new TargetSelector { ExcludeTags = ["Шаман"] },
                Next = Ids.Of("remove"),
            },
            new RemoveTagNode { Id = Ids.Of("remove"), DisplayName = "remove", Tag = "{tag}", Next = Ids.Of("icon") },
            new SetIconNode
            {
                Id = Ids.Of("icon"), DisplayName = "icon",
                IconPath = "Assets/ClassIcons/{tag}.png",
                Target = new TargetSelector { RequireTags = ["Жрец"] },
                Next = Ids.Of("run"),
            },
            new RunSubmacroNode
            {
                Id = Ids.Of("run"), DisplayName = "run",
                SubmacroId = SubmacroId,
                Await = false,
                Target = new TargetSelector { ExcludeTags = ["Шаман"] },
                Next = Ids.Of("find"),
            },
            new FindElementNode
            {
                Id = Ids.Of("find"), DisplayName = "find",
                Template = "ServerSelectButton",
                Region = new ScreenRect(10, 20, 300, 40),
                FoundPointVar = "btn",
                Found = Ids.Of("wait"),
                NotFound = null,
            },
            // Region намеренно пуст — это форма «искать по всему окну».
            new WaitForElementNode
            {
                Id = Ids.Of("wait"), DisplayName = "wait",
                Template = "ChatPanelButtons",
                TimeoutMs = 60_000,
                Found = Ids.Of("recognize"),
                Timeout = null,
            },
            new RecognizeTagNode
            {
                Id = Ids.Of("recognize"), DisplayName = "recognize",
                TemplateSet = "classes",
                Region = new ScreenRect(3200, 1060, 160, 35),
                ApplyTag = false,
                ResultVar = "cls",
                Matched = null,
                NotMatched = Ids.Of("key"),
            },
        ],
    };

    [Test]
    public async Task EveryNodeType_SurvivesFromNodeThenToNode()
    {
        foreach (var node in EveryNodeType().Nodes)
        {
            var (before, after) = RoundTrip(node);
            await Assert.That(after).IsEqualTo(before);
        }
    }

    [Test]
    public async Task NullSelector_And_EmptySelector_StayDistinct()
    {
        var contextWindow = new KeyPressNode { Id = Ids.Of("n"), DisplayName = "n", Key = VirtualKey.A, Target = null };
        var everyWindow = new KeyPressNode { Id = Ids.Of("n"), DisplayName = "n", Key = VirtualKey.A, Target = new TargetSelector() };

        var (_, contextAfter) = RoundTrip(contextWindow);
        var (_, everyAfter) = RoundTrip(everyWindow);

        await Assert.That(contextAfter).IsNotEqualTo(everyAfter);
        await Assert.That(((KeyPressNode)NodeRowViewModel.FromNode(contextWindow).ToNode()).Target).IsNull();
        await Assert.That(((KeyPressNode)NodeRowViewModel.FromNode(everyWindow).ToNode()).Target).IsNotNull();
    }

    [Test]
    public async Task CanvasCoordinates_AreCarriedThrough_EvenThoughTheRowsEditorNeverShowsThem()
    {
        var node = new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 250, Editor = new NodeEditorInfo(700, 42) };

        var result = (DelayNode)NodeRowViewModel.FromNode(node).ToNode();

        await Assert.That(result.Editor).IsEqualTo(new NodeEditorInfo(700, 42));
    }

    [Test]
    public async Task ConditionalsAndDelay_HaveNoTargetSelector_EverythingElseDoes()
    {
        var graph = EveryNodeType();
        foreach (var node in graph.Nodes)
        {
            var row = NodeRowViewModel.FromNode(node);
            var expected = node is not (DelayNode or FindElementNode or WaitForElementNode or RecognizeTagNode);
            await Assert.That(row.HasTarget).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task EdgeLabels_MatchTheNodeFamily()
    {
        var find = NodeRowViewModel.FromNode(new FindElementNode { Id = Ids.Of("f"), DisplayName = "f", Template = "t" });
        var wait = NodeRowViewModel.FromNode(new WaitForElementNode { Id = Ids.Of("w"), DisplayName = "w", Template = "t", TimeoutMs = 1 });
        var key = NodeRowViewModel.FromNode(new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.A });

        // Семейство ноды выбирает НАБОР исходов: у Find это найдено/не найдено, у Wait —
        // найдено/таймаут. Проверяется выбор ключей, а не то, какими словами они подписаны.
        await Assert.That(find.Edges.Select(e => e.Label))
            .IsEquivalentTo(new[] { Strings.Node_Edge_Found, Strings.Node_Edge_NotFound });
        await Assert.That(wait.Edges.Select(e => e.Label))
            .IsEquivalentTo(new[] { Strings.Node_Edge_Found, Strings.Node_Edge_Timeout });
        await Assert.That(key.Edges).Count().IsEqualTo(1);
    }

    // ---- Delay: секунды ↔ миллисекунды ------------------------------------------------

    [Test]
    [Arguments(1500, "1.5")]
    [Arguments(2000, "2")]
    [Arguments(200, "0.2")]
    [Arguments(0, "0")]
    public async Task Delay_MsRendersAsSeconds(int ms, string expected)
    {
        var row = (DelayNodeRowViewModel)NodeRowViewModel.FromNode(new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = ms });

        await Assert.That(row.SecondsText).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1.5", 1500)]
    [Arguments("2", 2000)]
    // Русская раскладка на цифровом блоке выдаёт запятую; инвариантный разбор прочитал бы "1,5"
    // как 15 секунд.
    [Arguments("1,5", 1500)]
    [Arguments("0", 0)]
    [Arguments("", 0)]
    public async Task Delay_SecondsParseBackToMs(string text, int expected)
    {
        var row = (DelayNodeRowViewModel)NodeRowViewModel.FromNode(new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 0 });
        row.SecondsText = text;

        await Assert.That(((DelayNode)row.ToNode()).Ms).IsEqualTo(expected);
        await Assert.That(row.GetInputErrors()).IsEmpty();
    }

    [Test]
    [Arguments("abc")]
    [Arguments("-1")]
    public async Task Delay_RejectsNonsense(string text)
    {
        var row = (DelayNodeRowViewModel)NodeRowViewModel.FromNode(new DelayNode { Id = Ids.Of("пауза"), DisplayName = "пауза", Ms = 0 });
        row.SecondsText = text;

        await Assert.That(row.GetInputErrors()).IsNotEmpty();
    }

    // ---- Click: ровно одно из Point и PointVar ------------------------------------------

    [Test]
    public async Task Click_LiteralPoint_LeavesPointVarUnset()
    {
        var row = (ClickNodeRowViewModel)NodeRowViewModel.FromNode(
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", PointVar = "cursor" });

        row.UseVariable = false;
        row.XText = "640";
        row.YText = "480";
        var node = (ClickNode)row.ToNode();

        await Assert.That(node.PointVar).IsNull();
        await Assert.That(node.Point).IsEqualTo(new ScreenPoint(640, 480));
        await Assert.That(row.UseLiteralPoint).IsTrue();
    }

    [Test]
    public async Task Click_Variable_LeavesPointUnset()
    {
        var row = (ClickNodeRowViewModel)NodeRowViewModel.FromNode(
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", Point = new ScreenPoint(1, 2) });

        row.UseVariable = true;
        row.PointVar = "btn";
        var node = (ClickNode)row.ToNode();

        await Assert.That(node.Point).IsNull();
        await Assert.That(node.PointVar).IsEqualTo("btn");
        await Assert.That(row.UseLiteralPoint).IsFalse();
    }

    [Test]
    public async Task Click_ReportsUnparseableCoordinates()
    {
        var row = (ClickNodeRowViewModel)NodeRowViewModel.FromNode(
            new ClickNode { Id = Ids.Of("клик"), DisplayName = "клик", Point = default(ScreenPoint) });
        row.XText = "сто";

        await Assert.That(row.GetInputErrors()).IsNotEmpty();
    }

    // ---- области ------------------------------------------------------------------------

    [Test]
    public async Task Region_ZeroSize_MeansWholeWindow()
    {
        var row = (FindElementNodeRowViewModel)NodeRowViewModel.FromNode(
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "f", Template = "t", Region = new ScreenRect(5, 6, 7, 8) });

        row.Region.WidthText = "0";

        await Assert.That(((FindElementNode)row.ToNode()).Region).IsNull();
    }

    [Test]
    public async Task Region_IsMandatoryForRecognize_AndKeepsZeroes()
    {
        var row = (RecognizeTagNodeRowViewModel)NodeRowViewModel.FromNode(
            new RecognizeTagNode { Id = Ids.Of("r"), DisplayName = "r", TemplateSet = "classes", Region = default });

        await Assert.That(((RecognizeTagNode)row.ToNode()).Region).IsEqualTo(default(ScreenRect));
    }

    // ---- триггеры -------------------------------------------------------------------------

    [Test]
    public async Task Triggers_RoundTrip()
    {
        foreach (var trigger in EveryNodeType().Triggers)
        {
            var result = TriggerRowViewModel.FromTrigger(trigger).ToTrigger();
            await Assert.That(result).IsEqualTo(trigger);
        }
    }

    [Test]
    public async Task UnboundHotkey_IsReportedAsAnInputError()
    {
        var row = TriggerRowViewModel.Create(MacroTriggerKind.Hotkey);

        await Assert.That(row.GetInputErrors()).IsNotEmpty();

        ((HotkeyTriggerRowViewModel)row).KeyName = "F13";

        await Assert.That(row.GetInputErrors()).IsEmpty();
    }

    [Test]
    public async Task EveryKind_CanBeCreatedFromTheAddMenu()
    {
        await Assert.That(NodeRowViewModel.Kinds).Count().IsEqualTo(Enum.GetValues<MacroNodeKind>().Length);

        foreach (var option in NodeRowViewModel.Kinds)
        {
            // Подпись генерируется от ТИПА ноды; номер — наименьший свободный, а свободен он
            // сквозным образом по графу, поэтому занятый «click-1» двигает номер и у ноды другого
            // семейства. Имена без хвоста «-N» (набранные руками) номеров не занимают.
            var row = NodeRowViewModel.Create(option.Kind, ["click-1"]);
            await Assert.That(row.DisplayName).IsEqualTo($"{MacroNodeNames.Prefix(row.ToNode())}-2");
            // Только что созданная строка обязана сериализоваться сразу же, иначе «добавить ноду»
            // приводило бы граф в состояние, которое и на диск-то не записать.
            await Assert.That(JsonSerializer.Serialize(row.ToNode(), MacroGraphJson.Options)).IsNotEmpty();
        }
    }
}
