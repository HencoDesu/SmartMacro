using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D3a: логика редактора-канвы, без окон.
//
// Отрисовку здесь проверить нельзя, да и не в ней риск. Риск во всём том, что канва ВЫЧИСЛЯЕТ:
// куда встанет коробка, как проложится ребро, что считается конечным состоянием, в какой раздел
// библиотеки попадёт макрос, — потому что всё это в диффе невидимо и всё это пользователь
// заметит первым.
public class MacroCanvasTests
{
    // Библиотека ОБЩАЯ и пустая: эти тесты про вычисления канвы, а не про файлы, и
    // в папку ни один из них не пишет. Графы подаются через LoadGraph.
    private static MacroEditorViewModel CreateEditor(FakeIpcClient? client = null) =>
        new(client ?? new FakeIpcClient(), TempLibrary.Shared, null, null, ImmediateUiDispatcher.Instance);

    /// <summary>То же самое, но отдаёт и поддельный демон, чтобы тест мог слать в него события прогона.</summary>
    private static MacroEditorViewModel CreateEditor(out FakeIpcClient daemon)
    {
        daemon = new FakeIpcClient();
        return CreateEditor(daemon);
    }

    /// <summary>Форма pw-boot: цепочка с одной веткой и без единой координаты.</summary>
    private static MacroGraph BootLike() => new()
    {
        Name = "pw-boot",
        StartNodeId = Ids.Of("wait-server"),
        Nodes =
        [
            new WaitForElementNode
            {
                Id = Ids.Of("wait-server"), DisplayName = "wait-server", Template = "A", TimeoutMs = 1000,
                Found = Ids.Of("click-server")
            },
            new ClickNode
            {
                Id = Ids.Of("click-server"), DisplayName = "click-server", Point = new ScreenPoint(1, 2),
                Next = Ids.Of("wait-char")
            },
            new WaitForElementNode
            {
                Id = Ids.Of("wait-char"), DisplayName = "wait-char", Template = "B", TimeoutMs = 1000,
                Found = Ids.Of("click-char")
            },
            new ClickNode
            {
                Id = Ids.Of("click-char"), DisplayName = "click-char", Point = new ScreenPoint(3, 4),
                Next = Ids.Of("open-stats")
            },
            new KeyPressNode
            {
                Id = Ids.Of("open-stats"), DisplayName = "open-stats", Key = VirtualKey.C, Next = Ids.Of("await-stats")
            },
            new DelayNode
                { Id = Ids.Of("await-stats"), DisplayName = "await-stats", Ms = 500, Next = Ids.Of("recognize") },
            new RecognizeTagNode
            {
                Id = Ids.Of("recognize"), DisplayName = "recognize",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 160, 35),
                Matched = Ids.Of("set-icon"),
                NotMatched = Ids.Of("close-stats"),
            },
            new SetIconNode
                { Id = Ids.Of("set-icon"), DisplayName = "set-icon", IconPath = "x.png", Next = Ids.Of("close-stats") },
            new KeyPressNode { Id = Ids.Of("close-stats"), DisplayName = "close-stats", Key = VirtualKey.C },
        ],
    };

    private static List<NodeRowViewModel> Rows(MacroGraph graph) =>
        [.. graph.Nodes.Select(NodeRowViewModel.FromNode)];

    // ---- раскладка ---------------------------------------------------------------------------

    [Test]
    public async Task AutoLayout_WalksTheGraphDepthFirst_AndWrapsEveryThreeBoxes()
    {
        var rows = Rows(BootLike());

        MacroGraphLayout.Apply(rows, Ids.Of("wait-server"));

        // В глубину от старта, следуя исходам каждой ноды по порядку. Именно это и удерживает
        // цепочку в порядке чтения: pw-boot в макете разложен ровно так.
        string[] expected =
        [
            "wait-server", "click-server", "wait-char",
            "click-char", "open-stats", "await-stats",
            "recognize", "set-icon", "close-stats",
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            var row = rows.Single(node => node.DisplayName == expected[i]);
            await Assert.That(row.X).IsEqualTo((i % 3) * CanvasMetrics.ColumnPitch);
            await Assert.That(row.Y).IsEqualTo((i / 3) * CanvasMetrics.RowPitch);
        }
    }

    [Test]
    public async Task AutoLayout_PutsUnreachableNodesAfterEverythingItCouldReach()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "с-сиротой",
            StartNodeId = Ids.Of("a"),
            Nodes =
            [
                new DelayNode { Id = Ids.Of("orphan"), DisplayName = "orphan", Ms = 1 },
                new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 1, Next = Ids.Of("b") },
                new DelayNode { Id = Ids.Of("b"), DisplayName = "b", Ms = 1 },
            ],
        });

        MacroGraphLayout.Apply(rows, Ids.Of("a"));

        await Assert.That(rows.Single(r => r.DisplayName == "a").X).IsEqualTo(0d);
        await Assert.That(rows.Single(r => r.DisplayName == "b").X).IsEqualTo(CanvasMetrics.ColumnPitch);
        await Assert.That(rows.Single(r => r.DisplayName == "orphan").X).IsEqualTo(CanvasMetrics.ColumnPitch * 2);
    }

    [Test]
    public async Task EnsurePositions_LaysOutAGraphThatHasNoneAtAll()
    {
        var rows = Rows(BootLike());

        var moved = MacroGraphLayout.EnsurePositions(rows, Ids.Of("wait-server"));

        await Assert.That(moved).IsTrue();
        await Assert.That(rows.All(row => row.HasPosition)).IsTrue();
        // Не куча в начале координат: там имеет право стоять ровно одна коробка.
        await Assert.That(rows.Count(row => row is { X: 0, Y: 0 })).IsEqualTo(1);
    }

    [Test]
    public async Task EnsurePositions_LeavesAFullyPlacedGraphAlone()
    {
        var rows = Rows(BootLike());
        MacroGraphLayout.Apply(rows, Ids.Of("wait-server"));
        var before = rows.Select(row => (row.X, row.Y)).ToList();

        var moved = MacroGraphLayout.EnsurePositions(rows, Ids.Of("wait-server"));

        await Assert.That(moved).IsFalse();
        await Assert.That(rows.Select(row => (row.X, row.Y))).IsEquivalentTo(before);
    }

    [Test]
    public async Task EnsurePositions_KeepsHandPlacedBoxes_AndDropsTheStraysUnderneath()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "полу-разложенный",
            StartNodeId = Ids.Of("placed"),
            Nodes =
            [
                new DelayNode
                {
                    Id = Ids.Of("placed"), DisplayName = "placed", Ms = 1, Next = Ids.Of("stray"),
                    Editor = new NodeEditorInfo(600, 300)
                },
                new DelayNode { Id = Ids.Of("stray"), DisplayName = "stray", Ms = 1 },
            ],
        });

        MacroGraphLayout.EnsurePositions(rows, Ids.Of("placed"));

        var placed = rows.Single(row => row.DisplayName == "placed");
        var stray = rows.Single(row => row.DisplayName == "stray");
        await Assert.That(placed.X).IsEqualTo(600d);
        await Assert.That(placed.Y).IsEqualTo(300d);
        // Ниже самой нижней из существующих коробок, чтобы не приземлиться поверх какой-нибудь.
        await Assert.That(stray.Y).IsGreaterThan(placed.Y + placed.LayoutHeight);
    }

    [Test]
    public async Task NextFreeSlot_FillsTheFirstHoleInTheGrid()
    {
        var rows = Rows(BootLike());
        MacroGraphLayout.Apply(rows, Ids.Of("wait-server"));
        // Девять нод — ряды 0..2 заполнены; следующей коробке место в ряду 3, столбце 0.
        var (x, y) = MacroGraphLayout.NextFreeSlot(rows);

        await Assert.That(x).IsEqualTo(0d);
        await Assert.That(y).IsEqualTo(CanvasMetrics.RowPitch * 3);
    }

    // ---- прокладка рёбер ---------------------------------------------------------------------

    [Test]
    public async Task Router_DrawsNothingForAnUnwiredOutcome()
    {
        // Особый упор самого макета: «в конец» не имеет права породить ни ноду, ни болтающуюся
        // линию.
        var rows = Rows(new MacroGraph
        {
            Name = "конец",
            StartNodeId = Ids.Of("wait"),
            Nodes =
            [
                new WaitForElementNode
                {
                    Id = Ids.Of("wait"), DisplayName = "wait", Template = "A", TimeoutMs = 1, Found = null,
                    Timeout = null
                }
            ],
        });

        await Assert.That(CanvasEdgeRouter.BuildAll(rows)).IsEmpty();
        await Assert.That(rows[0].Edges.All(edge => edge.IsEnd)).IsTrue();
        // Свёрнутая коробка говорит про конец прямо в строке порта, а не рисует ребро в
        // терминальную ноду; и подставлен в подпись ИМЕННО исход «таймаут», а не соседний.
        await Assert.That(Msg.Arg(rows[0].Edges[1].BoxLabel, Strings.Node_Edge_BoxLabelEnd))
            .IsEqualTo(Strings.Node_Edge_Timeout.ToLowerInvariant());
    }

    [Test]
    public async Task Router_DrawsNothingForAnOutcomeNamingAMissingNode()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "битая-ссылка",
            StartNodeId = Ids.Of("a"),
            Nodes = [new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 1, Next = Ids.Of("которого-нет") }],
        });

        await Assert.That(CanvasEdgeRouter.BuildAll(rows)).IsEmpty();
    }

    [Test]
    public async Task Router_UsesTheSideEntryForTheNextBoxOnTheSameRow()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "пара",
            StartNodeId = Ids.Of("a"),
            Nodes =
            [
                new DelayNode
                {
                    Id = Ids.Of("a"), DisplayName = "a", Ms = 1, Next = Ids.Of("b"), Editor = new NodeEditorInfo(0, 0)
                },
                new DelayNode
                {
                    Id = Ids.Of("b"), DisplayName = "b", Ms = 1,
                    Editor = new NodeEditorInfo(CanvasMetrics.ColumnPitch, 0)
                },
            ],
        });

        var edge = CanvasEdgeRouter.BuildAll(rows).Single();

        await Assert.That(edge.IsDirect).IsTrue();
        await Assert.That(edge.Waypoints).Count().IsEqualTo(2);
        await Assert.That(edge.Waypoints[0].X).IsEqualTo(CanvasMetrics.NodeWidth);
        await Assert.That(edge.Waypoints[^1].X).IsEqualTo(CanvasMetrics.ColumnPitch);
        await Assert.That(edge.Waypoints[^1].Y).IsEqualTo(CanvasMetrics.SideEntryInset);
    }

    [Test]
    public async Task Router_SendsAWrappingEdgeThroughTheGutterAboveTheTargetRow()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "перенос",
            StartNodeId = Ids.Of("a"),
            Nodes =
            [
                new DelayNode
                {
                    Id = Ids.Of("a"), DisplayName = "a", Ms = 1, Next = Ids.Of("b"),
                    Editor = new NodeEditorInfo(CanvasMetrics.ColumnPitch * 2, 0)
                },
                new DelayNode
                {
                    Id = Ids.Of("b"), DisplayName = "b", Ms = 1, Editor = new NodeEditorInfo(0, CanvasMetrics.RowPitch)
                },
            ],
        });

        var edge = CanvasEdgeRouter.BuildAll(rows).Single();

        await Assert.That(edge.IsDirect).IsFalse();
        await Assert.That(edge.Waypoints).Count().IsEqualTo(5);
        // Горизонтальный участок идёт НАД целевым рядом, а не сквозь коробки того или другого.
        var gutterY = CanvasMetrics.RowPitch - CanvasMetrics.GutterOffset;
        await Assert.That(edge.Waypoints[2].Y).IsEqualTo(gutterY);
        await Assert.That(edge.Waypoints[3].Y).IsEqualTo(gutterY);
        // …и падает в ВЕРХНИЙ край цели.
        await Assert.That(edge.Waypoints[^1].Y).IsEqualTo(CanvasMetrics.RowPitch);
        await Assert.That(edge.Waypoints[^1].X).IsEqualTo(CanvasMetrics.NodeWidth / 2);
    }

    [Test]
    public async Task Router_SpreadsSeveralEdgesArrivingAtTheSameBox()
    {
        var rows = Rows(new MacroGraph
        {
            Name = "схождение",
            StartNodeId = Ids.Of("a"),
            Nodes =
            [
                new DelayNode
                {
                    Id = Ids.Of("a"), DisplayName = "a", Ms = 1, Next = Ids.Of("target"),
                    Editor = new NodeEditorInfo(0, 0)
                },
                new DelayNode
                {
                    Id = Ids.Of("b"), DisplayName = "b", Ms = 1, Next = Ids.Of("target"),
                    Editor = new NodeEditorInfo(CanvasMetrics.ColumnPitch, 0)
                },
                new DelayNode
                {
                    Id = Ids.Of("target"), DisplayName = "target", Ms = 1,
                    Editor = new NodeEditorInfo(0, CanvasMetrics.RowPitch)
                },
            ],
        });

        var edges = CanvasEdgeRouter.BuildAll(rows);

        await Assert.That(edges).Count().IsEqualTo(2);
        await Assert.That(edges[0].Waypoints[^1].X).IsNotEqualTo(edges[1].Waypoints[^1].X);
    }

    [Test]
    public async Task Router_PutsTheTwoOutcomesOfAConditionalOnDifferentPorts()
    {
        var row = NodeRowViewModel.FromNode(new WaitForElementNode
        {
            Id = Ids.Of("wait"), DisplayName = "wait",
            Template = "A",
            TimeoutMs = 1,
            Editor = new NodeEditorInfo(0, 0),
        });

        var found = CanvasEdgeRouter.OutcomePort(row, 0);
        var timeout = CanvasEdgeRouter.OutcomePort(row, 1);

        await Assert.That(found.X).IsEqualTo(CanvasMetrics.NodeWidth);
        await Assert.That(timeout.Y - found.Y).IsEqualTo(CanvasMetrics.OutcomeRowHeight);
        // Обе внутри коробки, выше её нижнего края.
        await Assert.That(timeout.Y).IsLessThan(row.LayoutHeight);
    }

    // ---- дерево библиотеки (F4) ------------------------------------------------------------------
    //
    // Группировка по ПРЕФИКСУ ИМЕНИ («pw · 3», «прочее · 2») убрана вместе с тремя своими тестами:
    // она выводила структуру из того, как автор назвал файлы, потому что другой структуры не
    // было. С под-макросами структура настоящая — под-макрос лежит внутри файла своего макроса, —
    // и держать рядом вторую, придуманную, значило бы рисовать дерево с одним ненастоящим уровнем.

    [Test]
    public async Task Library_IsATreeOfMacrosWithTheirSubmacros()
    {
        // Здесь библиотека важна по содержанию, так что папка своя.
        using var library = new TempLibrary();
        library.WriteExternally(Graph("pw-boot"), [Submacro("опознать"), Submacro("войти")]);
        library.WriteExternally(Graph("Сбор наград"));
        using var vm = new MacroEditorViewModel(
            new FakeIpcClient(), library.Library, null, null, ImmediateUiDispatcher.Instance);

        // Заголовок группы — САМ макрос, а не выдуманный префикс.
        await Assert.That(vm.MacroGroups.Select(g => g.Name))
            .IsEquivalentTo(new[] { "pw-boot", "Сбор наград" });

        var boot = vm.MacroGroups.Single(g => g.Name == "pw-boot");
        await Assert.That(boot.Items.Select(i => i.Name)).IsEquivalentTo(new[] { "войти", "опознать" });
        // Макрос без функций — просто строка: группа с нулём элементов ничего не рисует сверх неё.
        await Assert.That(vm.MacroGroups.Single(g => g.Name == "Сбор наград").HasItems).IsFalse();
    }

    /// <summary>
    /// Поиск смотрит и на подпись функции: набранное «опознать» обязано находить макрос, внутри
    /// которого такая функция есть, — иначе имя, которое пользователь видит в дереве, ищется хуже,
    /// чем имя файла.
    /// </summary>
    [Test]
    public async Task LibrarySearch_FindsAMacroByTheNameOfItsSubmacro()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Graph("pw-boot"), [Submacro("опознать")]);
        library.WriteExternally(Graph("Сбор наград"));
        using var vm = new MacroEditorViewModel(
            new FakeIpcClient(), library.Library, null, null, ImmediateUiDispatcher.Instance);

        vm.LibrarySearch = "опозна";

        await Assert.That(vm.MacroGroups.Select(g => g.Name)).IsEquivalentTo(new[] { "pw-boot" });
        await Assert.That(vm.MacroGroups[0].Items.Single().Name).IsEqualTo("опознать");

        // А совпадение по имени МАКРОСА показывает его целиком, со всеми функциями.
        vm.LibrarySearch = "boot";
        await Assert.That(vm.MacroGroups[0].Items).Count().IsEqualTo(1);
    }

    // ---- редактор поверх канвы ---------------------------------------------------------------------

    [Test]
    public async Task LoadGraph_PlacesEveryBox_WithoutMakingTheEditorDirty()
    {
        using var vm = CreateEditor();

        vm.LoadGraph(BootLike());

        await Assert.That(vm.Nodes.All(node => node.HasPosition)).IsTrue();
        // Раскладка запекается в исходный слепок при загрузке: открыть старый макрос — это не
        // правка.
        await Assert.That(vm.IsDirty()).IsFalse();
    }

    [Test]
    public async Task MovingABox_IsAnEdit_AndRe_routesItsEdges()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var before = vm.CanvasEdges.Single(edge => edge.Source.DisplayName == "click-server").Waypoints[0];

        vm.MoveNode(vm.Nodes.Single(node => node.DisplayName == "click-server"), 1000, 900);

        await Assert.That(vm.IsDirty()).IsTrue();
        var after = vm.CanvasEdges.Single(edge => edge.Source.DisplayName == "click-server").Waypoints[0];
        await Assert.That(after.X).IsNotEqualTo(before.X);
        await Assert.That(vm.BuildGraph().Nodes.Single(node => node.Id == Ids.Of("click-server")).Editor)
            .IsEqualTo(new NodeEditorInfo(1000, 900));
    }

    [Test]
    public async Task RewiringAnOutcome_MovesTheLine()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var delay = vm.Nodes.Single(node => node.DisplayName == "await-stats");

        vm.RewireEdge(delay.Edges[0], Ids.Of("close-stats"));

        await Assert.That(vm.CanvasEdges.Single(edge => edge.Source.DisplayName == "await-stats").Target.DisplayName)
            .IsEqualTo("close-stats");
        await Assert.That(((DelayNode)vm.BuildGraph().Nodes.Single(n => n.Id == Ids.Of("await-stats"))).Next)
            .IsEqualTo(Ids.Of("close-stats"));
    }

    [Test]
    public async Task RewiringToNothing_EndsTheRun_AndRemovesTheLine()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var delay = vm.Nodes.Single(node => node.DisplayName == "await-stats");

        vm.RewireEdge(delay.Edges[0], null);

        await Assert.That(delay.Edges[0].IsEnd).IsTrue();
        await Assert.That(vm.CanvasEdges.Any(edge => edge.Source.DisplayName == "await-stats")).IsFalse();
        await Assert.That(((DelayNode)vm.BuildGraph().Nodes.Single(n => n.Id == Ids.Of("await-stats"))).Next).IsNull();
    }

    [Test]
    public async Task RewiringANodeToItself_IsRefused()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var delay = vm.Nodes.Single(node => node.DisplayName == "await-stats");

        vm.RewireEdge(delay.Edges[0], delay.Id);

        await Assert.That(delay.Edges[0].TargetId).IsEqualTo(Ids.Of("recognize"));
    }

    [Test]
    public async Task RenamingANode_DoesNotTouchTheGraphAtAll()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var before = vm.CanvasEdges.Count;
        var node = vm.Nodes.Single(n => n.DisplayName == "await-stats");
        var incoming = vm.CanvasEdges.Where(edge => edge.Target.Id == node.Id).Select(edge => edge.Source.Id).ToList();

        node.DisplayName = "пауза";

        // Раньше это была ОПЕРАЦИЯ НАД ГРАФОМ: редактор ловил старое значение и перенацеливал
        // каждое входящее ребро, стартовую ноду и набор точек останова. Теперь рёбра ссылаются
        // по Id, и переименование не двигает ровным счётом ничего — меняется только подпись.
        await Assert.That(vm.CanvasEdges).Count().IsEqualTo(before);
        await Assert.That(vm.CanvasEdges.Where(edge => edge.Target.Id == node.Id).Select(edge => edge.Source.Id))
            .IsEquivalentTo(incoming);
        await Assert.That(vm.CanvasEdges.Any(edge => edge.Target.DisplayName == "пауза")).IsTrue();
        // Элемент выпадающего списка тот же объект, только с новой подписью, — иначе ComboBox
        // потерял бы выделение, а закрытое поле показывало бы старое имя.
        await Assert.That(vm.NodeChoices.Any(choice => choice.Display == "пауза")).IsTrue();
    }

    [Test]
    public async Task DeletingANode_DropsItsLines_AndTheOnesPointingAtIt()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        vm.DeleteNode(vm.Nodes.Single(node => node.DisplayName == "await-stats"));

        await Assert.That(vm.CanvasEdges.Any(edge =>
            edge.Source.DisplayName == "await-stats" || edge.Target.DisplayName == "await-stats")).IsFalse();
    }

    [Test]
    public async Task AddNode_LandsOnAFreeSlot_NotOnTopOfAnything()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        var added = vm.AddNode(MacroNodeKind.Delay);

        await Assert.That(added.HasPosition).IsTrue();
        var occupied = vm.Nodes
            .Where(node => !ReferenceEquals(node, added))
            .Any(node => Math.Abs(node.X - added.X) < 1 && Math.Abs(node.Y - added.Y) < 1);
        await Assert.That(occupied).IsFalse();
    }

    [Test]
    public async Task AutoLayout_TidiesAHandMovedGraph()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        vm.MoveNode(vm.Nodes[0], 1500, 1500);

        vm.AutoLayout();

        await Assert.That(vm.Nodes.Single(node => node.DisplayName == "wait-server").X).IsEqualTo(0d);
        await Assert.That(vm.Nodes.Single(node => node.DisplayName == "wait-server").Y).IsEqualTo(0d);
    }

    [Test]
    public async Task Zoom_IsClamped()
    {
        using var vm = CreateEditor();

        vm.Zoom = 99;
        await Assert.That(vm.Zoom).IsEqualTo(MacroEditorViewModel.MaxZoom);

        vm.Zoom = 0;
        await Assert.That(vm.Zoom).IsEqualTo(MacroEditorViewModel.MinZoom);

        vm.Zoom = 1;
        await Assert.That(vm.ZoomText).IsEqualTo("100%");
    }

    [Test]
    public async Task OpeningAMacro_ResetsTheView()
    {
        using var vm = CreateEditor();
        vm.Zoom = 1.8;
        vm.PanX = -400;

        vm.LoadGraph(BootLike());

        await Assert.That(vm.Zoom).IsEqualTo(1d);
        await Assert.That(vm.PanX).IsGreaterThanOrEqualTo(0d);
    }

    [Test]
    public async Task ExpandingABox_ClosesTheOtherOne_AndSelectsIt()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());
        var first = vm.Nodes[0];
        var second = vm.Nodes[1];

        vm.ExpandNode(first);
        vm.ExpandNode(second);

        await Assert.That(first.IsExpanded).IsFalse();
        await Assert.That(second.IsExpanded).IsTrue();
        await Assert.That(vm.SelectedNode).IsEqualTo(second);

        vm.CollapseNodes();
        await Assert.That(vm.Nodes.Any(node => node.IsExpanded)).IsFalse();
    }

    [Test]
    public async Task SelectingANode_SwitchesTheInspector()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        await Assert.That(vm.HasSelectedNode).IsFalse();
        await Assert.That(vm.InspectorTitle).IsEqualTo(Strings.Editor_Inspector_MacroTitle);

        vm.SelectedNode = vm.Nodes.Single(node => node.DisplayName == "await-stats");

        await Assert.That(vm.HasSelectedNode).IsTrue();
        // Заголовок переключился на ТИП выбранной ноды, а не остался общим «Макрос».
        await Assert.That(vm.InspectorTitle).IsEqualTo(Strings.Node_Type_Delay);
        await Assert.That(vm.InspectorTitle).IsNotEqualTo(Strings.Editor_Inspector_MacroTitle);
    }

    // ---- та поверхность, которую предстоит наполнить волне D3b ------------------------------------

    [Test]
    public async Task RunLog_IsEmptyUntilTheDaemonReportsAnything()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        await Assert.That(vm.RunLog).IsEmpty();
        await Assert.That(vm.HasRunLog).IsFalse();
        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.ExecutingNodeId).IsNull();
        await Assert.That(vm.Nodes.Any(node => node.IsExecuting)).IsFalse();
        // Пустой текст отличает «ничего не запускалось» от «ничего не записывается»: полосу
        // питают только пока подписку держат «Макросы».
        await Assert.That(vm.RunLogEmptyText).IsEqualTo(Strings.Editor_RunLog_EmptyUnsubscribed);
        await Assert.That(vm.RunLogEmptyText).IsNotEqualTo(Strings.Editor_RunLog_EmptyRecorded);
    }

    [Test]
    public async Task ExecutingNodeId_LightsExactlyOneBox()
    {
        using var vm = CreateEditor();
        vm.LoadGraph(BootLike());

        vm.ExecutingNodeId = Ids.Of("recognize");

        await Assert.That(vm.Nodes.Where(node => node.IsExecuting).Select(node => node.DisplayName))
            .IsEquivalentTo(new[] { "recognize" });
        await Assert.That(vm.CanvasEdges
                .Single(edge => edge.Source.DisplayName == "recognize" && edge.OutcomeIndex == 0).IsActive)
            .IsTrue();

        vm.ExecutingNodeId = null;
        await Assert.That(vm.Nodes.Any(node => node.IsExecuting)).IsFalse();
    }

    [Test]
    public async Task ARealRunFillsTheStripAndWalksTheHighlightAcrossTheCanvas()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x140804);

        daemon.Push(RunEvents.Started(walk));
        await Assert.That(vm.HasRuns).IsTrue();
        await Assert.That(vm.RunChipText).IsEqualTo("0x140804");
        await Assert.That(vm.SelectedRunIsLive).IsTrue();

        // Вход в ноду открывает строку и зажигает коробку; исхода у строки пока нет.
        daemon.Push(RunEvents.Entered(walk, 0, "wait-server"));
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);
        await Assert.That(vm.RunLog[0].Outcome).IsEqualTo(RunLogRowViewModel.PendingOutcome);
        await Assert.That(vm.RunLog[0].IsCurrent).IsTrue();
        await Assert.That(vm.ExecutingNodeId).IsEqualTo(Ids.Of("wait-server"));
        await Assert.That(vm.Nodes.Single(node => node.IsExecuting).DisplayName).IsEqualTo("wait-server");

        // Выход из неё достраивает ТУ ЖЕ строку на месте — полоса не имеет права мигнуть новой.
        var openRow = vm.RunLog[0];
        daemon.Push(RunEvents.Exited(walk, 1200, "wait-server", RunOutcomes.Found, "A @ 1190,1802", 1200));
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);
        await Assert.That(ReferenceEquals(vm.RunLog[0], openRow)).IsTrue();
        await Assert.That(openRow.Elapsed).IsEqualTo("0:00.0");
        await Assert.That(openRow.Outcome).IsEqualTo(Strings.Editor_RunLog_OutcomeFound);
        await Assert.That(openRow.OutcomeIsAccent).IsTrue();
        // Деталь демона перенесена дословно, а длительность приписана в форме СЕКУНД — 1200 мс
        // выше порога. Разделитель « · » собирает сама view-model, он не из ресурсов.
        await Assert.That(openRow.Detail).StartsWith("A @ 1190,1802 · ");
        await Assert.That(Msg.Arg(openRow.Detail.Split(" · ")[^1], Strings.Editor_RunLog_DurationSec))
            .IsEqualTo("1.2");
        await Assert.That(openRow.IsCurrent).IsFalse();

        // Подсветка следует за обходчиком, по одной коробке за раз.
        daemon.Push(RunEvents.Entered(walk, 1200, "click-server"));
        await Assert.That(vm.Nodes.Where(node => node.IsExecuting).Select(node => node.DisplayName))
            .IsEquivalentTo(new[] { "click-server" });
        await Assert.That(vm.CanvasEdges.Single(edge => edge.Source.DisplayName == "click-server").IsActive).IsTrue();
        daemon.Push(RunEvents.Exited(walk, 1240, "click-server", RunOutcomes.Ok, "PostMessage 1192,1805", 40));
        // …а здесь 40 мс не дотягивают до секунды, и выбрана форма МИЛЛИСЕКУНД.
        await Assert.That(vm.RunLog[1].Detail).StartsWith("PostMessage 1192,1805 · ");
        await Assert.That(Msg.Arg(vm.RunLog[1].Detail.Split(" · ")[^1], Strings.Editor_RunLog_DurationMs))
            .IsEqualTo("40");
        await Assert.That(vm.RunLog[1].Outcome).IsEqualTo(Strings.Editor_RunLog_OutcomeOk);
        await Assert.That(vm.RunLog[1].OutcomeIsAccent).IsFalse();
        await Assert.That(vm.RunLog[1].Elapsed).IsEqualTo("0:01.2");

        // Прогон кончается: лог остаётся, чтобы его читать, а канва гаснет.
        daemon.Push(RunEvents.Finished(walk, 1300, RunOutcomes.Completed));
        await Assert.That(vm.ExecutingNodeId).IsNull();
        await Assert.That(vm.Nodes.Any(node => node.IsExecuting)).IsFalse();
        await Assert.That(vm.RunLog).Count().IsEqualTo(2);
        await Assert.That(vm.SelectedRunIsLive).IsFalse();
    }

    [Test]
    public async Task AFanOutOverTenWindows_LightsOneBox_AndThePickerReachesTheRest()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        // Тот самый случай, ради которого выбор обхода и существует: один граф, десять обходов,
        // десять разных живых нод.
        var walks = Enumerable.Range(0, 10)
            .Select(i => RunEvents.Walk("pw-boot", hwnd: 0x100 + i))
            .ToList();
        var nodes = new[] { "wait-server", "click-server", "wait-char", "click-char", "open-stats" };
        foreach (var (walk, i) in walks.Select((w, i) => (w, i)))
        {
            daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, i * 10, nodes[i % nodes.Length]));
        }

        await Assert.That(vm.Runs).Count().IsEqualTo(10);
        // Ровно одна: девять зажжённых коробок на одном графе — та самая пестрота, которой этой
        // волне и надо было избежать.
        await Assert.That(vm.Nodes.Count(node => node.IsExecuting)).IsEqualTo(1);

        // Выбор остаётся за первым обходом: живой прогон никогда не отбирает более поздний
        // собрат.
        await Assert.That(vm.RunChipText).IsEqualTo("0x100");
        await Assert.That(vm.RunPositionText).IsEqualTo("1 / 10");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo(Ids.Of("wait-server"));

        vm.SelectNextRun();
        await Assert.That(vm.RunChipText).IsEqualTo("0x101");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo(Ids.Of("click-server"));
        await Assert.That(vm.Nodes.Count(node => node.IsExecuting)).IsEqualTo(1);
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);

        // ◂ с первой записи заворачивает на последнюю, а не упирается в тупик.
        vm.SelectPreviousRun();
        vm.SelectPreviousRun();
        await Assert.That(vm.RunChipText).IsEqualTo("0x109");
        await Assert.That(vm.RunPositionText).IsEqualTo("10 / 10");
    }

    [Test]
    public async Task AFinishedSelectionStepsAsideForANewRun_ButALiveOneDoesNot()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        var first = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(first), RunEvents.Entered(first, 0, "wait-server"));
        await Assert.That(vm.RunChipText).IsEqualTo("0x1");

        // Живой выбор устоит против новичка.
        var second = RunEvents.Walk("pw-boot", hwnd: 0x2);
        daemon.Push(RunEvents.Started(second));
        await Assert.That(vm.RunChipText).IsEqualTo("0x1");

        // А вот как только он завершился, эстафету принимает следующий прогон, — иначе
        // пользователь останется смотреть на лог, который перестал двигаться.
        daemon.Push(RunEvents.Finished(first, 100, RunOutcomes.Completed));
        var third = RunEvents.Walk("pw-boot", hwnd: 0x3);
        daemon.Push(RunEvents.Started(third), RunEvents.Entered(third, 0, "open-stats"));
        await Assert.That(vm.RunChipText).IsEqualTo("0x3");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo(Ids.Of("open-stats"));
    }

    [Test]
    public async Task OpeningAnotherMacroMidRun_SwapsTheLogAndComesBackToIt()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        var boot = RunEvents.Walk("pw-boot", hwnd: 0xA);
        var other = RunEvents.Walk("pw-assist", hwnd: 0xB);
        daemon.Push(
            RunEvents.Started(boot), RunEvents.Entered(boot, 0, "recognize"),
            RunEvents.Started(other), RunEvents.Entered(other, 0, "n1"));

        // Обходы другого графа отслеживаются, но на экране не меняют ничего.
        await Assert.That(vm.Runs).Count().IsEqualTo(1);
        await Assert.That(vm.ExecutingNodeId).IsEqualTo(Ids.Of("recognize"));

        vm.LoadGraph(new MacroGraph
        {
            Name = "pw-assist",
            StartNodeId = Ids.Of("n1"),
            Nodes = [new DelayNode { Id = Ids.Of("n1"), DisplayName = "n1", Ms = 5 }],
        });
        await Assert.That(vm.RunChipText).IsEqualTo("0xB");
        await Assert.That(vm.ExecutingNodeId).IsEqualTo(Ids.Of("n1"));
        await Assert.That(vm.Nodes.Single(node => node.IsExecuting).DisplayName).IsEqualTo("n1");

        // Обратно: лог первого обхода уцелел, а не был выброшен.
        vm.LoadGraph(BootLike());
        await Assert.That(vm.RunChipText).IsEqualTo("0xA");
        await Assert.That(vm.RunLog).Count().IsEqualTo(1);
        await Assert.That(vm.ExecutingNodeId).IsEqualTo(Ids.Of("recognize"));
    }

    [Test]
    public async Task APartialLogSaysSo_WhetherItJoinedLateOrTheDaemonDroppedEvents()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.SubscribeRunEvents, new[]
            {
                // То, чем отвечает демон: обходы, уже находящиеся в полёте, и помеченные как
                // таковые.
                new RunWalkDto(Guid.NewGuid(), Guid.NewGuid(), "pw-boot", 0x140804, 0, DateTimeOffset.UtcNow,
                    FromStart: false),
            });
        using var vm = CreateEditor(client);
        vm.LoadGraph(BootLike());

        await vm.SetRunEventSubscriptionAsync(true);

        await Assert.That(vm.HasRuns).IsTrue();
        await Assert.That(vm.HasRunLogNotice).IsTrue();
        await Assert.That(vm.RunLogNotice).IsEqualTo(Strings.Editor_RunLog_NoticePartial);
        // Подписка есть — значит пусто уже по другой причине, и текст обязан быть другим.
        await Assert.That(vm.RunLogEmptyText).IsEqualTo(Strings.Editor_RunLog_EmptyRecorded);
        await Assert.That(vm.RunLogEmptyText).IsNotEqualTo(Strings.Editor_RunLog_EmptyUnsubscribed);

        // О выброшенной пачке тоже сообщают — иначе получился бы лог с невидимой дырой. Обе
        // причины складываются: выбран по-прежнему тот обход, к которому подключились посреди
        // прогона (живой никогда не отбирают), так что у него И начала нет, И демон с тех пор
        // успел что-то выбросить.
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x2);
        client.RaiseEvent(IpcMessageTypes.RunEvents, new RunEventBatch([RunEvents.Started(walk)], Dropped: 12));
        // Обе причины сложились в одну строку, и вторая несёт ЧИСЛО выброшенных событий.
        var notice = vm.RunLogNotice!.Split(" · ");
        await Assert.That(notice[0]).IsEqualTo(Strings.Editor_RunLog_NoticePartial);
        await Assert.That(Msg.Arg(notice[1], Strings.Editor_RunLog_NoticeDropped)).IsEqualTo("12");
    }

    [Test]
    public async Task ReconnectDropsTheHistory_AndReSubscribes()
    {
        var client = new FakeIpcClient().Respond(IpcMessageTypes.SubscribeRunEvents, Array.Empty<RunWalkDto>());
        using var vm = CreateEditor(client);
        vm.LoadGraph(BootLike());
        await vm.SetRunEventSubscriptionAsync(true);

        var walk = RunEvents.Walk("pw-boot", hwnd: 0x7);
        client.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "wait-server"));
        await Assert.That(vm.HasRunLog).IsTrue();

        client.RaiseConnected();

        // Подписку демон забывает вместе с соединением, а размер провала узнать неоткуда, —
        // поэтому панель переспрашивает и начинает с чистого листа, а не продолжает битый лог.
        await Assert.That(vm.HasRunLog).IsFalse();
        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.ExecutingNodeId).IsNull();
        await Assert.That(client.CountOf(IpcMessageTypes.SubscribeRunEvents)).IsEqualTo(2);
    }

    [Test]
    public async Task AnEventForAnUnknownWalkIsIgnoredRatherThanInventingARun()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());

        // Возможно только тогда, когда WalkStarted попал в пачку, которую демону пришлось
        // выбросить. У прогона, придуманного здесь на месте, не было бы имени макроса, и подшить
        // его к какому-либо графу не вышло бы.
        var orphan = RunEvents.Walk("pw-boot", hwnd: 0x9);
        daemon.Push(RunEvents.Entered(orphan, 0, "wait-server"));

        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.ExecutingNodeId).IsNull();
    }

    [Test]
    public async Task ClearRunLog_ForgetsEverythingWithoutStoppingAnything()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x5);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "wait-server"));

        vm.ClearRunLog();

        await Assert.That(vm.HasRuns).IsFalse();
        await Assert.That(vm.RunLog).IsEmpty();
        await Assert.That(vm.ExecutingNodeId).IsNull();
    }

    [Test]
    public async Task ALogCappedAtItsRowLimitDropsTheOldestRows()
    {
        using var vm = CreateEditor(out var daemon);
        vm.LoadGraph(BootLike());
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x6);
        daemon.Push(RunEvents.Started(walk));

        // Зацикленный макрос не имеет права раздувать панель без предела.
        for (var i = 0; i < MacroRunViewModel.MaxRows + 25; i++)
        {
            daemon.Push(RunEvents.Entered(walk, i, "await-stats"),
                RunEvents.Exited(walk, i, "await-stats", RunOutcomes.Ok, null, 1));
        }

        await Assert.That(vm.RunLog).Count().IsEqualTo(MacroRunViewModel.MaxRows);
        await Assert.That(vm.RunLog[0].Elapsed).IsEqualTo(RunLogRowViewModel.FormatElapsed(25));
    }

    // ---- обвес коробки -------------------------------------------------------------------------

    [Test]
    public async Task ConditionalBoxesAreTallerThanActionBoxes()
    {
        var action = NodeRowViewModel.FromNode(new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 1 });
        var conditional = NodeRowViewModel.FromNode(new FindElementNode
            { Id = Ids.Of("f"), DisplayName = "f", Template = "t" });

        await Assert.That(action.IsConditional).IsFalse();
        await Assert.That(conditional.IsConditional).IsTrue();
        await Assert.That(action.LayoutHeight).IsEqualTo(CanvasMetrics.ActionNodeHeight);
        await Assert.That(conditional.LayoutHeight).IsEqualTo(CanvasMetrics.ConditionalNodeHeight);
    }

    [Test]
    public async Task BoxSummary_FollowsTheFieldsItDescribes()
    {
        var row = (RecognizeTagNodeRowViewModel)NodeRowViewModel.FromNode(new RecognizeTagNode
        {
            Id = Ids.Of("r"), DisplayName = "r",
            TemplateSet = "classes",
            Region = new ScreenRect(0, 0, 160, 35),
        });
        // Подпись несёт имя набора и размеры области; « · » и «160×35» собирает view-model сама.
        await Assert.That(Msg.Arg(row.Summary.Split(" · ")[0], Strings.Node_Summary_TemplateSet))
            .IsEqualTo("classes");
        await Assert.That(row.Summary.Split(" · ")[^1]).IsEqualTo("160×35");

        var seen = false;
        row.PropertyChanged += (_, e) => seen |= e.PropertyName == nameof(NodeRowViewModel.Summary);

        // Область живёт в собственной view-model; без переброски её изменение до коробки не
        // дошло бы никогда, и подпись молча устарела бы.
        row.Region.WidthText = "320";

        await Assert.That(seen).IsTrue();
        // Изменилась ровно ширина — вот она и в подписи.
        await Assert.That(row.Summary.Split(" · ")[^1]).IsEqualTo("320×35");
    }

    // D4 заменила простую текстовую фишку на коробке живым бейджем; Summary теперь — это только
    // СЕЛЕКТОРНАЯ его половина (счётчик окон бейдж ставит впереди — см. TargetBadgeTests).
    [Test]
    public async Task TargetSummary_DescribesTheSelectorInWords()
    {
        var row = NodeRowViewModel.FromNode(
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.A });
        // Три разных ключа на три состояния селектора — тест как раз про выбор между ними:
        // «нет селектора», «селектор есть и пуст» и «есть исключение».
        await Assert.That(row.Target!.Summary).IsEqualTo(Strings.Editor_Targets_TextContext);

        row.Target.UseSelector = true;
        await Assert.That(row.Target.Summary).IsEqualTo(Strings.Editor_Targets_TextAll);
        await Assert.That(row.Target.Summary).IsNotEqualTo(Strings.Editor_Targets_TextContext);

        row.Target.ExcludeText = "Склад";
        await Assert.That(Msg.Arg(row.Target.Summary, Strings.Editor_Targets_TextExcludeOnly))
            .IsEqualTo("Склад");
    }

    // Фишка появляется на коробке, только когда нода отходит от значения по умолчанию. Волна D4
    // это правило сохранила (в заголовке шириной 210px не помещаются разом и подпись типа, и
    // бейдж), так что оно остаётся закреплённым.
    [Test]
    public async Task TargetChip_IsOnlyDrawnWhenTheNodeRoutesByTags()
    {
        var row = NodeRowViewModel.FromNode(
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.A });
        await Assert.That(row.ShowsTargetChip).IsFalse();

        var seen = false;
        row.PropertyChanged += (_, e) => seen |= e.PropertyName == nameof(NodeRowViewModel.ShowsTargetChip);

        row.Target!.UseSelector = true;

        await Assert.That(row.ShowsTargetChip).IsTrue();
        await Assert.That(seen).IsTrue();
    }

    /// <summary>Под-макрос из одной ноды — столько, сколько нужно, чтобы он был валиден.</summary>
    private static MacroSubmacro Submacro(string name)
    {
        var only = new DelayNode { Ms = 1, DisplayName = "delay-1" };
        return new MacroSubmacro(
            Guid.NewGuid(),
            new MacroGraph { Name = name, StartNodeId = only.Id, Nodes = [only] });
    }

    private static MacroGraph Graph(string name) => new()
    {
        Name = name,
        StartNodeId = Ids.Of("n1"),
        Nodes = [new DelayNode { Id = Ids.Of("n1"), DisplayName = "n1", Ms = 1 }],
    };
}
