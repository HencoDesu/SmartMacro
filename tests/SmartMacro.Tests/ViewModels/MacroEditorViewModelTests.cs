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

// Стадия 3: поведение редактора макросов, без окон и через IPC.
//
// Демон заменён заглушкой DaemonLibraryStub, а не расписан моком по вызовам, потому что
// проверяемые поведения разговорны по своей природе: сохранение — это «запрос → демон пишет →
// демон рассылает MacrosChanged → клиент перезапрашивает», и логика редактора вокруг
// несохранённых правок и внешних изменений имеет смысл только против собеседника, который
// действительно делает все четыре шага. Поэтому заглушка держит настоящую библиотеку, проверяет
// настоящим MacroGraphValidator и шлёт те же события в том же порядке, что и демон, — в том
// числе поднимает MacrosChanged ИЗНУТРИ сохранения, а именно этот порядок и делает «наша
// собственная запись вернулась эхом» настоящим случаем.
public class MacroEditorViewModelTests
{
    /// <summary>Минимальный демон: библиотека макросов, список прогонов и договорённость SaveMacro.</summary>
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

        /// <summary>Пишет граф так, как это сделал бы сторонний редактор, и присылает пуш об изменении.</summary>
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

        // Повторяет IpcRequestDispatcher.SaveMacroAsync: проверить, добавить проверку имени как
        // замечание уровня графа, отказать при любой ошибке, иначе записать и разослать.
        private ValidationIssueDto[] Save(SaveMacroRequest request)
        {
            var issues = new List<ValidationIssue>(MacroGraphValidator.Validate(request.Macro));
            if (NameError(request.Macro.Name) is { } nameError)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, null, null, nameError));
            }

            if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
            {
                return [.. issues.Select(issue => issue.ToDto())];
            }

            Macros.RemoveAll(macro => string.Equals(macro.Name, request.Macro.Name, StringComparison.Ordinal));
            Macros.Add(request.Macro);
            // Поднимается до того, как вернётся ответ, — ровно так же, как это делает демон:
            // насос событий и путь ответа суть разные писатели на одном соединении.
            Client.RaiseEvent(IpcMessageTypes.MacrosChanged);
            // Пусто — значит, записано. При успехе предупреждения намеренно НЕ сообщаются.
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

    /// <summary>a → b → c, все ноды Delay, триггеров нет (поэтому правило про контекст не действует).</summary>
    private static MacroGraph Chain(string name = "цепочка") => new()
    {
        Name = name,
        StartNodeId = Ids.Of("a"),
        Nodes =
        [
            new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 100, Next = Ids.Of("b") },
            new DelayNode { Id = Ids.Of("b"), DisplayName = "b", Ms = 200, Next = Ids.Of("c") },
            new DelayNode { Id = Ids.Of("c"), DisplayName = "c", Ms = 300 },
        ],
    };

    // ---- правка структуры ---------------------------------------------------------------

    [Test]
    public async Task DeleteNode_ClearsEveryInboundEdge()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        vm.DeleteNode(vm.Nodes.Single(n => n.DisplayName == "b"));

        var graph = vm.BuildGraph();
        await Assert.That(graph.Nodes).Count().IsEqualTo(2);
        await Assert.That(((DelayNode)graph.Nodes[0]).Next).IsNull();
        await Assert.That(graph.StartNodeId).IsEqualTo(Ids.Of("a"));
    }

    [Test]
    public async Task DeleteStartNode_MovesTheStartToWhatIsLeft()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        vm.DeleteNode(vm.Nodes.Single(n => n.DisplayName == "a"));

        await Assert.That(vm.StartNodeId).IsEqualTo(Ids.Of("b"));
        await Assert.That(vm.BuildGraph().StartNodeId).IsEqualTo(Ids.Of("b"));
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

        await Assert.That(vm.StartNodeId).IsEqualTo(Guid.Empty);
        await Assert.That(vm.BuildGraph().Nodes).IsEmpty();
    }

    [Test]
    public async Task RenamingANode_ChangesTheLabelAndNothingElse()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        vm.Nodes.Single(n => n.DisplayName == "a").DisplayName = "начало";
        vm.Nodes.Single(n => n.DisplayName == "b").DisplayName = "середина";

        // Прежде это была операция НАД ГРАФОМ: редактор ловил старое значение и перенацеливал
        // каждое входящее ребро и стартовую ноду. Теперь связь идёт по Id, и переименование их не
        // касается — граф до и после совпадает всюду, кроме подписей.
        var graph = vm.BuildGraph();
        await Assert.That(graph.StartNodeId).IsEqualTo(Ids.Of("a"));
        await Assert.That(((DelayNode)graph.Nodes[0]).Next).IsEqualTo(Ids.Of("b"));
        await Assert.That(((DelayNode)graph.Nodes[1]).Next).IsEqualTo(Ids.Of("c"));
        await Assert.That(graph.Nodes.Select(node => node.DisplayName))
            .IsEquivalentTo(new[] { "начало", "середина", "c" });
    }

    [Test]
    public async Task BlankDisplayName_IsRejected()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(Chain());

        // Безымянная нода читалась бы в полосе лога как пропущенная строка.
        vm.Nodes[0].DisplayName = "   ";

        await Assert.That(vm.Nodes[0].DisplayName).IsEqualTo("a");
    }

    [Test]
    public async Task AddNode_NamesNodesAfterTheirType_AndSeedsTheStartOfAnEmptyGraph()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());
        vm.LoadGraph(new MacroGraph { Name = "пусто", StartNodeId = Guid.Empty, Nodes = [] });

        var first = vm.AddNode(MacroNodeKind.KeyPress);
        var second = vm.AddNode(MacroNodeKind.Click);

        // Имя от ТИПА, а не «n1»/«n2»: полоса лога прогона обязана читаться сразу, без того чтобы
        // автор сперва переименовал каждую ноду руками. Номер сквозной по графу, поэтому он ещё и
        // говорит, в каком порядке ноды заводили.
        await Assert.That(first.DisplayName).IsEqualTo("key-1");
        await Assert.That(second.DisplayName).IsEqualTo("click-2");
        await Assert.That(vm.StartNodeId).IsEqualTo(first.Id);
        // Каждому ребру обязаны предлагаться обе новые ноды плюс запись «конец прогона».
        await Assert.That(vm.NodeChoices.Select(choice => choice.Display))
            .IsEquivalentTo(new[] { "— конец —", "key-1", "click-2" });
    }

    // ---- что не пускает сохранить ------------------------------------------------------------

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

        // У макроса на хоткее нет контекстного окна, поэтому достижимая условная нода — это
        // ОШИБКА, классический авторский промах, ради ловли которого проверяльщик и существует.
        vm.LoadGraph(new MacroGraph
        {
            Name = "битый",
            Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F13)],
            StartNodeId = Ids.Of("find"),
            Nodes = [new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "Btn" }],
        });
        // Правка — чтобы утверждение «после отказа правки всё ещё не сохранены» можно было
        // наблюдать.
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
        // Недопечатанное число до провода не доходит: BuildGraph втихую подставил бы что-нибудь,
        // о чём пользователь не просил.
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
        // "orphan" недостижим от стартовой ноды — это предупреждение, а не ошибка, и притом
        // такое, которое демон при успешном сохранении НЕ возвращает.
        vm.LoadGraph(new MacroGraph
        {
            Name = "с-предупреждением",
            StartNodeId = Ids.Of("a"),
            Nodes =
            [
                new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 100 },
                new DelayNode { Id = Ids.Of("orphan"), DisplayName = "orphan", Ms = 100 },
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

        // Поддельный клиент сериализует нагрузку настоящими настройками IpcJson, так что заодно
        // покрыто и выживание полиморфных дискриминаторов $type в запросе.
        var received = daemon.Find(original.Name);
        await Assert.That(received).IsNotNull();
        // Координаты на канве — ЕДИНСТВЕННОЕ, что round trip вправе добавить: D3a расставляет при
        // загрузке нерасставленные ноды, поэтому каждая нода возвращается с Editor. Всё остальное
        // обязано совпасть байт в байт.
        await Assert.That(MacroGraphJson.Serialize(WithoutLayout(received!)))
            .IsEqualTo(MacroGraphJson.Serialize(WithoutLayout(original)));
        await Assert.That(received!.Nodes.All(node => node.Editor is not null)).IsTrue();
        // …а нода, у которой координаты уже были, сохраняет ровно те, что были.
        await Assert.That(received.Nodes.Single(node => node.Id == Ids.Of("key")).Editor)
            .IsEqualTo(new NodeEditorInfo(12.5, -40));
    }

    private static MacroGraph WithoutLayout(MacroGraph graph) => new()
    {
        Name = graph.Name,
        Triggers = graph.Triggers,
        StartNodeId = graph.StartNodeId,
        Nodes = [.. graph.Nodes.Select(node => node with { Editor = null })],
    };

    // ---- библиотека и перезагрузка на лету ------------------------------------------------

    // Свежая установка: демон примеров больше не сеет, поэтому «библиотека пуста» — это ПЕРВОЕ,
    // что видит пользователь, и подсказка «выберите макрос слева» на пустом списке читалась бы
    // как поломка. Пустой канве нужны РАЗНЫЕ слова в зависимости от того, есть ли что выбирать.
    [Test]
    public async Task EmptyLibrary_GetsItsOwnHint_NotThePickOneHint()
    {
        var daemon = new DaemonLibraryStub();
        using var vm = CreateEditor(daemon);

        await Assert.That(vm.IsLibraryEmpty).IsTrue();
        await Assert.That(vm.ShowEmptyLibraryHint).IsTrue();
        await Assert.That(vm.ShowPickMacroHint).IsFalse();

        // Как только в библиотеке что-то появилось, возвращается обычная подсказка.
        daemon.WriteExternally(Chain("первый"));

        await Assert.That(vm.IsLibraryEmpty).IsFalse();
        await Assert.That(vm.ShowEmptyLibraryHint).IsFalse();
        await Assert.That(vm.ShowPickMacroHint).IsTrue();
    }

    // Черновик при пустой библиотеке — законное состояние, и подсказка обязана уйти с дороги
    // его графа: иначе «библиотека пуста» легло бы поверх нод, которые пользователь только что
    // расставил.
    [Test]
    public async Task DraftOnAnEmptyLibrary_ShowsNeitherHint()
    {
        using var vm = CreateEditor(new DaemonLibraryStub());

        vm.NewMacro();

        await Assert.That(vm.IsLibraryEmpty).IsTrue();
        await Assert.That(vm.ShowEmptyLibraryHint).IsFalse();
        await Assert.That(vm.ShowPickMacroHint).IsFalse();
    }

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

        // Кто-то правит файл у нас за спиной; наблюдатель демона присылает пуш MacrosChanged.
        daemon.WriteExternally(new MacroGraph
        {
            Name = "живой",
            StartNodeId = Ids.Of("only"),
            Nodes = [new DelayNode { Id = Ids.Of("only"), DisplayName = "only", Ms = 5000 }],
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
            StartNodeId = Ids.Of("only"),
            Nodes = [new DelayNode { Id = Ids.Of("only"), DisplayName = "only", Ms = 5000 }],
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

    // ---- запуск, остановка, хоткеи -------------------------------------------------------------

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

        // Приостановка не подключена (тесты — или хост, который решил обойтись без неё): падать
        // от этого нельзя.
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
            StartNodeId = Ids.Of("a"),
            Nodes =
            [
                new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 1 },
                new DelayNode { Id = Ids.Of("orphan"), DisplayName = "orphan", Ms = 1 },
            ],
        });

        await vm.SaveAsync();
        var issue = vm.Issues.Single(i => i.NodeId == Ids.Of("orphan"));
        vm.SelectIssue(issue);

        // Замечание адресуется по id, а печатается по подписи — редактор пользуется первым.
        await Assert.That(issue.Display).Contains("[orphan]");
        await Assert.That(vm.SelectedNode?.DisplayName).IsEqualTo("orphan");
        await Assert.That(vm.Nodes.Single(n => n.DisplayName == "orphan").IsSelected).IsTrue();
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
