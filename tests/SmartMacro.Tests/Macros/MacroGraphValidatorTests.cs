using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: статическая проверка — каждое правило срабатывает на нарочно собранном графе, а чистые
// графы проходят.
public class MacroGraphValidatorTests
{
    private static readonly TargetSelector AnySelector = new() { RequireTags = ["перс"] };
    private static readonly MacroTrigger Hotkey = new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23);
    private static readonly MacroTrigger Process = new ProcessAppearedTrigger("elementclient");

    private static MacroGraph Graph(Guid startId, List<MacroTrigger> triggers, params MacroNode[] nodes) =>
        new() { Name = "м", Triggers = triggers, StartNodeId = startId, Nodes = [.. nodes] };

    private static IReadOnlyList<ValidationIssue> Errors(IReadOnlyList<ValidationIssue> issues) =>
        issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();

    private static IReadOnlyList<ValidationIssue> Warnings(IReadOnlyList<ValidationIssue> issues) =>
        issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();

    [Test]
    public async Task CleanContextMacro_PassesWithoutIssues()
    {
        // Макрос загрузочного вида с триггером по процессу: условные ноды и действия без цели
        // здесь в порядке.
        var graph = Graph(Ids.Of("w"), [Process],
            new WaitForElementNode { Id = Ids.Of("w"), DisplayName = "w", Template = "мир", TimeoutMs = 60000, Found = Ids.Of("k"), Timeout = null },
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.C, Next = Ids.Of("d") },
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 100, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task CleanHotkeyMacro_WithSelectorsEverywhere_Passes()
    {
        var graph = Graph(Ids.Of("k"), [Hotkey],
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.F8, Target = AnySelector, Next = Ids.Of("c") },
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", Point = new ScreenPoint(1, 2), Target = AnySelector, Next = Ids.Of("d") },
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 100, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task UnknownStartNodeId_IsGraphLevelError()
    {
        var graph = Graph(Ids.Of("призрак"), [Process],
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 1, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        var startErrors = Errors(issues).Where(i => i.NodeId is null).ToList();
        await Assert.That(startErrors).Count().IsEqualTo(1);
        await Assert.That(startErrors[0].Message).Contains("Стартовая нода");
    }

    [Test]
    public async Task DuplicateNodeIds_AreErrors()
    {
        var graph = Graph(Ids.Of("d"), [Process],
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 1, Next = null },
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 2, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.Message.Contains("Дубликат"))).IsTrue();
    }

    [Test]
    public async Task EdgeToUnknownNode_IsError()
    {
        var graph = Graph(Ids.Of("f"), [Process],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "f", Template = "т", Found = Ids.Of("есть"), NotFound = Ids.Of("нету") },
            new DelayNode { Id = Ids.Of("есть"), DisplayName = "есть", Ms = 1, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        var edgeErrors = Errors(issues).Where(i => i.NodeName == "f").ToList();
        await Assert.That(edgeErrors).Count().IsEqualTo(1);
        // Цель ребра не называется: ноды с таким id в графе нет, а важно, ЧЕЙ исход повис.
        await Assert.That(edgeErrors[0].Message).Contains("NotFound");
    }

    [Test]
    public async Task ClickNode_BothPointAndPointVar_IsError()
    {
        var graph = Graph(Ids.Of("c"), [Process],
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", Point = new ScreenPoint(1, 1), PointVar = "cursor", Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeName == "c" && i.Message.Contains("ровно одно"))).IsTrue();
    }

    [Test]
    public async Task ClickNode_NeitherPointNorPointVar_IsError()
    {
        var graph = Graph(Ids.Of("c"), [Process],
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeName == "c" && i.Message.Contains("ровно одно"))).IsTrue();
    }

    [Test]
    public async Task HotkeyOnlyMacro_ReachableConditional_IsContextError()
    {
        var graph = Graph(Ids.Of("f"), [Hotkey],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "f", Template = "т", Found = null, NotFound = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeName == "f" && i.Message.Contains("контекстное окно"))).IsTrue();
    }

    [Test]
    public async Task ProcessTrigger_MakesTheSameConditionalLegal()
    {
        var graph = Graph(Ids.Of("f"), [Hotkey, Process],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "f", Template = "т", Found = null, NotFound = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task HotkeyOnlyMacro_TargetlessAction_IsContextError()
    {
        var graph = Graph(Ids.Of("k"), [Hotkey],
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.F8, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeName == "k" && i.Message.Contains("контекстное окно"))).IsTrue();
    }

    // Послабление W0.2b: в граф БЕЗ триггеров можно войти только через веер селектора или через
    // «Запустить» из интерфейса на конкретном окне, так что контекст ему всегда приходит от
    // вызывающего. Ноды без цели — ПРАВИЛЬНАЯ форма для такой библиотечной подпрограммы, именно
    // она и делает её пригодной к повторному применению на каждом окне, — а значит, правило про
    // контекст срабатывать не должно.
    [Test]
    public async Task TriggerlessLibraryMacro_ConditionalAndTargetlessAction_AreLegal()
    {
        var graph = Graph(Ids.Of("k"), [],
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.C, Next = Ids.Of("r") },
            new RecognizeTagNode
            {
                Id = Ids.Of("r"), DisplayName = "r",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 10, 10),
                Matched = Ids.Of("i"),
                NotMatched = null,
            },
            new SetIconNode { Id = Ids.Of("i"), DisplayName = "i", IconPath = "icons/{tag}.png", Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task AddingAHotkeyToALibraryMacro_BringsTheContextErrorBack()
    {
        var nodes = new MacroNode[]
        {
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.C, Next = null },
        };

        var libraryOnly = Graph(Ids.Of("k"), [], nodes);
        var triggered = Graph(Ids.Of("k"), [Hotkey], nodes);

        await Assert.That(Errors(MacroGraphValidator.Validate(libraryOnly))).Count().IsEqualTo(0);
        await Assert.That(Errors(MacroGraphValidator.Validate(triggered)).Any(i => i.Message.Contains("контекстное окно"))).IsTrue();
    }

    [Test]
    public async Task HotkeyOnlyMacro_UnreachableConditional_NoContextError_ButUnreachableWarning()
    {
        var graph = Graph(Ids.Of("d"), [Hotkey],
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 1, Next = null },
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "f", Template = "т", Found = null, NotFound = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues)).Count().IsEqualTo(0);
        var unreachable = Warnings(issues).Where(i => i.NodeName == "f").ToList();
        await Assert.That(unreachable).Count().IsEqualTo(1);
        await Assert.That(unreachable[0].Message).Contains("недостижима");
    }

    [Test]
    public async Task UnreachableNode_IsWarning()
    {
        var graph = Graph(Ids.Of("d"), [Process],
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 1, Next = null },
            new DelayNode { Id = Ids.Of("остров"), DisplayName = "остров", Ms = 2, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Warnings(issues).Any(i => i.NodeName == "остров" && i.Message.Contains("недостижима"))).IsTrue();
    }

    [Test]
    public async Task CycleWithoutPause_IsHotLoopWarning()
    {
        var graph = Graph(Ids.Of("k1"), [Process],
            new KeyPressNode { Id = Ids.Of("k1"), DisplayName = "k1", Key = VirtualKey.F1, Next = Ids.Of("k2") },
            new KeyPressNode { Id = Ids.Of("k2"), DisplayName = "k2", Key = VirtualKey.F2, Next = Ids.Of("k1") });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Warnings(issues).Any(i => i.Message.Contains("вхолостую"))).IsTrue();
    }

    [Test]
    public async Task SelfLoopWithoutPause_IsHotLoopWarning()
    {
        var graph = Graph(Ids.Of("k"), [Process],
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.F1, Next = Ids.Of("k") });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Warnings(issues).Any(i => i.Message.Contains("вхолостую"))).IsTrue();
    }

    [Test]
    public async Task CycleWithDelay_IsNotAHotLoop()
    {
        var graph = Graph(Ids.Of("k1"), [Process],
            new KeyPressNode { Id = Ids.Of("k1"), DisplayName = "k1", Key = VirtualKey.F1, Next = Ids.Of("d") },
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 500, Next = Ids.Of("k1") });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task CycleThroughWaitForElement_IsNotAHotLoop()
    {
        var graph = Graph(Ids.Of("w"), [Process],
            new WaitForElementNode { Id = Ids.Of("w"), DisplayName = "w", Template = "т", TimeoutMs = 1000, Found = null, Timeout = Ids.Of("k") },
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.F1, Next = Ids.Of("w") });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }
    // ---- подписи и порог (волна «Guid + DisplayName») ----------------------------------------

    [Test]
    public async Task DuplicateDisplayName_IsAWarning_NotAnError()
    {
        // Подпись ни на что не влияет, кроме читаемости: граф с двумя «Клик» исполняется
        // однозначно. Но строка лога «0:01.2 · Клик · ок», встретившаяся дважды, читателю уже
        // ничего не говорит — на канве неоднозначность снимет подсветка, а в тексте нечем.
        var graph = Graph(Ids.Of("a"), [Process],
            new DelayNode { Id = Ids.Of("a"), DisplayName = "Клик", Ms = 1, Next = Ids.Of("b") },
            new DelayNode { Id = Ids.Of("b"), DisplayName = "Клик", Ms = 1 });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues)).IsEmpty();
        var duplicates = Warnings(issues).Where(i => i.Message.Contains("носит больше одной ноды")).ToList();
        // Помечены ОБЕ: клик по замечанию должен подсвечивать ту ноду, о которой оно говорит.
        await Assert.That(duplicates).Count().IsEqualTo(2);
        await Assert.That(duplicates.Select(i => i.NodeId))
            .IsEquivalentTo(new Guid?[] { Ids.Of("a"), Ids.Of("b") });
    }

    [Test]
    [Arguments(0.0, false)]
    [Arguments(-0.5, false)]
    [Arguments(1.5, false)]
    [Arguments(0.85, true)]
    [Arguments(1.0, true)]
    public async Task MatchThresholdOutsideZeroToOne_IsAnError(double threshold, bool expectedClean)
    {
        // Ноль означал бы «совпадает что угодно», выше единицы — «не совпадёт никогда»: и то и
        // другое не настройка точности, а выключенная нода.
        var graph = Graph(Ids.Of("f"), [Process],
            new FindElementNode
            {
                Id = Ids.Of("f"), DisplayName = "find-1", Template = "т", MatchThreshold = threshold,
            });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.Message.Contains("Порог совпадения")))
            .IsEqualTo(!expectedClean);
    }

    [Test]
    public async Task NoMatchThreshold_IsClean_BecauseNullMeansTheVisionDefault()
    {
        var graph = Graph(Ids.Of("f"), [Process],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "find-1", Template = "т" });

        await Assert.That(MacroGraphValidator.Validate(graph)).IsEmpty();
    }
    // ---- шаблоны бандла (F2) ----------------------------------------------------------------

    [Test]
    public async Task ANodeNamingATemplateTheBundleDoesNotHave_IsAWarningOnThatNode()
    {
        // Повышение класса ошибки: раньше это выяснялось из раздела «НЕТ ФАЙЛА» в отдельном
        // режиме (куда надо было пойти) либо строчкой в журнале посреди прогона (когда уже
        // поздно). Набор шаблонов бандла известен статически — значит, и сказать можно статически.
        var graph = Graph(Ids.Of("f"), [Process],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "find-1", Template = "КнопкаКоторойНет" });

        var issues = MacroGraphValidator.Validate(graph, MacroTemplateInventory.Empty);

        await Assert.That(Errors(issues)).IsEmpty();
        var missing = Warnings(issues).Single(i => i.Message.Contains("нет шаблона"));
        await Assert.That(missing.NodeId).IsEqualTo(Ids.Of("f"));
        await Assert.That(missing.Message).Contains("КнопкаКоторойНет");
    }

    // ПРЕДУПРЕЖДЕНИЕ, а не ошибка: ошибка запрещает сохранение, а «набрал имя → импортировал
    // файл» — нормальный порядок действий. К тому же ненайденный шаблон прогон не обрывает, нода
    // честно уходит по «не найдено».
    [Test]
    public async Task AMissingTemplate_DoesNotBlockSaving()
    {
        var graph = Graph(Ids.Of("f"), [Process],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "find-1", Template = "нет" });

        await Assert.That(MacroGraphValidator.Validate(graph, MacroTemplateInventory.Empty)
            .All(i => i.Severity == ValidationSeverity.Warning)).IsTrue();
    }

    [Test]
    public async Task AMissingSet_SaysSetRatherThanTemplate()
    {
        // «Нет набора» и «нет файла» — разные новости: в первом случае не хватает целой папки, и
        // искать надо не тот же файл.
        var graph = Graph(Ids.Of("r"), [Process],
            new RecognizeTagNode
            {
                Id = Ids.Of("r"), DisplayName = "recognize-1", TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 10, 10),
            });

        var issues = MacroGraphValidator.Validate(graph, MacroTemplateInventory.Empty);

        await Assert.That(Warnings(issues).Single(i => i.Message.Contains("набора")).Message).Contains("classes");
    }

    [Test]
    public async Task TemplatesThatAreInTheBundle_AreClean()
    {
        var graph = Graph(Ids.Of("f"), [Process],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "find-1", Template = "Кнопка", Found = Ids.Of("r") },
            new RecognizeTagNode
            {
                Id = Ids.Of("r"), DisplayName = "recognize-1", TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 10, 10),
            });
        var inventory = MacroTemplateInventory.FromPaths(["Кнопка.png", "classes/Лучник.png"]);

        await Assert.That(MacroGraphValidator.Validate(graph, inventory)).IsEmpty();
    }

    // null и пустая опись — разные вещи. null значит «состав бандла неизвестен», и обвинить ноду
    // в ссылке на несуществующий файл, не посмотрев в файл, значило бы соврать: именно так зовёт
    // валидатор редактор, пересчитывая предупреждения по несохранённому черновику.
    [Test]
    public async Task WithoutAnInventory_TheTemplateCheckIsSkippedEntirely()
    {
        var graph = Graph(Ids.Of("f"), [Process],
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "find-1", Template = "нет" });

        await Assert.That(MacroGraphValidator.Validate(graph)).IsEmpty();
    }
}
