using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Resources;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

/// <summary>
/// Редактор и под-макросы (F4): выделение куска в функцию, переключение канвы между графами
/// одного бандла, запись их в один файл и третья координата точки останова.
///
/// Библиотека настоящая, как и во всех тестах редактора с волны F3: «сохранить» означает
/// «записать zip и перечитать папку», и подделав это интерфейсом, мы проверяли бы собственный мок
/// вместо того, что действительно ляжет в файл.
/// </summary>
public class SubmacroEditorTests
{
    private static MacroEditorViewModel Editor(TempLibrary library, FakeIpcClient client) =>
        new(client, library.Library, null, null, ImmediateUiDispatcher.Instance);

    /// <summary>a → b → c, все ноды Delay: триггеров нет, так что правило контекста не мешает.</summary>
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

    private static void Mark(MacroEditorViewModel vm, params string[] names)
    {
        foreach (var name in names)
        {
            vm.ToggleMark(vm.Nodes.Single(node => node.DisplayName == name));
        }
    }

    // ---- выделение в под-макрос ------------------------------------------------------------

    [Test]
    public async Task Extract_ReplacesTheMarkedNodesWithACallAndSavesBothGraphs()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Chain());
        using var vm = Editor(library, new FakeIpcClient());
        vm.SelectedMacro = vm.Macros.Single();
        Mark(vm, "b", "c");

        await Assert.That(vm.CanExtractSubmacro).IsTrue();
        // Счётчик на кнопке — не украшение: он однажды застревал на старом значении после
        // извлечения. Проверяется подставленное ЧИСЛО, слова вокруг свободны.
        await Assert.That(Msg.Arg(vm.ExtractLabel, Strings.Editor_Toolbar_ExtractCount)).IsEqualTo("2");
        await Assert.That(vm.ExtractSubmacro("опознать")).IsTrue();

        // На канве остался родитель: одна нода плюс вызов.
        await Assert.That(vm.Nodes.Select(n => n.DisplayName)).IsEquivalentTo(new[] { "a", "sub-1" });
        await Assert.That(vm.Submacros.Select(s => s.Name)).IsEquivalentTo(new[] { "опознать" });
        // Дерево библиотеки показывает новую функцию ЕЩЁ ДО сохранения — иначе кнопка выглядела
        // бы несработавшей.
        await Assert.That(vm.MacroGroups.Single().Items.Select(i => i.Name)).IsEquivalentTo(new[] { "опознать" });

        await Assert.That(await vm.SaveAsync()).IsTrue();

        // В ФАЙЛЕ оба графа: родитель с вызовом и функция из двух нод.
        var entry = library.Library.TryGet("цепочка")!;
        await Assert.That(entry.Submacros).Count().IsEqualTo(1);
        await Assert.That(entry.Submacros[0].Graph.Nodes).Count().IsEqualTo(2);
        var call = entry.Graph!.Nodes.OfType<RunSubmacroNode>().Single();
        await Assert.That(call.SubmacroId).IsEqualTo(entry.Submacros[0].Id);
        // И бандл целиком валиден: ни ошибки, ни предупреждения.
        await Assert.That(entry.Validate()).IsEmpty();
    }

    [Test]
    public async Task Extract_RefusesAndChangesNothing_WhenTheSelectionHasTwoEntries()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        // find → (found: x, notFound: y); отмечаем обе ветки.
        vm.LoadGraph(new MacroGraph
        {
            Name = "развилка",
            StartNodeId = Ids.Of("find"),
            Nodes =
            [
                new FindElementNode
                {
                    Id = Ids.Of("find"), DisplayName = "find", Template = "т",
                    Found = Ids.Of("x"), NotFound = Ids.Of("y"),
                },
                new DelayNode { Id = Ids.Of("x"), DisplayName = "x", Ms = 1 },
                new DelayNode { Id = Ids.Of("y"), DisplayName = "y", Ms = 1 },
            ],
        });
        Mark(vm, "x", "y");

        await Assert.That(vm.ExtractSubmacro()).IsFalse();

        // Отказ назвал ноды, а граф остался нетронутым. Панель обязана донести ИМЕННО отказ
        // «два входа» — с ним пользователь знает, что делать; «не извлекается» бесполезно.
        await Assert.That(Msg.Args(vm.ErrorMessage, Strings.Extraction_Refused_ManyEntries)[1]).Contains("«x»");
        await Assert.That(vm.Nodes).Count().IsEqualTo(3);
        await Assert.That(vm.Submacros).IsEmpty();
    }

    [Test]
    public async Task MarksAreClearedByAClickOnEmptyCanvas_NotBySelectingANode()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        vm.LoadGraph(Chain());
        Mark(vm, "b");

        // Разглядывание графа набор не разрушает: выделение для инспектора и отметка — разные вещи.
        vm.SelectedNode = vm.Nodes[0];
        await Assert.That(vm.MarkedCount).IsEqualTo(1);

        vm.ClearMarks();
        await Assert.That(vm.MarkedCount).IsEqualTo(0);
        await Assert.That(vm.CanExtractSubmacro).IsFalse();
    }

    /// <summary>
    /// ⚠️ Найдено глазами: после создания макроса раздел «Триггеры» и кнопка «+ Под-макрос»
    /// оставались погашенными. Значение <c>ShowsTriggers</c> считалось верно, просто о нём никто
    /// не сообщал — привязка узнавала о нём только при переключении графа. Тест смотрит на
    /// УВЕДОМЛЕНИЕ, потому что именно его и не было.
    /// </summary>
    [Test]
    public async Task OpeningAMacro_AnnouncesThatTriggersAreShownAgain()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        var announced = new List<string?>();
        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        vm.NewMacro();

        await Assert.That(vm.ShowsTriggers).IsTrue();
        await Assert.That(announced).Contains(nameof(MacroEditorViewModel.ShowsTriggers));
    }

    // ---- переключение канвы между графами бандла --------------------------------------------

    [Test]
    public async Task OpeningASubmacro_SwapsTheCanvasAndKeepsEditsOnBothSides()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        vm.LoadGraph(Chain());
        Mark(vm, "b", "c");
        vm.ExtractSubmacro("опознать");
        var id = vm.Submacros.Single().Id;

        await Assert.That(vm.OpenSubmacro(id)).IsTrue();
        await Assert.That(vm.IsSubmacroOpen).IsTrue();
        await Assert.That(vm.SubmacroName).IsEqualTo("опознать");
        await Assert.That(vm.OpenGraphPath).IsEqualTo("цепочка ▸ опознать");
        // Триггеров у функции не бывает — раздел не показываем вовсе.
        await Assert.That(vm.ShowsTriggers).IsFalse();
        await Assert.That(vm.Nodes.Select(n => n.DisplayName)).IsEquivalentTo(new[] { "b", "c" });

        // Правим функцию и возвращаемся: правка обязана дожить до записи.
        vm.AddNode(MacroNodeKind.Delay);
        vm.OpenParentGraph();

        await Assert.That(vm.IsSubmacroOpen).IsFalse();
        await Assert.That(vm.Nodes.Select(n => n.DisplayName)).IsEquivalentTo(new[] { "a", "sub-1" });
        await Assert.That(await vm.SaveAsync()).IsTrue();
        await Assert.That(library.Library.TryGet("цепочка")!.Submacros.Single().Graph.Nodes).Count().IsEqualTo(3);
    }

    /// <summary>
    /// Правка функции — это правка того же файла, значит редактор обязан считать себя грязным.
    /// Сравнение по одному открытому графу считало бы её отсутствующей до возврата к родителю.
    /// </summary>
    [Test]
    public async Task EditingASubmacro_MakesTheEditorDirty()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        vm.LoadGraph(Chain());
        Mark(vm, "b", "c");
        vm.ExtractSubmacro("опознать");
        await vm.SaveAsync();
        await Assert.That(vm.IsDirty()).IsFalse();

        vm.OpenSubmacro(vm.Submacros.Single().Id);
        vm.SubmacroName = "опознание";

        await Assert.That(vm.IsDirty()).IsTrue();
    }

    [Test]
    public async Task DeletingASubmacro_IsRefusedWhileANodeStillCallsIt()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        vm.LoadGraph(Chain());
        Mark(vm, "b", "c");
        vm.ExtractSubmacro("опознать");
        var id = vm.Submacros.Single().Id;

        await Assert.That(vm.DeleteSubmacro(id)).IsFalse();
        // Названа виноватая нода: «функция ещё нужна» без адреса заставляет искать вызов руками.
        await Assert.That(Msg.Arg(vm.ErrorMessage, Strings.Editor_Status_SubmacroInUse)).Contains("«sub-1»");
        await Assert.That(vm.Submacros).Count().IsEqualTo(1);

        // Убрали вызов — и удаление проходит.
        vm.DeleteNode(vm.Nodes.Single(n => n.DisplayName == "sub-1"));
        await Assert.That(vm.DeleteSubmacro(id)).IsTrue();
        await Assert.That(vm.Submacros).IsEmpty();
    }

    /// <summary>Список ноды вызова предлагает функции ЭТОГО бандла, а не макросы библиотеки.</summary>
    [Test]
    public async Task TheCallNodeOffersOnlyTheSubmacrosOfThisBundle()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Chain("посторонний"));
        using var vm = Editor(library, new FakeIpcClient());
        vm.LoadGraph(Chain());
        Mark(vm, "b", "c");
        vm.ExtractSubmacro("опознать");

        await Assert.That(vm.SubmacroChoices.Select(c => c.Display)).IsEquivalentTo(new[] { "опознать" });
        var row = (RunSubmacroNodeRowViewModel)vm.Nodes.Single(n => n.DisplayName == "sub-1");
        await Assert.That(row.Submacro!.Display).IsEqualTo("опознать");
    }

    /// <summary>
    /// ⚠️ Найдено глазами: сходив в функцию и вернувшись, нода вызова показывала ПУСТОЙ выпадающий
    /// список — и коробка на канве теряла подпись функции. Ссылка при этом была цела: терялся
    /// только список, который строкам раздаёт редактор, а <c>LoadCanvasGraph</c> его не
    /// пересобирал.
    /// </summary>
    [Test]
    public async Task ReturningFromASubmacro_KeepsTheCallNodeShowingIt()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        vm.LoadGraph(Chain());
        Mark(vm, "b", "c");
        vm.ExtractSubmacro("опознать");
        var id = vm.Submacros.Single().Id;

        await vm.SaveAsync();
        vm.OpenSubmacro(id);
        // Наблюдатель за папкой догоняет собственную запись уже после того, как на канве оказалась
        // функция, — именно в этом порядке это и случается вживую.
        library.Library.Refresh();
        vm.OpenParentGraph();

        var row = (RunSubmacroNodeRowViewModel)vm.Nodes.Single(n => n.DisplayName == "sub-1");
        await Assert.That(row.SubmacroId).IsEqualTo(id);
        await Assert.That(row.Submacro).IsNotNull();
        await Assert.That(row.Submacro!.Display).IsEqualTo("опознать");
        await Assert.That(row.Summary).Contains("опознать");
    }

    // ---- точки останова: третья координата ---------------------------------------------------

    /// <summary>
    /// Точка, поставленная в функции, уезжает демону С ЕЁ ID и НЕ снимает точек родителя. До F4
    /// «заменить целиком» делало бы ровно это, потому что ключ был парой.
    /// </summary>
    [Test]
    public async Task BreakpointsAreKeyedByMacroAndSubmacro()
    {
        using var library = new TempLibrary();
        var client = new FakeIpcClient();
        using var vm = Editor(library, client);
        vm.LoadGraph(Chain());
        Mark(vm, "b", "c");
        vm.ExtractSubmacro("опознать");
        await vm.SaveAsync();

        vm.ToggleBreakpoint(vm.Nodes.Single(n => n.DisplayName == "a"));
        var sent = client.PayloadsOf<SetBreakpointsRequest>(IpcMessageTypes.SetBreakpoints);
        var parentSet = sent[^1];
        await Assert.That(parentSet.SubmacroId).IsNull();
        await Assert.That(parentSet.NodeIds!).IsEquivalentTo(new[] { Ids.Of("a") });

        var id = vm.Submacros.Single().Id;
        vm.OpenSubmacro(id);
        vm.ToggleBreakpoint(vm.Nodes.Single(n => n.DisplayName == "b"));

        sent = client.PayloadsOf<SetBreakpointsRequest>(IpcMessageTypes.SetBreakpoints);
        var subSet = sent[^1];
        await Assert.That(subSet.SubmacroId).IsEqualTo(id);
        await Assert.That(subSet.NodeIds!).IsEquivalentTo(new[] { Ids.Of("b") });
        await Assert.That(subSet.MacroName).IsEqualTo("цепочка");

        // А вернувшись к родителю, его точка на месте: наборы разошлись по ключам, а не затёрли
        // друг друга.
        vm.OpenParentGraph();
        await Assert.That(vm.BreakpointNodeIds).IsEquivalentTo(new[] { Ids.Of("a") });
    }

    // ---- дерево библиотеки ------------------------------------------------------------------

    [Test]
    public async Task OpeningASubmacroMovesTheHighlightOffTheMacroRow()
    {
        using var library = new TempLibrary();
        using var vm = Editor(library, new FakeIpcClient());
        vm.LoadGraph(Chain());
        Mark(vm, "b", "c");
        vm.ExtractSubmacro("опознать");
        await vm.SaveAsync();

        var group = vm.MacroGroups.Single();
        await Assert.That(group.Macro.IsCurrent).IsTrue();
        await Assert.That(group.Items.Single().IsCurrent).IsFalse();

        vm.OpenSubmacro(vm.Submacros.Single().Id);

        // Подсвечена ровно одна строка на всё дерево: на канве ровно один граф.
        await Assert.That(vm.MacroGroups.Single().Macro.IsCurrent).IsFalse();
        await Assert.That(vm.MacroGroups.Single().Items.Single().IsCurrent).IsTrue();
    }

    /// <summary>Бандл, пришедший от другого человека, открывается со всеми своими функциями.</summary>
    [Test]
    public async Task AnImportedBundleOpensWithItsSubmacros()
    {
        using var library = new TempLibrary();
        var only = new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 1 };
        var sub = new MacroSubmacro(
            Ids.Of("s"),
            new MacroGraph { Name = "чужая функция", StartNodeId = only.Id, Nodes = [only] });
        library.WriteExternally(
            new MacroGraph
            {
                Name = "чужой",
                StartNodeId = Ids.Of("call"),
                Nodes = [new RunSubmacroNode { Id = Ids.Of("call"), DisplayName = "call", SubmacroId = sub.Id }],
            },
            [sub]);

        using var vm = Editor(library, new FakeIpcClient());
        vm.SelectedMacro = vm.Macros.Single();

        await Assert.That(vm.Submacros.Select(s => s.Name)).IsEquivalentTo(new[] { "чужая функция" });
        await Assert.That(vm.Issues).IsEmpty();

        // ⚠️ Найдено глазами: выпадающий список ноды вызова оставался пустым, а коробка на канве
        // теряла подпись функции. Список собирался только при пересборке библиотеки, а та
        // отрабатывает ДО загрузки бандла — то есть когда под-макросов ещё нет.
        var call = (RunSubmacroNodeRowViewModel)vm.Nodes.Single();
        await Assert.That(call.Submacro!.Display).IsEqualTo("чужая функция");
        await Assert.That(call.Summary).Contains("чужая функция");

        await Assert.That(vm.OpenSubmacro(sub.Id)).IsTrue();
        await Assert.That(vm.Nodes.Single().DisplayName).IsEqualTo("d");
    }
}
