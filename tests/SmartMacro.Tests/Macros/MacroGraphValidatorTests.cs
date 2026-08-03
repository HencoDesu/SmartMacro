using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: static validation — every rule fires on a crafted graph, clean graphs pass.
public class MacroGraphValidatorTests
{
    private static readonly TargetSelector AnySelector = new() { RequireTags = ["перс"] };
    private static readonly MacroTrigger Hotkey = new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23);
    private static readonly MacroTrigger Process = new ProcessAppearedTrigger("elementclient");

    private static MacroGraph Graph(string startId, List<MacroTrigger> triggers, params MacroNode[] nodes) =>
        new() { Name = "м", Triggers = triggers, StartNodeId = startId, Nodes = [.. nodes] };

    private static IReadOnlyList<ValidationIssue> Errors(IReadOnlyList<ValidationIssue> issues) =>
        issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();

    private static IReadOnlyList<ValidationIssue> Warnings(IReadOnlyList<ValidationIssue> issues) =>
        issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();

    [Test]
    public async Task CleanContextMacro_PassesWithoutIssues()
    {
        // Process-triggered boot-style macro: conditionals and targetless actions are fine.
        var graph = Graph("w", [Process],
            new WaitForElementNode { Id = "w", Template = "мир", TimeoutMs = 60000, Found = "k", Timeout = null },
            new KeyPressNode { Id = "k", Key = VirtualKey.C, Next = "d" },
            new DelayNode { Id = "d", Ms = 100, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task CleanHotkeyMacro_WithSelectorsEverywhere_Passes()
    {
        var graph = Graph("k", [Hotkey],
            new KeyPressNode { Id = "k", Key = VirtualKey.F8, Target = AnySelector, Next = "c" },
            new ClickNode { Id = "c", Point = new ScreenPoint(1, 2), Target = AnySelector, Next = "d" },
            new DelayNode { Id = "d", Ms = 100, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task UnknownStartNodeId_IsGraphLevelError()
    {
        var graph = Graph("призрак", [Process],
            new DelayNode { Id = "d", Ms = 1, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        var startErrors = Errors(issues).Where(i => i.NodeId is null).ToList();
        await Assert.That(startErrors).Count().IsEqualTo(1);
        await Assert.That(startErrors[0].Message).Contains("StartNodeId");
    }

    [Test]
    public async Task DuplicateNodeIds_AreErrors()
    {
        var graph = Graph("d", [Process],
            new DelayNode { Id = "d", Ms = 1, Next = null },
            new DelayNode { Id = "d", Ms = 2, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.Message.Contains("Дубликат"))).IsTrue();
    }

    [Test]
    public async Task EdgeToUnknownNode_IsError()
    {
        var graph = Graph("f", [Process],
            new FindElementNode { Id = "f", Template = "т", Found = "есть", NotFound = "нету" },
            new DelayNode { Id = "есть", Ms = 1, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        var edgeErrors = Errors(issues).Where(i => i.NodeId == "f").ToList();
        await Assert.That(edgeErrors).Count().IsEqualTo(1);
        await Assert.That(edgeErrors[0].Message).Contains("нету");
    }

    [Test]
    public async Task ClickNode_BothPointAndPointVar_IsError()
    {
        var graph = Graph("c", [Process],
            new ClickNode { Id = "c", Point = new ScreenPoint(1, 1), PointVar = "cursor", Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeId == "c" && i.Message.Contains("ровно одно"))).IsTrue();
    }

    [Test]
    public async Task ClickNode_NeitherPointNorPointVar_IsError()
    {
        var graph = Graph("c", [Process],
            new ClickNode { Id = "c", Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeId == "c" && i.Message.Contains("ровно одно"))).IsTrue();
    }

    [Test]
    public async Task HotkeyOnlyMacro_ReachableConditional_IsContextError()
    {
        var graph = Graph("f", [Hotkey],
            new FindElementNode { Id = "f", Template = "т", Found = null, NotFound = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeId == "f" && i.Message.Contains("контекстное окно"))).IsTrue();
    }

    [Test]
    public async Task ProcessTrigger_MakesTheSameConditionalLegal()
    {
        var graph = Graph("f", [Hotkey, Process],
            new FindElementNode { Id = "f", Template = "т", Found = null, NotFound = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task HotkeyOnlyMacro_TargetlessAction_IsContextError()
    {
        var graph = Graph("k", [Hotkey],
            new KeyPressNode { Id = "k", Key = VirtualKey.F8, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues).Any(i => i.NodeId == "k" && i.Message.Contains("контекстное окно"))).IsTrue();
    }

    // W0.2b relaxation: a graph with NO triggers can only be entered via RunMacroNode or
    // a UI Run against a window, so its context always comes from the caller. Targetless
    // nodes are the CORRECT shape for such a library routine — that's what makes it
    // reusable per window — so the context rule must not fire.
    [Test]
    public async Task TriggerlessLibraryMacro_ConditionalAndTargetlessAction_AreLegal()
    {
        var graph = Graph("k", [],
            new KeyPressNode { Id = "k", Key = VirtualKey.C, Next = "r" },
            new RecognizeTagNode
            {
                Id = "r",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 10, 10),
                Matched = "i",
                NotMatched = null,
            },
            new SetIconNode { Id = "i", IconPath = "icons/{tag}.png", Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task AddingAHotkeyToALibraryMacro_BringsTheContextErrorBack()
    {
        var nodes = new MacroNode[]
        {
            new KeyPressNode { Id = "k", Key = VirtualKey.C, Next = null },
        };

        var libraryOnly = Graph("k", [], nodes);
        var triggered = Graph("k", [Hotkey], nodes);

        await Assert.That(Errors(MacroGraphValidator.Validate(libraryOnly))).Count().IsEqualTo(0);
        await Assert.That(Errors(MacroGraphValidator.Validate(triggered)).Any(i => i.Message.Contains("контекстное окно"))).IsTrue();
    }

    [Test]
    public async Task HotkeyOnlyMacro_UnreachableConditional_NoContextError_ButUnreachableWarning()
    {
        var graph = Graph("d", [Hotkey],
            new DelayNode { Id = "d", Ms = 1, Next = null },
            new FindElementNode { Id = "f", Template = "т", Found = null, NotFound = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Errors(issues)).Count().IsEqualTo(0);
        var unreachable = Warnings(issues).Where(i => i.NodeId == "f").ToList();
        await Assert.That(unreachable).Count().IsEqualTo(1);
        await Assert.That(unreachable[0].Message).Contains("недостижима");
    }

    [Test]
    public async Task UnreachableNode_IsWarning()
    {
        var graph = Graph("d", [Process],
            new DelayNode { Id = "d", Ms = 1, Next = null },
            new DelayNode { Id = "остров", Ms = 2, Next = null });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Warnings(issues).Any(i => i.NodeId == "остров" && i.Message.Contains("недостижима"))).IsTrue();
    }

    [Test]
    public async Task CycleWithoutPause_IsHotLoopWarning()
    {
        var graph = Graph("k1", [Process],
            new KeyPressNode { Id = "k1", Key = VirtualKey.F1, Next = "k2" },
            new KeyPressNode { Id = "k2", Key = VirtualKey.F2, Next = "k1" });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Warnings(issues).Any(i => i.Message.Contains("вхолостую"))).IsTrue();
    }

    [Test]
    public async Task SelfLoopWithoutPause_IsHotLoopWarning()
    {
        var graph = Graph("k", [Process],
            new KeyPressNode { Id = "k", Key = VirtualKey.F1, Next = "k" });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(Warnings(issues).Any(i => i.Message.Contains("вхолостую"))).IsTrue();
    }

    [Test]
    public async Task CycleWithDelay_IsNotAHotLoop()
    {
        var graph = Graph("k1", [Process],
            new KeyPressNode { Id = "k1", Key = VirtualKey.F1, Next = "d" },
            new DelayNode { Id = "d", Ms = 500, Next = "k1" });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }

    [Test]
    public async Task CycleThroughWaitForElement_IsNotAHotLoop()
    {
        var graph = Graph("w", [Process],
            new WaitForElementNode { Id = "w", Template = "т", TimeoutMs = 1000, Found = null, Timeout = "k" },
            new KeyPressNode { Id = "k", Key = VirtualKey.F1, Next = "w" });

        var issues = MacroGraphValidator.Validate(graph);

        await Assert.That(issues).Count().IsEqualTo(0);
    }
}
