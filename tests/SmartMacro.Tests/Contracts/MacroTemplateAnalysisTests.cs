using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Contracts;

// Статический разбор «какой макрос какой шаблон называет». Половина браузера шаблонов, и та
// самая, которой НЕ нужен новый тип сообщения: библиотека у панели уже есть.
//
// Главное, что здесь закреплено, — что разбор говорит ровно то же, что делает исполнитель. Он
// живёт рядом с MacroVariableAnalysis и по тем же правилам, но с одним важным отличием, которое
// легко забыть: имена шаблонов НЕ интерполируются.
public class MacroTemplateAnalysisTests
{
    private static readonly ScreenRect Region = new(0, 0, 100, 40);

    private static MacroGraph Macro(string name, params MacroNode[] nodes) => new()
    {
        Name = name,
        StartNodeId = nodes.Length > 0 ? nodes[0].Id : Ids.Of("n1"),
        Nodes = [.. nodes],
    };

    [Test]
    public async Task Analyze_SeparatesSingleTemplatesFromSets()
    {
        var usage = MacroTemplateAnalysis.Analyze(
        [
            Macro(
                "pw-boot",
                new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "ServerSelectButton" },
                new WaitForElementNode { Id = Ids.Of("wait"), DisplayName = "wait", Template = "CharacterSelectButton", TimeoutMs = 1000 }),
            Macro("pw-identify", new RecognizeTagNode { Id = Ids.Of("recognize"), DisplayName = "recognize", TemplateSet = "classes", Region = Region }),
        ]);

        // Наборы первыми, дальше по имени: раздел «наборы» в браузере идёт над одиночными.
        await Assert.That(usage.Select(u => $"{(u.IsSet ? "набор" : "файл")}:{u.Name}"))
            .IsEquivalentTo(new[] { "набор:classes", "файл:CharacterSelectButton", "файл:ServerSelectButton" });
    }

    [Test]
    public async Task Analyze_NamesEveryNodeThatMentionsTheTemplate_AndCountsMacros()
    {
        var usage = MacroTemplateAnalysis.Analyze(
        [
            Macro(
                "pw-boot",
                new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "Кнопка" },
                new WaitForElementNode { Id = Ids.Of("wait"), DisplayName = "wait", Template = "Кнопка", TimeoutMs = 1000 }),
            Macro("pw-relog", new FindElementNode { Id = Ids.Of("again"), DisplayName = "again", Template = "Кнопка" }),
        ]);

        var button = usage.Single(u => u.Name == "Кнопка");

        await Assert.That(button.References.Select(r => $"{r.MacroName}/{r.NodeName}"))
            .IsEquivalentTo(new[] { "pw-boot/find", "pw-boot/wait", "pw-relog/again" });
        await Assert.That(button.References.Select(r => r.Slot))
            .IsEquivalentTo(new[] { TemplateSlot.FindTemplate, TemplateSlot.WaitTemplate, TemplateSlot.FindTemplate });
        // Ссылок три, макросов два — в списке показывается именно второе.
        await Assert.That(button.MacroCount).IsEqualTo(2);
    }

    [Test]
    public async Task Analyze_KeepsASetAndAFileOfTheSameNameApart()
    {
        // Папка templates/classes и файл templates/classes.png — разные вещи, и обе законны.
        var usage = MacroTemplateAnalysis.Analyze(
        [
            Macro(
                "оба",
                new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "classes" },
                new RecognizeTagNode { Id = Ids.Of("recognize"), DisplayName = "recognize", TemplateSet = "classes", Region = Region }),
        ]);

        await Assert.That(usage).Count().IsEqualTo(2);
        await Assert.That(usage.Count(u => u.IsSet)).IsEqualTo(1);
        await Assert.That(usage.Count(u => !u.IsSet)).IsEqualTo(1);
    }

    [Test]
    public async Task Analyze_TreatsBracesAsPartOfTheName_BecauseTheExecutorDoesToo()
    {
        // Тег, путь иконки и имя вызываемого макроса исполнитель прогоняет через подстановку
        // переменных; Template и TemplateSet — НЕТ, они уезжают в примитивы как есть. Разбор,
        // который сообщал бы про чтение переменной, врал бы о поведении движка — ровно та же
        // ошибка, что бейдж целей, расходящийся с SelectorEvaluator.
        var usage = MacroTemplateAnalysis.Analyze(
            [Macro("подстановки-нет", new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "иконка-{tag}" })]);

        await Assert.That(usage).Count().IsEqualTo(1);
        await Assert.That(usage[0].Name).IsEqualTo("иконка-{tag}");
    }

    [Test]
    public async Task Analyze_IgnoresEmptyNames_AndNodesWithoutTemplates()
    {
        // Незаполненное поле — забота валидатора, а не браузера: строка «(пусто)» в списке
        // шаблонов была бы шумом на месте, где о ней всё равно нечего сказать.
        var usage = MacroTemplateAnalysis.Analyze(
        [
            Macro(
                "пусто",
                new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "   " },
                new DelayNode { Id = Ids.Of("delay"), DisplayName = "delay", Ms = 10 }),
        ]);

        await Assert.That(usage).IsEmpty();
    }

    [Test]
    public async Task Analyze_OfAnEmptyLibrary_IsEmpty()
    {
        await Assert.That(MacroTemplateAnalysis.Analyze([])).IsEmpty();
    }
}
