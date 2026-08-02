using FakeItEasy;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// Stage 3: the macro editor's behaviour, headless and over IPC.
//
// The daemon is stubbed by DaemonLibraryStub rather than mocked call-by-call, because the
// behaviours under test are conversational: a save is "request → daemon writes → daemon
// broadcasts MacrosChanged → client re-fetches", and the editor's dirty/external-change
// logic only makes sense against a peer that actually does all four. The stub therefore
// keeps a real library, validates with the real MacroGraphValidator, and pushes the same
// events in the same order the daemon does — including raising MacrosChanged from INSIDE
// the save, which is the ordering that makes "our own write echoing back" a real case.
public class MacroEditorViewModelTests
{
    /// <summary>A minimal daemon: a macro library, the run list, and the SaveMacro contract.</summary>
    private sealed class DaemonLibraryStub
    {
        public DaemonLibraryStub()
        {
            Client = new FakeIpcClient();
            Client.Respond(IpcMessageTypes.GetMacros, _ => Snapshot());
            Client.Respond(IpcMessageTypes.GetRunningMacros, _ => Runs.ToArray());
            Client.Respond(IpcMessageTypes.SaveMacro, payload => Save((SaveMacroRequest)payload!));
            Client.Respond(IpcMessageTypes.DeleteMacro, payload => Delete((DeleteMacroRequest)payload!));
        }

        public FakeIpcClient Client { get; }

        public List<MacroGraph> Macros { get; } = [];

        public List<RunningMacroDto> Runs { get; } = [];

        public MacroGraph? Find(string name) =>
            Macros.FirstOrDefault(macro => string.Equals(macro.Name, name, StringComparison.Ordinal));

        /// <summary>Writes a graph the way an external editor would, and pushes the change.</summary>
        public void WriteExternally(MacroGraph graph)
        {
            Macros.RemoveAll(macro => string.Equals(macro.Name, graph.Name, StringComparison.Ordinal));
            Macros.Add(graph);
            Client.RaiseEvent(IpcMessageTypes.MacrosChanged);
        }

        public void SetRuns(params RunningMacroDto[] runs)
        {
            Runs.Clear();
            Runs.AddRange(runs);
            Client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, Runs.ToArray());
        }

        private MacroGraph[] Snapshot() =>
            [.. Macros.OrderBy(macro => macro.Name, StringComparer.Ordinal)];

        // Mirrors IpcRequestDispatcher.SaveMacroAsync: validate, add the name check as a
        // graph-level issue, reject on any error, otherwise write and broadcast.
        private ValidationIssueDto[] Save(SaveMacroRequest request)
        {
            var issues = new List<ValidationIssue>(MacroGraphValidator.Validate(request.Macro));
            if (NameError(request.Macro.Name) is { } nameError)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, null, nameError));
            }

            if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
            {
                return [.. issues.Select(issue => issue.ToDto())];
            }

            Macros.RemoveAll(macro => string.Equals(macro.Name, request.Macro.Name, StringComparison.Ordinal));
            Macros.Add(request.Macro);
            // Raised before the reply is returned — exactly as the daemon does it, since the
            // event pump and the response path are different writers on the same connection.
            Client.RaiseEvent(IpcMessageTypes.MacrosChanged);
            // Empty = written. Warnings are deliberately NOT reported on success.
            return [];
        }

        private object? Delete(DeleteMacroRequest request)
        {
            if (Macros.RemoveAll(macro => string.Equals(macro.Name, request.Name, StringComparison.Ordinal)) > 0)
            {
                Client.RaiseEvent(IpcMessageTypes.MacrosChanged);
            }
            return null;
        }

        private static string? NameError(string name) =>
            string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                ? $"имя '{name}' не годится для файла"
                : null;
    }

    private static MacroEditorViewModel CreateEditor(
        DaemonLibraryStub daemon,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null) =>
        new(daemon.Client, launcher, hotkeys, ImmediateUiDispatcher.Instance, @"C:\smartmacro\macros");

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

    // ---- structure editing ------------------------------------------------------------

    [Test]
    public async Task DeleteNode_ClearsEveryInboundEdge()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        vm.DeleteNode(vm.Nodes.Single(n => n.NodeId == "b"));

        var graph = vm.BuildGraph();
        await Assert.That(graph.Nodes).Count().IsEqualTo(2);
        await Assert.That(((DelayNode)graph.Nodes[0]).Next).IsNull();
        await Assert.That(graph.StartNodeId).IsEqualTo("a");
    }

    [Test]
    public async Task DeleteStartNode_MovesTheStartToWhatIsLeft()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        vm.DeleteNode(vm.Nodes.Single(n => n.NodeId == "a"));

        await Assert.That(vm.StartNodeId).IsEqualTo("b");
        await Assert.That(vm.BuildGraph().StartNodeId).IsEqualTo("b");
    }

    [Test]
    public async Task DeletingEveryNode_LeavesAnEmptyStart_WithoutThrowing()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        while (vm.Nodes.Count > 0)
        {
            vm.DeleteNode(vm.Nodes[0]);
        }

        await Assert.That(vm.StartNodeId).IsEqualTo(string.Empty);
        await Assert.That(vm.BuildGraph().Nodes).IsEmpty();
    }

    [Test]
    public async Task RenamingANode_RepointsInboundEdgesAndTheStartNode()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        vm.Nodes.Single(n => n.NodeId == "a").NodeId = "начало";
        vm.Nodes.Single(n => n.NodeId == "b").NodeId = "середина";

        var graph = vm.BuildGraph();
        await Assert.That(graph.StartNodeId).IsEqualTo("начало");
        await Assert.That(((DelayNode)graph.Nodes[0]).Next).IsEqualTo("середина");
        await Assert.That(((DelayNode)graph.Nodes[1]).Next).IsEqualTo("c");
    }

    [Test]
    public async Task BlankNodeId_IsRejected()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        vm.Nodes[0].NodeId = "   ";

        await Assert.That(vm.Nodes[0].NodeId).IsEqualTo("a");
    }

    [Test]
    public async Task AddNode_GeneratesUniqueIds_AndSeedsTheStartOfAnEmptyGraph()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(new MacroGraph { Name = "пусто", StartNodeId = string.Empty, Nodes = [] });

        var first = vm.AddNode(MacroNodeKind.KeyPress);
        var second = vm.AddNode(MacroNodeKind.Click);

        await Assert.That(first.NodeId).IsEqualTo("n1");
        await Assert.That(second.NodeId).IsEqualTo("n2");
        await Assert.That(vm.StartNodeId).IsEqualTo("n1");
        // Both new ids plus the "end of run" entry must be offered to every edge.
        await Assert.That(vm.NodeIdChoices).IsEquivalentTo(new[] { string.Empty, "n1", "n2" });
    }

    // ---- save gating --------------------------------------------------------------------

    [Test]
    public async Task Save_SendsSaveMacro_AndTheGraphLandsInTheLibrary()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain("рабочий"));

        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsTrue();
        await Assert.That(daemon.Client.CountOf(IpcMessageTypes.SaveMacro)).IsEqualTo(1);
        await Assert.That(daemon.Find("рабочий")).IsNotNull();
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsFalse();
        await Assert.That(vm.IsDirty()).IsFalse();
    }

    [Test]
    public async Task Save_RejectedByTheDaemon_RendersTheIssues_AndTheEditorStaysDirty()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);

        // A hotkey macro has no context window, so a reachable conditional node is an
        // ERROR — the classic authoring mistake the validator exists to catch.
        vm.LoadGraph(new MacroGraph
        {
            Name = "битый",
            Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F13)],
            StartNodeId = "find",
            Nodes = [new FindElementNode { Id = "find", Template = "Btn" }],
        });
        // An edit, so "still dirty after a refusal" is an observable claim.
        vm.MacroName = "битый-2";

        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsFalse();
        await Assert.That(daemon.Macros).IsEmpty();
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsTrue();
        await Assert.That(vm.ErrorMessage).IsNotNull();
        await Assert.That(vm.IsDirty()).IsTrue();
    }

    [Test]
    public async Task Save_IsBlockedLocallyByARowLevelInputError_AndNothingIsSent()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain("кривая-пауза"));
        ((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText = "две секунды";

        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsFalse();
        // A half-typed number never reaches the wire: BuildGraph would silently substitute
        // something the user didn't ask for.
        await Assert.That(daemon.Client.CountOf(IpcMessageTypes.SaveMacro)).IsEqualTo(0);
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsTrue();
    }

    [Test]
    public async Task Save_IsBlockedByAnUnusableName()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain());
        vm.MacroName = "плохое/имя";

        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsFalse();
        await Assert.That(daemon.Macros).IsEmpty();
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsTrue();
    }

    [Test]
    public async Task Save_SucceedsWithWarnings_WhichAreReDerivedLocally()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        // "orphan" is unreachable from the start node — a warning, not an error, and one the
        // daemon does NOT return on a successful save.
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
        await Assert.That(vm.StatusMessage).Contains("предупреждени");
        await Assert.That(daemon.Find("с-предупреждением")).IsNotNull();
    }

    [Test]
    public async Task Rename_SavesTheNewNameAndDeletesTheOldOne()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain("старое-имя"));
        await vm.SaveAsync();

        vm.MacroName = "новое-имя";
        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsTrue();
        await Assert.That(daemon.Find("новое-имя")).IsNotNull();
        await Assert.That(daemon.Find("старое-имя")).IsNull();
        await Assert.That(daemon.Client.PayloadsOf<DeleteMacroRequest>(IpcMessageTypes.DeleteMacro).Single().Name)
            .IsEqualTo("старое-имя");
        await Assert.That(vm.Macros.Select(m => m.Name)).IsEquivalentTo(new[] { "новое-имя" });
    }

    [Test]
    public async Task Save_RoundTripsAGraphWithEveryNodeTypeAcrossTheWire()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        var original = NodeRowRoundTripTests.EveryNodeType();
        vm.LoadGraph(original);

        await Assert.That(await vm.SaveAsync()).IsTrue();

        // The fake client serialises the payload with the real IpcJson options, so this
        // covers the polymorphic $type discriminators surviving the request.
        var received = daemon.Find(original.Name);
        await Assert.That(received).IsNotNull();
        // Canvas coordinates are the ONE thing a round trip is allowed to add: D3a places
        // unplaced nodes on load, so every node comes back with an Editor. Everything else
        // has to be byte-identical.
        await Assert.That(MacroGraphJson.Serialize(WithoutLayout(received!)))
            .IsEqualTo(MacroGraphJson.Serialize(WithoutLayout(original)));
        await Assert.That(received!.Nodes.All(node => node.Editor is not null)).IsTrue();
        // …and a node that already had coordinates keeps exactly the ones it had.
        await Assert.That(received.Nodes.Single(node => node.Id == "key").Editor)
            .IsEqualTo(new NodeEditorInfo(12.5, -40));
    }

    private static MacroGraph WithoutLayout(MacroGraph graph) => new()
    {
        Name = graph.Name,
        Triggers = graph.Triggers,
        StartNodeId = graph.StartNodeId,
        Nodes = [.. graph.Nodes.Select(node => node with { Editor = null })],
    };

    // ---- library / hot reload -----------------------------------------------------------

    [Test]
    public async Task Construction_FetchesTheLibrary_AndSelectingAMacroLoadsIt()
    {
        var daemon = new DaemonLibraryStub();
        daemon.Macros.Add(Chain("первый"));
        daemon.Macros.Add(Chain("второй"));
        using var vm = CreateEditor(daemon);

        await Assert.That(daemon.Client.CountOf(IpcMessageTypes.GetMacros)).IsEqualTo(1);
        await Assert.That(vm.Macros).Count().IsEqualTo(2);
        await Assert.That(vm.HasOpenMacro).IsFalse();

        vm.SelectedMacro = vm.Macros.Single(m => m.Name == "второй");

        await Assert.That(vm.HasOpenMacro).IsTrue();
        await Assert.That(vm.MacroName).IsEqualTo("второй");
        await Assert.That(vm.Nodes).Count().IsEqualTo(3);
        await Assert.That(vm.IsDirty()).IsFalse();
    }

    [Test]
    public async Task Reconnect_RefetchesTheLibrary()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        await Assert.That(vm.Macros).IsEmpty();

        daemon.Macros.Add(Chain("появился-пока-нас-не-было"));
        daemon.Client.RaiseConnected();

        await Assert.That(daemon.Client.CountOf(IpcMessageTypes.GetMacros)).IsEqualTo(2);
        await Assert.That(vm.Macros.Select(m => m.Name)).IsEquivalentTo(new[] { "появился-пока-нас-не-было" });
    }

    [Test]
    public async Task MacrosChanged_ReloadsACleanEditor()
    {
        var daemon = new DaemonLibraryStub();
        daemon.Macros.Add(Chain("живой"));
        using var vm = CreateEditor(daemon);
        vm.SelectedMacro = vm.Macros.Single();

        // Someone edits the file behind our back; the daemon's watcher pushes MacrosChanged.
        daemon.WriteExternally(new MacroGraph
        {
            Name = "живой",
            StartNodeId = "only",
            Nodes = [new DelayNode { Id = "only", Ms = 5000 }],
        });

        await Assert.That(vm.ChangedOnDisk).IsFalse();
        await Assert.That(vm.Nodes).Count().IsEqualTo(1);
        await Assert.That(((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText).IsEqualTo("5");
    }

    [Test]
    public async Task MacrosChanged_DoesNotClobberUnsavedEdits()
    {
        var daemon = new DaemonLibraryStub();
        daemon.Macros.Add(Chain("живой"));
        using var vm = CreateEditor(daemon);
        vm.SelectedMacro = vm.Macros.Single();
        ((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText = "9";

        daemon.WriteExternally(new MacroGraph
        {
            Name = "живой",
            StartNodeId = "only",
            Nodes = [new DelayNode { Id = "only", Ms = 5000 }],
        });

        await Assert.That(vm.ChangedOnDisk).IsTrue();
        await Assert.That(vm.Nodes).Count().IsEqualTo(3);
        await Assert.That(((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText).IsEqualTo("9");
    }

    [Test]
    public async Task OurOwnSave_IsNotMistakenForAnExternalChange()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain("своё"));

        await vm.SaveAsync();

        await Assert.That(vm.ChangedOnDisk).IsFalse();
        await Assert.That(vm.IsDirty()).IsFalse();
        await Assert.That(vm.SelectedMacro?.Name).IsEqualTo("своё");
    }

    [Test]
    public async Task NewMacro_ProducesASaveableDraftThatIsNotYetInTheLibrary()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);

        vm.NewMacro();

        await Assert.That(vm.HasOpenMacro).IsTrue();
        await Assert.That(vm.Nodes).Count().IsEqualTo(1);
        await Assert.That(daemon.Macros).IsEmpty();

        await Assert.That(await vm.SaveAsync()).IsTrue();
        await Assert.That(daemon.Macros).Count().IsEqualTo(1);
    }

    [Test]
    public async Task DeleteMacro_SendsDeleteMacro_AndClosesTheEditor()
    {
        var daemon = new DaemonLibraryStub();
        daemon.Macros.Add(Chain("на-удаление"));
        using var vm = CreateEditor(daemon);
        vm.SelectedMacro = vm.Macros.Single();

        var deleted = await vm.DeleteMacroAsync(vm.Macros.Single());

        await Assert.That(deleted).IsTrue();
        await Assert.That(daemon.Client.PayloadsOf<DeleteMacroRequest>(IpcMessageTypes.DeleteMacro).Single().Name)
            .IsEqualTo("на-удаление");
        await Assert.That(vm.Macros).IsEmpty();
        await Assert.That(vm.HasOpenMacro).IsFalse();
    }

    [Test]
    public async Task DeleteMacro_ReportsAFailedRequest()
    {
        var daemon = new DaemonLibraryStub();
        daemon.Macros.Add(Chain("упрямый"));
        using var vm = CreateEditor(daemon);
        daemon.Client.Fail(IpcMessageTypes.DeleteMacro, "файл занят");

        var deleted = await vm.DeleteMacroAsync(vm.Macros.Single());

        await Assert.That(deleted).IsFalse();
        await Assert.That(vm.ErrorMessage).IsNotNull();
        await Assert.That(vm.Macros).Count().IsEqualTo(1);
    }

    [Test]
    public async Task FolderPath_IsTheHostSuppliedDaemonFolder()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        await Assert.That(vm.FolderPath).IsEqualTo(@"C:\smartmacro\macros");
    }

    // ---- run / stop / hotkeys -------------------------------------------------------------

    [Test]
    public async Task Run_GoesThroughTheLauncher()
    {
        var daemon = new DaemonLibraryStub();
        daemon.Macros.Add(Chain("запускаемый"));
        var launcher = A.Fake<IMacroLauncher>();
        using var vm = CreateEditor(daemon, launcher: launcher);

        vm.Run(vm.Macros.Single());

        A.CallTo(() => launcher.RunMacro("запускаемый")).MustHaveHappenedOnceExactly();
        await Assert.That(vm.ErrorMessage).IsNull();
    }

    [Test]
    public async Task RunState_FollowsTheDaemonsRunList_AndStopSendsStopMacro()
    {
        var daemon = new DaemonLibraryStub();
        daemon.Macros.Add(Chain("бегущий"));
        using var vm = CreateEditor(daemon);

        var runId = Guid.NewGuid();
        daemon.SetRuns(new RunningMacroDto(runId, "бегущий", DateTimeOffset.UtcNow, null));
        await Assert.That(vm.Macros.Single().IsRunning).IsTrue();

        await vm.StopAsync(vm.Macros.Single());
        await Assert.That(daemon.Client.PayloadsOf<StopMacroRequest>(IpcMessageTypes.StopMacro).Single().RunId)
            .IsEqualTo(runId);

        daemon.SetRuns();
        await Assert.That(vm.Macros.Single().IsRunning).IsFalse();
    }

    [Test]
    public async Task HotkeySuspension_IsForwarded_AndOptional()
    {
        var daemon = new DaemonLibraryStub();
        var hotkeys = A.Fake<IHotkeySuspension>();
        using var withSuspension = CreateEditor(daemon, hotkeys: hotkeys);

        await withSuspension.SuspendHotkeysAsync();
        await withSuspension.ResumeHotkeysAsync();

        A.CallTo(() => hotkeys.SuspendAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => hotkeys.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        // No suspension wired (tests, or a host that chose not to) must not blow up.
        using var without = CreateEditor(daemon);
        await without.SuspendHotkeysAsync();
        await without.ResumeHotkeysAsync();
    }

    [Test]
    public async Task SelectIssue_HighlightsTheOffendingNode()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);
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

    [Test]
    public async Task Dispose_UnsubscribesFromTheDaemon()
    {
        var daemon = new DaemonLibraryStub();
        var vm = CreateEditor(daemon);
        vm.Dispose();

        daemon.WriteExternally(Chain("после-dispose"));
        daemon.Client.RaiseConnected();

        await Assert.That(vm.Macros).IsEmpty();
    }
}
