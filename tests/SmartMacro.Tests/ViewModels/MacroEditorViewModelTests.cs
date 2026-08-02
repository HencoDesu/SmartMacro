using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;

namespace SmartMacro.Tests.ViewModels;

// W0.3: the macro editor's behaviour, headless.
//
// The store is the real thing over a temp folder rather than a fake: MacroGraphStore is a
// sealed class (FakeItEasy needs an interface or a virtual), and "the file is/isn't on
// disk" is a sharper assertion for the save-gating tests than "the method was/wasn't
// called". External-edit scenarios are driven by calling SaveAsync on the same store
// instead of waiting on FileSystemWatcher — MacrosChanged is the seam under test, and the
// watcher's debounce is already covered by MacroGraphStoreTests.
public class MacroEditorViewModelTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // The watcher may still hold the folder; temp cleanup isn't the assertion.
        }
    }

    private static MacroGraphStore CreateStore(string dir) =>
        new(dir, NullLogger<MacroGraphStore>.Instance, seedDefaults: false);

    private static MacroRunRegistry CreateRuns() => new(NullLogger<MacroRunRegistry>.Instance);

    private static MacroEditorViewModel CreateEditor(
        MacroGraphStore store,
        MacroRunRegistry? runs = null,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null) =>
        new(store, runs ?? CreateRuns(), launcher, hotkeys, ImmediateUiDispatcher.Instance);

    /// <summary>a → b → c, all Delay nodes, no triggers (so the context rule doesn't apply).</summary>
    private static MacroGraph Chain(string name = "цепочка") => new()
    {
        Name = name,
        StartNodeId = "a",
        Nodes =
        [
            new DelayNode { Id = "a", Ms = 100, Next = "b" },
            new DelayNode { Id = "b", Ms = 200, Next = "c" },
            new DelayNode { Id = "c", Ms = 300 },
        ],
    };

    private static string PathOf(string dir, string name) => Path.Combine(dir, "macros", $"{name}.json");

    // ---- structure editing ------------------------------------------------------------

    [Test]
    public async Task DeleteNode_ClearsEveryInboundEdge()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain());

            vm.DeleteNode(vm.Nodes.Single(n => n.NodeId == "b"));

            var graph = vm.BuildGraph();
            await Assert.That(graph.Nodes).Count().IsEqualTo(2);
            await Assert.That(((DelayNode)graph.Nodes[0]).Next).IsNull();
            await Assert.That(graph.StartNodeId).IsEqualTo("a");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task DeleteStartNode_MovesTheStartToWhatIsLeft()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain());

            vm.DeleteNode(vm.Nodes.Single(n => n.NodeId == "a"));

            await Assert.That(vm.StartNodeId).IsEqualTo("b");
            await Assert.That(vm.BuildGraph().StartNodeId).IsEqualTo("b");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task DeletingEveryNode_LeavesAnEmptyStart_WithoutThrowing()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain());

            while (vm.Nodes.Count > 0)
            {
                vm.DeleteNode(vm.Nodes[0]);
            }

            await Assert.That(vm.StartNodeId).IsEqualTo(string.Empty);
            await Assert.That(vm.BuildGraph().Nodes).IsEmpty();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task RenamingANode_RepointsInboundEdgesAndTheStartNode()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain());

            vm.Nodes.Single(n => n.NodeId == "a").NodeId = "начало";
            vm.Nodes.Single(n => n.NodeId == "b").NodeId = "середина";

            var graph = vm.BuildGraph();
            await Assert.That(graph.StartNodeId).IsEqualTo("начало");
            await Assert.That(((DelayNode)graph.Nodes[0]).Next).IsEqualTo("середина");
            await Assert.That(((DelayNode)graph.Nodes[1]).Next).IsEqualTo("c");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task BlankNodeId_IsRejected()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain());

            vm.Nodes[0].NodeId = "   ";

            await Assert.That(vm.Nodes[0].NodeId).IsEqualTo("a");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task AddNode_GeneratesUniqueIds_AndSeedsTheStartOfAnEmptyGraph()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(new MacroGraph { Name = "пусто", StartNodeId = string.Empty, Nodes = [] });

            var first = vm.AddNode(MacroNodeKind.KeyPress);
            var second = vm.AddNode(MacroNodeKind.Click);

            await Assert.That(first.NodeId).IsEqualTo("n1");
            await Assert.That(second.NodeId).IsEqualTo("n2");
            await Assert.That(vm.StartNodeId).IsEqualTo("n1");
            // Both new ids plus the "end of run" entry must be offered to every edge.
            await Assert.That(vm.NodeIdChoices).IsEquivalentTo(new[] { string.Empty, "n1", "n2" });
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task MoveNode_ReordersTheListWithoutTouchingEdges()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain());

            vm.MoveNodeDown(vm.Nodes[0]);

            var graph = vm.BuildGraph();
            await Assert.That(graph.Nodes.Select(n => n.Id)).IsEquivalentTo(new[] { "b", "a", "c" });
            await Assert.That(((DelayNode)graph.Nodes.Single(n => n.Id == "a")).Next).IsEqualTo("b");
            await Assert.That(graph.StartNodeId).IsEqualTo("a");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ---- save gating --------------------------------------------------------------------

    [Test]
    public async Task Save_WritesAValidGraph()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain("рабочий"));

            var saved = await vm.SaveAsync();

            await Assert.That(saved).IsTrue();
            await Assert.That(File.Exists(PathOf(dir, "рабочий"))).IsTrue();
            await Assert.That(store.TryGet("рабочий")).IsNotNull();
            await Assert.That(vm.Issues.Any(i => i.IsError)).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_IsBlockedByAValidatorError_AndNothingReachesDisk()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);

            // A hotkey macro has no context window, so a reachable conditional node is an
            // ERROR — the classic authoring mistake the validator exists to catch.
            vm.LoadGraph(new MacroGraph
            {
                Name = "битый",
                Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F13)],
                StartNodeId = "find",
                Nodes = [new FindElementNode { Id = "find", Template = "Btn" }],
            });

            var saved = await vm.SaveAsync();

            await Assert.That(saved).IsFalse();
            await Assert.That(File.Exists(PathOf(dir, "битый"))).IsFalse();
            await Assert.That(store.TryGet("битый")).IsNull();
            await Assert.That(vm.Issues.Any(i => i.IsError)).IsTrue();
            await Assert.That(vm.ErrorMessage).IsNotNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_IsBlockedByARowLevelInputError()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain("кривая-пауза"));
            ((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText = "две секунды";

            var saved = await vm.SaveAsync();

            await Assert.That(saved).IsFalse();
            await Assert.That(File.Exists(PathOf(dir, "кривая-пауза"))).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_IsBlockedByAnUnusableName()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain());
            vm.MacroName = "плохое/имя";

            var saved = await vm.SaveAsync();

            await Assert.That(saved).IsFalse();
            await Assert.That(store.All).IsEmpty();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_AllowsWarnings()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            // "orphan" is unreachable from the start node — a warning, not an error.
            vm.LoadGraph(new MacroGraph
            {
                Name = "с-предупреждением",
                StartNodeId = "a",
                Nodes =
                [
                    new DelayNode { Id = "a", Ms = 100 },
                    new DelayNode { Id = "orphan", Ms = 100 },
                ],
            });

            var saved = await vm.SaveAsync();

            await Assert.That(saved).IsTrue();
            await Assert.That(vm.Issues.Any(i => !i.IsError)).IsTrue();
            await Assert.That(vm.Issues.Any(i => i.IsError)).IsFalse();
            await Assert.That(store.TryGet("с-предупреждением")).IsNotNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Rename_WritesTheNewFileAndRemovesTheOldOne()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain("старое-имя"));
            await vm.SaveAsync();

            vm.MacroName = "новое-имя";
            var saved = await vm.SaveAsync();

            await Assert.That(saved).IsTrue();
            await Assert.That(File.Exists(PathOf(dir, "новое-имя"))).IsTrue();
            await Assert.That(File.Exists(PathOf(dir, "старое-имя"))).IsFalse();
            await Assert.That(store.TryGet("старое-имя")).IsNull();
            await Assert.That(store.TryGet("новое-имя")).IsNotNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_RoundTripsAGraphWithEveryNodeTypeThroughTheStore()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            var original = NodeRowRoundTripTests.EveryNodeType();
            vm.LoadGraph(original);

            await Assert.That(await vm.SaveAsync()).IsTrue();

            var reloaded = store.TryGet(original.Name);
            await Assert.That(reloaded).IsNotNull();
            await Assert.That(MacroGraphJson.Serialize(reloaded!))
                .IsEqualTo(MacroGraphJson.Serialize(original));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ---- library / hot reload -----------------------------------------------------------

    [Test]
    public async Task SelectingAMacro_LoadsItIntoTheEditor()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(Chain("первый"));
            await store.SaveAsync(Chain("второй"));
            using var vm = CreateEditor(store);

            await Assert.That(vm.Macros).Count().IsEqualTo(2);
            await Assert.That(vm.HasOpenMacro).IsFalse();

            vm.SelectedMacro = vm.Macros.Single(m => m.Name == "второй");

            await Assert.That(vm.HasOpenMacro).IsTrue();
            await Assert.That(vm.MacroName).IsEqualTo("второй");
            await Assert.That(vm.Nodes).Count().IsEqualTo(3);
            await Assert.That(vm.IsDirty()).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task ExternalChange_ReloadsACleanEditor()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(Chain("живой"));
            using var vm = CreateEditor(store);
            vm.SelectedMacro = vm.Macros.Single();

            // Someone edits the file behind our back.
            await store.SaveAsync(new MacroGraph
            {
                Name = "живой",
                StartNodeId = "only",
                Nodes = [new DelayNode { Id = "only", Ms = 5000 }],
            });

            await Assert.That(vm.ChangedOnDisk).IsFalse();
            await Assert.That(vm.Nodes).Count().IsEqualTo(1);
            await Assert.That(((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText).IsEqualTo("5");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task ExternalChange_DoesNotClobberUnsavedEdits()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(Chain("живой"));
            using var vm = CreateEditor(store);
            vm.SelectedMacro = vm.Macros.Single();
            ((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText = "9";

            await store.SaveAsync(new MacroGraph
            {
                Name = "живой",
                StartNodeId = "only",
                Nodes = [new DelayNode { Id = "only", Ms = 5000 }],
            });

            await Assert.That(vm.ChangedOnDisk).IsTrue();
            await Assert.That(vm.Nodes).Count().IsEqualTo(3);
            await Assert.That(((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText).IsEqualTo("9");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task OurOwnSave_IsNotMistakenForAnExternalChange()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(Chain("своё"));

            await vm.SaveAsync();

            await Assert.That(vm.ChangedOnDisk).IsFalse();
            await Assert.That(vm.IsDirty()).IsFalse();
            await Assert.That(vm.SelectedMacro?.Name).IsEqualTo("своё");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task NewMacro_ProducesASaveableDraftThatIsNotYetOnDisk()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);

            vm.NewMacro();

            await Assert.That(vm.HasOpenMacro).IsTrue();
            await Assert.That(vm.Nodes).Count().IsEqualTo(1);
            await Assert.That(store.All).IsEmpty();

            await Assert.That(await vm.SaveAsync()).IsTrue();
            await Assert.That(store.All).Count().IsEqualTo(1);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task DeleteMacro_RemovesItFromTheLibraryAndClosesTheEditor()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(Chain("на-удаление"));
            using var vm = CreateEditor(store);
            vm.SelectedMacro = vm.Macros.Single();

            var deleted = await vm.DeleteMacroAsync(vm.Macros.Single());

            await Assert.That(deleted).IsTrue();
            await Assert.That(vm.Macros).IsEmpty();
            await Assert.That(vm.HasOpenMacro).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ---- run / stop / hotkeys -------------------------------------------------------------

    [Test]
    public async Task Run_GoesThroughTheLauncher()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(Chain("запускаемый"));
            var launcher = A.Fake<IMacroLauncher>();
            using var vm = CreateEditor(store, launcher: launcher);

            vm.Run(vm.Macros.Single());

            A.CallTo(() => launcher.RunMacro("запускаемый")).MustHaveHappenedOnceExactly();
            await Assert.That(vm.ErrorMessage).IsNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task RunState_FollowsTheRunRegistry()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(Chain("бегущий"));
            using var runs = CreateRuns();
            using var vm = CreateEditor(store, runs);

            var handle = runs.TryBegin("бегущий");
            await Assert.That(vm.Macros.Single().IsRunning).IsTrue();

            vm.Stop(vm.Macros.Single());
            await Assert.That(handle!.Token.IsCancellationRequested).IsTrue();

            runs.Complete(handle.RunId);
            await Assert.That(vm.Macros.Single().IsRunning).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task HotkeySuspension_IsForwarded_AndOptional()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            var hotkeys = A.Fake<IHotkeySuspension>();
            using var withSuspension = CreateEditor(store, hotkeys: hotkeys);

            await withSuspension.SuspendHotkeysAsync();
            await withSuspension.ResumeHotkeysAsync();

            A.CallTo(() => hotkeys.SuspendAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
            A.CallTo(() => hotkeys.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();

            // No suspension wired (stage-3 slim UI, tests) must not blow up.
            using var without = CreateEditor(store);
            await without.SuspendHotkeysAsync();
            await without.ResumeHotkeysAsync();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task SelectIssue_HighlightsTheOffendingNode()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var vm = CreateEditor(store);
            vm.LoadGraph(new MacroGraph
            {
                Name = "подсветка",
                StartNodeId = "a",
                Nodes =
                [
                    new DelayNode { Id = "a", Ms = 1 },
                    new DelayNode { Id = "orphan", Ms = 1 },
                ],
            });

            await vm.SaveAsync();
            var issue = vm.Issues.Single(i => i.NodeId == "orphan");
            vm.SelectIssue(issue);

            await Assert.That(vm.SelectedNode?.NodeId).IsEqualTo("orphan");
            await Assert.That(vm.Nodes.Single(n => n.NodeId == "orphan").IsSelected).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Dispose_UnsubscribesFromBothRegistries()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            using var runs = CreateRuns();
            var vm = CreateEditor(store, runs);
            vm.Dispose();

            await store.SaveAsync(Chain("после-dispose"));
            runs.TryBegin("после-dispose");

            await Assert.That(vm.Macros).IsEmpty();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
