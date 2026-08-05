using System.Text.Json;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: MacroGraphJson — ЭТО диалект файлов макросов: каждый тип ноды, каждый тип триггера,
// селекторы и координаты редактора обязаны пережить round trip, а испорченный ввод обязан падать
// чистым JsonException (хранилище показывает это как «файл битый», а не как крах).
public class MacroGraphJsonTests
{
    // Общий с тестами диалекта IPC — см. FullMacroGraphFixture.
    private static MacroGraph BuildFullGraph() => FullMacroGraphFixture.Build();

    [Test]
    public async Task RoundTrip_PreservesEveryNodeTriggerAndEditorCoordinate()
    {
        var original = BuildFullGraph();

        var json = MacroGraphJson.Serialize(original);
        var reloaded = MacroGraphJson.Deserialize(json);

        // Проверка на полное соответствие: повторная сериализация перечитанного графа обязана
        // совпасть байт в байт (записи с полями-List не сравниваются по значению, так что
        // свидетелем равенства выступает форма JSON).
        await Assert.That(MacroGraphJson.Serialize(reloaded)).IsEqualTo(json);

        // Выборочные проверки по типизированной модели.
        await Assert.That(reloaded.Name).IsEqualTo("полный");
        await Assert.That(reloaded.StartNodeId).IsEqualTo(Ids.Of("key"));
        await Assert.That(reloaded.Triggers).Count().IsEqualTo(3);
        await Assert.That(reloaded.Nodes).Count().IsEqualTo(11);

        var hotkey = (HotkeyTrigger)reloaded.Triggers[0];
        await Assert.That(hotkey.Modifiers).IsEqualTo(HotkeyModifiers.Control | HotkeyModifiers.Shift);
        await Assert.That(hotkey.Key).IsEqualTo(VirtualKey.F5);
        var mouse = (HotkeyTrigger)reloaded.Triggers[1];
        await Assert.That(mouse.IsMouse).IsTrue();
        await Assert.That(((ProcessAppearedTrigger)reloaded.Triggers[2]).ProcessName).IsEqualTo("elementclient");

        var key = (KeyPressNode)reloaded.Nodes[0];
        await Assert.That(key.Editor).IsEqualTo(new NodeEditorInfo(10.5, -20.25));
        await Assert.That(key.Target!.RequireTags).Count().IsEqualTo(1);
        await Assert.That(key.Target!.ExcludeTags[0]).IsEqualTo("МАСТЕР");

        var clickLiteral = (ClickNode)reloaded.Nodes[1];
        await Assert.That(clickLiteral.Point).IsEqualTo(new ScreenPoint(100, 200));
        await Assert.That(clickLiteral.DoubleClick).IsTrue();
        var clickVar = (ClickNode)reloaded.Nodes[2];
        await Assert.That(clickVar.Point).IsNull();
        await Assert.That(clickVar.PointVar).IsEqualTo("cursor");

        var run = (RunSubmacroNode)reloaded.Nodes[7];
        await Assert.That(run.Await).IsFalse();

        var find = (FindElementNode)reloaded.Nodes[8];
        await Assert.That(find.Region).IsEqualTo(new ScreenRect(1, 2, 3, 4));
        await Assert.That(find.NotFound).IsNull();

        var recognize = (RecognizeTagNode)reloaded.Nodes[10];
        await Assert.That(recognize.ApplyTag).IsFalse();
        await Assert.That(recognize.ResultVar).IsEqualTo("класс");
    }

    [Test]
    public async Task Serialized_UsesTypeDiscriminatorsAndStringEnums()
    {
        var json = MacroGraphJson.Serialize(BuildFullGraph());

        foreach (var discriminator in new[]
                 {
                     "keyPress", "click", "delay", "addTag", "removeTag", "setIcon",
                     "submacro", "findElement", "waitForElement", "recognizeTag",
                     "hotkey", "process",
                 })
        {
            await Assert.That(json).Contains($"\"$type\": \"{discriminator}\"");
        }

        // Перечисления сериализуются именами, а не числами.
        await Assert.That(json).Contains("\"F8\"");
        await Assert.That(json).Contains("Control, Shift");
        await Assert.That(json).Contains("XButton1");
    }

    [Test]
    public async Task Deserialize_AppliesDocumentedDefaults()
    {
        // Await, ApplyTag, ResultVar и Triggers опущены — обязаны сработать значения по умолчанию.
        const string json =
            """
            {
              "Name": "м",
              "StartNodeId": "11111111-1111-1111-1111-111111111111",
              "Nodes": [
                { "$type": "submacro", "Id": "11111111-1111-1111-1111-111111111111", "SubmacroId": "22222222-2222-2222-2222-222222222222" },
                { "$type": "recognizeTag", "Id": "22222222-2222-2222-2222-222222222222", "TemplateSet": "классы",
                  "Region": { "X": 0, "Y": 0, "Width": 10, "Height": 10 } }
              ]
            }
            """;

        var graph = MacroGraphJson.Deserialize(json);

        await Assert.That(graph.Triggers).Count().IsEqualTo(0);
        var run = (RunSubmacroNode)graph.Nodes[0];
        await Assert.That(run.Await).IsTrue();
        await Assert.That(run.Next).IsNull();
        await Assert.That(run.Target).IsNull();
        var recognize = (RecognizeTagNode)graph.Nodes[1];
        await Assert.That(recognize.ApplyTag).IsTrue();
        await Assert.That(recognize.ResultVar).IsEqualTo("tag");
        // Порог не задан — значит, слой зрения возьмёт своё умолчание, а не ноль.
        await Assert.That(recognize.MatchThreshold).IsNull();
        // Подписи в файле нет — показывать ноду будут по имени семейства.
        await Assert.That(recognize.DisplayName).IsEqualTo(string.Empty);
        await Assert.That(MacroNodeNames.Display(recognize)).IsEqualTo("recognize");
    }

    [Test]
    public async Task UnknownNodeType_ThrowsCleanJsonException()
    {
        const string json =
            """
            {
              "Name": "м",
              "StartNodeId": "a",
              "Nodes": [ { "$type": "teleport", "Id": "a" } ]
            }
            """;

        await Assert.That(() => MacroGraphJson.Deserialize(json)).Throws<JsonException>();
    }

    [Test]
    public async Task UnknownTriggerType_ThrowsCleanJsonException()
    {
        const string json =
            """
            {
              "Name": "м",
              "Triggers": [ { "$type": "voice", "Phrase": "го" } ],
              "StartNodeId": "a",
              "Nodes": []
            }
            """;

        await Assert.That(() => MacroGraphJson.Deserialize(json)).Throws<JsonException>();
    }

    [Test]
    public async Task NullDocument_ThrowsCleanJsonException()
    {
        await Assert.That(() => MacroGraphJson.Deserialize("null")).Throws<JsonException>();
    }

    [Test]
    public async Task Serialize_KeepsCyrillicReadable_InsteadOfEscapingIt()
    {
        // По умолчанию STJ экранирует всё за пределами ASCII, и тег «Лучник» уезжал на диск
        // экранированными последовательностями. Это не порча — тот же сериализатор читает такое
        // обратно, — но файл, который автор правит руками и смотрит диффом, становился нечитаемым
        // ровно в тех местах, где написано что-то осмысленное.
        var graph = new MacroGraph
        {
            Name = "тег",
            StartNodeId = Ids.Of("t"),
            Nodes = [new AddTagNode { Id = Ids.Of("t"), DisplayName = "добавить", Tag = "Лучник" }],
        };

        var json = MacroGraphJson.Serialize(graph);

        await Assert.That(json).Contains("\"Лучник\"");
        await Assert.That(json).Contains("добавить");
        // Экранируется обратный слэш, а не начинается escape-последовательность: ищем в выводе
        // ЛИТЕРАЛЬНЫЕ символы \u04, которыми STJ записал бы кириллицу, если бы послабление
        // Encoder'а не действовало. Без удвоения "\u04" — незавершённая escape-последовательность
        // и ошибка компиляции.
        await Assert.That(json).DoesNotContain("\\u04");
        // И читается обратно — послабление касается только вывода.
        await Assert.That(((AddTagNode)MacroGraphJson.Deserialize(json).Nodes[0]).Tag).IsEqualTo("Лучник");
    }

    [Test]
    public async Task RoundTrip_KeepsThePerNodeMatchThreshold()
    {
        var graph = new MacroGraph
        {
            Name = "порог",
            StartNodeId = Ids.Of("r"),
            Nodes =
            [
                new RecognizeTagNode
                {
                    Id = Ids.Of("r"), DisplayName = "recognize-1", TemplateSet = "classes",
                    Region = new ScreenRect(0, 0, 10, 10), MatchThreshold = 0.82,
                },
            ],
        };

        var reloaded = (RecognizeTagNode)MacroGraphJson.Deserialize(MacroGraphJson.Serialize(graph)).Nodes[0];

        await Assert.That(reloaded.MatchThreshold).IsEqualTo(0.82);
        await Assert.That(reloaded.DisplayName).IsEqualTo("recognize-1");
        await Assert.That(reloaded.Id).IsEqualTo(Ids.Of("r"));
    }
}
