using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2a: как ноды ЧИТАЮТ переменные — клики по PointVar, подстановка {var} в Tag и IconPath,
// а также жёсткое правило: отсутствующая переменная прерывает прогон, а не тихо промахивается.
public class MacroExecutorVariableTests
{
    [Test]
    public async Task Click_WithPointVar_UsesTriggerCursorVariable()
    {
        var h = new ExecutorHarness();
        var variables = MacroVariables.ForTrigger(new ScreenPoint(640, 360));
        var graph = ExecutorHarness.Graph("м", Ids.Of("c"),
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", PointVar = "cursor", DoubleClick = true, Next = null });

        var result =
            await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window, variables), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls[0])
            .IsEqualTo(new RecordingPrimitives.Call("Click", ExecutorHarness.Window, new ScreenPoint(640, 360), true));
    }

    [Test]
    public async Task Click_WithLiteralPoint_UsesIt()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", Ids.Of("c"),
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", Point = new ScreenPoint(11, 22), Next = null });

        await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(h.Primitives.Calls[0])
            .IsEqualTo(new RecordingPrimitives.Call("Click", ExecutorHarness.Window, new ScreenPoint(11, 22), false));
    }

    [Test]
    public async Task Click_MissingPointVar_AbortsRun()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", Ids.Of("c"),
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", PointVar = "нет-такой", Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("нет-такой");
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Click_PointVarHoldingNonPoint_AbortsRun()
    {
        var h = new ExecutorHarness();
        var variables = new MacroVariables();
        variables.Set("btn", "строка");
        var graph = ExecutorHarness.Graph("м", Ids.Of("c"),
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", PointVar = "btn", Next = null });

        var result =
            await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window, variables), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Click_BothPointAndPointVar_AbortsRun()
    {
        var h = new ExecutorHarness();
        var variables = MacroVariables.ForTrigger(new ScreenPoint(1, 1));
        var graph = ExecutorHarness.Graph("м", Ids.Of("c"),
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", Point = new ScreenPoint(2, 2), PointVar = "cursor", Next = null });

        var result =
            await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window, variables), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("ровно одно");
    }

    [Test]
    public async Task Click_NeitherPointNorPointVar_AbortsRun()
    {
        var h = new ExecutorHarness();
        var graph = ExecutorHarness.Graph("м", Ids.Of("c"), new ClickNode { Id = Ids.Of("c"), DisplayName = "c", Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("ровно одно");
    }

    [Test]
    public async Task AddTag_InterpolatesVariableIntoTag()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        var variables = new MacroVariables();
        variables.Set("tag", "виз");
        var graph = ExecutorHarness.Graph("м", Ids.Of("t"),
            new AddTagNode { Id = Ids.Of("t"), DisplayName = "t", Tag = "класс-{tag}", Next = null });

        var result =
            await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window, variables), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Registry.HasTag(ExecutorHarness.Window, "класс-виз")).IsTrue();
    }

    [Test]
    public async Task SetIcon_InterpolatesVariableIntoPath()
    {
        var h = new ExecutorHarness();
        var variables = new MacroVariables();
        variables.Set("tag", "жрец");
        var graph = ExecutorHarness.Graph("м", Ids.Of("i"),
            new SetIconNode { Id = Ids.Of("i"), DisplayName = "i", IconPath = "icons/{tag}.png", Next = null });

        await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window, variables), CancellationToken.None);

        await Assert.That(h.Primitives.Calls[0])
            .IsEqualTo(new RecordingPrimitives.Call("SetIcon", ExecutorHarness.Window, "icons/жрец.png"));
    }

    [Test]
    public async Task Interpolation_MissingVariable_AbortsRun()
    {
        var h = new ExecutorHarness();
        h.Registry.Register(ExecutorHarness.Window, "elementclient");
        var graph = ExecutorHarness.Graph("м", Ids.Of("t"),
            new AddTagNode { Id = Ids.Of("t"), DisplayName = "t", Tag = "{неопределённая}", Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Aborted);
        await Assert.That(result.Error!).Contains("неопределённая");
        await Assert.That(h.Registry.GetTags(ExecutorHarness.Window)).Count().IsEqualTo(0);
    }

    [Test]
    public async Task FindThenClickPattern_ClicksTheFoundPoint()
    {
        // Заглавный приём из плана: Find(FoundPointVar: "btn") →Found→ Click(PointVar: "btn").
        var h = new ExecutorHarness();
        h.Primitives.FindHandler = (_, _, _) => new ScreenPoint(300, 400);
        var graph = ExecutorHarness.Graph("м", Ids.Of("f"),
            new FindElementNode { Id = Ids.Of("f"), DisplayName = "f", Template = "кнопка", FoundPointVar = "btn", Found = Ids.Of("c"), NotFound = null },
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", PointVar = "btn", Next = null });

        var result = await h.Executor.RunAsync(graph, h.Context(ExecutorHarness.Window), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        await Assert.That(h.Primitives.Calls).Count().IsEqualTo(2);
        await Assert.That(h.Primitives.Calls[1])
            .IsEqualTo(new RecordingPrimitives.Call("Click", ExecutorHarness.Window, new ScreenPoint(300, 400), false));
    }
}
