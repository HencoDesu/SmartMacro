using System.Globalization;
using FakeItEasy;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// Поведение редактора макросов, без окон.
//
// Стадия 3 отдала библиотеку демону и проверяла редактор через заглушку IPC. Волна F3 вернула
// авторство панели, и заглушки не стало: под редактором теперь НАСТОЯЩАЯ папка macros/ во
// временном каталоге (см. TempLibrary). Так и должно быть — «сохранить» означает «записать zip и
// перечитать папку», а подделав это интерфейсом, мы проверяли бы вместо атомарной записи, переноса
// шаблонов при переименовании и нечитаемого бандла в списке собственный мок.
//
// Демон при этом никуда не делся, но отвечает теперь только за своё: прогоны, отказы регистрации
// хоткеев, отладчик. Его по-прежнему изображает FakeIpcClient.
public class MacroEditorViewModelTests
{
    /// <summary>Панель целиком: настоящая папка макросов плюс поддельный демон над ней.</summary>
    private sealed class Panel : IDisposable
    {
        public Panel()
        {
            Client.Respond(IpcMessageTypes.GetRunningMacros, _ => Runs.ToArray());
        }

        public TempLibrary Library { get; } = new();

        public FakeIpcClient Client { get; } = new();

        public List<RunningMacroDto> Runs { get; } = [];

        /// <summary>Граф из библиотеки или <c>null</c> — так на неё смотрит тест.</summary>
        public MacroGraph? Find(string name) => Library.Library.TryGet(name)?.Graph;

        /// <summary>Сколько макросов лежит в папке, включая нечитаемые.</summary>
        public int Count => Library.Library.Entries.Count;

        /// <summary>Пишет бандл мимо панели — так выглядит правка проводником или другой сборкой.</summary>
        public void WriteExternally(MacroGraph graph) => Library.WriteExternally(graph);

        public void SetRuns(params RunningMacroDto[] runs)
        {
            Runs.Clear();
            Runs.AddRange(runs);
            Client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, Runs.ToArray());
        }

        public void Dispose() => Library.Dispose();
    }

    private static MacroEditorViewModel CreateEditor(
        Panel panel,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null,
        IMacroNameConflictPrompt? conflicts = null) =>
        new(panel.Client, panel.Library.Library, launcher, hotkeys, ImmediateUiDispatcher.Instance, conflicts);

    /// <summary>Пользователь, отвечающий на вопрос о занятом имени всегда одинаково.</summary>
    private static IMacroNameConflictPrompt Answering(
        MacroNameConflictChoice choice,
        List<MacroNameConflict>? asked = null)
    {
        var prompt = A.Fake<IMacroNameConflictPrompt>();
        A.CallTo(() => prompt.AskAsync(A<MacroNameConflict>._))
            .ReturnsLazily((MacroNameConflict conflict) =>
            {
                asked?.Add(conflict);
                return Task.FromResult(choice);
            });
        return prompt;
    }

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
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
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
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain());

        vm.DeleteNode(vm.Nodes.Single(n => n.DisplayName == "a"));

        await Assert.That(vm.StartNodeId).IsEqualTo(Ids.Of("b"));
        await Assert.That(vm.BuildGraph().StartNodeId).IsEqualTo(Ids.Of("b"));
    }

    [Test]
    public async Task DeletingEveryNode_LeavesAnEmptyStart_WithoutThrowing()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
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
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
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
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain());

        // Безымянная нода читалась бы в полосе лога как пропущенная строка.
        vm.Nodes[0].DisplayName = "   ";

        await Assert.That(vm.Nodes[0].DisplayName).IsEqualTo("a");
    }

    [Test]
    public async Task AddNode_NamesNodesAfterTheirType_AndSeedsTheStartOfAnEmptyGraph()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
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
            .IsEquivalentTo(new[] { Strings.Node_Edge_End, "key-1", "click-2" });
    }

    // ---- что не пускает сохранить ------------------------------------------------------------

    [Test]
    public async Task Save_WritesTheBundle_AndTheGraphLandsInTheLibrary()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain("рабочий"));

        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsTrue();
        await Assert.That(File.Exists(daemon.Library.PathFor("рабочий"))).IsTrue();
        await Assert.That(daemon.Find("рабочий")).IsNotNull();
        // Ни одного запроса ПРО МАКРОС по трубе не ушло: писать — дело панели. Всё, что редактор
        // спрашивает у демона, — это его собственное состояние.
        await Assert.That(daemon.Client.Requests.Select(r => r.Type).Distinct().Order().ToList())
            .IsEquivalentTo(new List<string>
            {
                IpcMessageTypes.GetBreakpoints,
                IpcMessageTypes.GetHotkeyFailures,
                IpcMessageTypes.GetRunningMacros,
                IpcMessageTypes.GetWindows,
            }.Order().ToList());
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsFalse();
        await Assert.That(vm.IsDirty()).IsFalse();
    }

    [Test]
    public async Task Save_BlockedByAValidationError_RendersTheIssues_AndTheEditorStaysDirty()
    {
        using var daemon = new Panel();
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
        await Assert.That(daemon.Count).IsEqualTo(0);
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsTrue();
        await Assert.That(vm.ErrorMessage).IsNotNull();
        await Assert.That(vm.IsDirty()).IsTrue();
    }

    [Test]
    public async Task Save_IsBlockedByARowLevelInputError_AndNothingIsWritten()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain("кривая-пауза"));
        ((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText = "две секунды";

        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsFalse();
        // Недопечатанное число до файла не доходит: BuildGraph втихую подставил бы что-нибудь,
        // о чём пользователь не просил.
        await Assert.That(daemon.Count).IsEqualTo(0);
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsTrue();
    }

    [Test]
    public async Task Save_IsBlockedByAnUnusableName()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain());
        vm.MacroName = "плохое/имя";

        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsFalse();
        await Assert.That(daemon.Count).IsEqualTo(0);
        await Assert.That(vm.Issues.Any(i => i.IsError)).IsTrue();
    }

    [Test]
    public async Task Save_SucceedsWithWarnings_WhichAreReDerivedLocally()
    {
        using var daemon = new Panel();
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
        // «Сохранено с предупреждениями (N)» — не «Сохранено»; и N обязано совпасть с тем,
        // что панель показывает в списке замечаний, иначе строка состояния врёт.
        await Assert.That(Msg.Arg(vm.StatusMessage, Strings.Editor_Status_SavedWithWarnings))
            .IsEqualTo(vm.Issues.Count(i => !i.IsError).ToString(CultureInfo.CurrentCulture));
        await Assert.That(vm.StatusMessage).IsNotEqualTo(Strings.Editor_Status_Saved);
        await Assert.That(daemon.Find("с-предупреждением")).IsNotNull();
    }

    [Test]
    public async Task Rename_SavesTheNewNameAndDeletesTheOldOne()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        vm.LoadGraph(Chain("старое-имя"));
        await vm.SaveAsync();

        vm.MacroName = "новое-имя";
        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsTrue();
        await Assert.That(daemon.Find("новое-имя")).IsNotNull();
        await Assert.That(daemon.Find("старое-имя")).IsNull();
        await Assert.That(File.Exists(daemon.Library.PathFor("старое-имя"))).IsFalse();
        await Assert.That(vm.Macros.Select(m => m.Name)).IsEquivalentTo(new[] { "новое-имя" });
    }

    // ---- занятое имя ----------------------------------------------------------------------
    //
    // Дефект, ради которого вопрос и заведён: переименовать «pw-buff» в существующее «pw-login»
    // значило получить файл с графом от одного макроса и шаблонами от другого, после чего
    // исходный файл удалялся. Два макроса становились одним, а в статусе значилось «Сохранено».
    //
    // Различить «сохраняюсь под своим именем» и «затираю чужой макрос» может ТОЛЬКО эта VM: имя
    // файла и есть личность, и папке оба случая выглядят одинаково.

    [Test]
    public async Task Rename_OntoATakenName_AsksAndOnCancelWritesNothing()
    {
        using var daemon = new Panel();
        daemon.Library.WriteExternally(Chain("pw-login"), ("classes/Лучник.png", "вырезано руками"));
        var asked = new List<MacroNameConflict>();
        using var vm = CreateEditor(daemon, conflicts: Answering(MacroNameConflictChoice.Cancel, asked));

        vm.LoadGraph(Chain("pw-buff"));
        await vm.SaveAsync();
        vm.MacroName = "pw-login";
        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsFalse();
        await Assert.That(asked.Select(conflict => conflict.Name)).IsEquivalentTo(new[] { "pw-login" });
        // Чужой макрос цел — вместе с шаблоном, ради которого весь формат и заведён.
        await Assert.That(daemon.Library.Library.TryGet("pw-login")!.TemplatePaths)
            .IsEquivalentTo(new[] { "classes/Лучник.png" });
        // И свой на месте: отказ не должен стоить пользователю его собственного файла.
        await Assert.That(daemon.Find("pw-buff")).IsNotNull();
        // Набранное имя не отбираем: пользователь передумает или поправит.
        await Assert.That(vm.MacroName).IsEqualTo("pw-login");
    }

    [Test]
    public async Task Rename_OntoATakenName_Replace_PutsOurBundleThere()
    {
        using var daemon = new Panel();
        daemon.Library.WriteExternally(Chain("pw-login"), ("classes/Лучник.png", "вырезано руками"));
        using var vm = CreateEditor(daemon, conflicts: Answering(MacroNameConflictChoice.Replace));

        vm.LoadGraph(Chain("pw-buff"));
        await vm.SaveAsync();
        daemon.Library.Library.AddTemplate("pw-buff", null, "Бафф", "png"u8.ToArray());
        vm.MacroName = "pw-login";
        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsTrue();
        // В файле теперь НАШ бандл целиком: и граф, и наши шаблоны — не смесь из двух макросов.
        await Assert.That(daemon.Library.Library.TryGet("pw-login")!.TemplatePaths)
            .IsEquivalentTo(new[] { "Бафф.png" });
        await Assert.That(daemon.Find("pw-buff")).IsNull();
        await Assert.That(vm.Macros.Select(m => m.Name)).IsEquivalentTo(new[] { "pw-login" });
    }

    [Test]
    public async Task Rename_OntoATakenName_FreeName_RenamesInTheEditorToo()
    {
        using var daemon = new Panel();
        daemon.Library.WriteExternally(Chain("pw-login"), ("classes/Лучник.png", "вырезано руками"));
        using var vm = CreateEditor(daemon, conflicts: Answering(MacroNameConflictChoice.FreeName));

        vm.LoadGraph(Chain("pw-buff"));
        await vm.SaveAsync();
        vm.MacroName = "pw-login";
        var saved = await vm.SaveAsync();

        await Assert.That(saved).IsTrue();
        // Имя в редакторе обязано стать тем, что человеку предложили: иначе поле говорит одно, а
        // библиотека другое.
        await Assert.That(vm.MacroName).IsEqualTo("pw-login-2");
        await Assert.That(daemon.Find("pw-login-2")).IsNotNull();
        await Assert.That(daemon.Find("pw-buff")).IsNull();
        await Assert.That(daemon.Library.Library.TryGet("pw-login")!.TemplatePaths)
            .IsEquivalentTo(new[] { "classes/Лучник.png" });
    }

    // Обратная сторона: обычное пересохранение вопросов НЕ задаёт. Диалог на каждое «Сохранить»
    // был бы хуже дефекта, который он лечит.
    [Test]
    public async Task Save_UnderItsOwnName_NeverAsks()
    {
        using var daemon = new Panel();
        var prompt = Answering(MacroNameConflictChoice.Cancel);
        using var vm = CreateEditor(daemon, conflicts: prompt);

        vm.LoadGraph(Chain("обычный"));
        await vm.SaveAsync();
        var again = await vm.SaveAsync();

        await Assert.That(again).IsTrue();
        A.CallTo(() => prompt.AskAsync(A<MacroNameConflict>._)).MustNotHaveHappened();
    }

    // Спросить не у кого — значит отмена. Молча заменить чужой макрос хуже, чем не сохранить.
    [Test]
    public async Task Rename_OntoATakenName_WithNobodyToAsk_IsACancel()
    {
        using var daemon = new Panel();
        daemon.Library.WriteExternally(Chain("pw-login"));
        using var vm = CreateEditor(daemon);

        vm.LoadGraph(Chain("pw-buff"));
        await vm.SaveAsync();
        vm.MacroName = "pw-login";

        await Assert.That(await vm.SaveAsync()).IsFalse();
        await Assert.That(daemon.Find("pw-buff")).IsNotNull();
    }

    // Цена замены называется ЧИСЛАМИ, а не словом «данные»: «Заменить?» без уточнения — вопрос,
    // на который отвечают «да» не читая.
    [Test]
    public async Task TheQuestionNamesWhatWouldBeLost()
    {
        using var daemon = new Panel();
        daemon.Library.WriteExternally(
            Chain("pw-login"),
            ("classes/Лучник.png", "а"),
            ("classes/Жрец.png", "б"));
        var asked = new List<MacroNameConflict>();
        using var vm = CreateEditor(daemon, conflicts: Answering(MacroNameConflictChoice.Cancel, asked));

        vm.LoadGraph(Chain("pw-buff"));
        await vm.SaveAsync();
        vm.MacroName = "pw-login";
        await vm.SaveAsync();

        var conflict = asked.Single();
        await Assert.That(conflict.Templates).IsEqualTo(2);
        await Assert.That(conflict.FreeName).IsEqualTo("pw-login-2");
        // Форма «два шаблона», а не «2 шаблонов»: согласование здесь и проверяется.
        await Assert.That(conflict.Loss).Contains(
            string.Format(CultureInfo.CurrentCulture, Strings.Dialog_NameConflict_Templates_Few, 2));
    }

    // Нечитаемый бандл в библиотеке — не выдумка: файл будущей версии формата лежит там
    // намеренно. Сказать про него «0 шаблонов» было бы ложью, поэтому вместо чисел едет вердикт
    // читателя.
    [Test]
    public async Task TheQuestionAdmitsWhenItCannotSeeInsideTheVictim()
    {
        using var daemon = new Panel();
        daemon.Library.WriteJunk("pw-login");
        var asked = new List<MacroNameConflict>();
        using var vm = CreateEditor(daemon, conflicts: Answering(MacroNameConflictChoice.Cancel, asked));

        vm.LoadGraph(Chain("pw-buff"));
        await vm.SaveAsync();
        vm.MacroName = "pw-login";
        await vm.SaveAsync();

        var conflict = asked.Single();
        await Assert.That(conflict.Fault).IsNotNull();
        await Assert.That(Msg.Is(conflict.Loss, Strings.Dialog_NameConflict_LossUnreadable)).IsTrue();
    }

    // ---- импорт под занятым именем ------------------------------------------------------------
    //
    // Раньше импорт молча приписывал суффикс: файл ложился рядом с тем, который пользователь,
    // возможно, и собирался обновить. Вариант остался — выбирать его перестала программа.

    [Test]
    [Arguments(MacroNameConflictChoice.FreeName, "гость-2", 2)]
    [Arguments(MacroNameConflictChoice.Replace, "гость", 1)]
    public async Task Import_UnderATakenName_Asks(MacroNameConflictChoice choice, string expected, int count)
    {
        using var source = new TempLibrary();
        using var daemon = new Panel();
        source.WriteExternally(Chain("гость"), ("Кнопка.png", "png"));
        daemon.Library.WriteExternally(Chain("гость"));
        using var vm = CreateEditor(daemon, conflicts: Answering(choice));

        var name = await vm.ImportMacroAsync(source.PathFor("гость"));

        await Assert.That(name).IsEqualTo(expected);
        await Assert.That(daemon.Count).IsEqualTo(count);
        // Импортированный бандл приехал целиком, со своим шаблоном.
        await Assert.That(daemon.Library.Library.TryGet(expected)!.TemplatePaths)
            .IsEquivalentTo(new[] { "Кнопка.png" });
    }

    // Отмена — это ОТМЕНА, а не отказ ввода-вывода: пользователь сам так решил, и красная строка
    // ошибки в ответ на собственное решение читается как поломка.
    [Test]
    public async Task Import_UnderATakenName_Cancel_LeavesTheLibraryAloneAndSaysSoCalmly()
    {
        using var source = new TempLibrary();
        using var daemon = new Panel();
        source.WriteExternally(Chain("гость"), ("Кнопка.png", "png"));
        daemon.Library.WriteExternally(Chain("гость"));
        using var vm = CreateEditor(daemon, conflicts: Answering(MacroNameConflictChoice.Cancel));

        var name = await vm.ImportMacroAsync(source.PathFor("гость"));

        await Assert.That(name).IsNull();
        await Assert.That(daemon.Count).IsEqualTo(1);
        await Assert.That(daemon.Library.Library.TryGet("гость")!.TemplatePaths).IsEmpty();
        await Assert.That(vm.ErrorMessage).IsNull();
        await Assert.That(Msg.Arg(vm.StatusMessage, Strings.Editor_Status_ImportCancelled)).IsEqualTo("гость");
    }

    [Test]
    public async Task Save_RoundTripsAGraphWithEveryNodeTypeAcrossTheWire()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        var original = NodeRowRoundTripTests.EveryNodeType();
        // Под-макрос едет вместе с графом: ссылка в никуда с волны F4 — ошибка валидации, и
        // сохранение её не пропустит.
        vm.LoadGraph(original, [NodeRowRoundTripTests.TheSubmacro()]);

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
        using var daemon = new Panel();
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
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);

        vm.NewMacro();

        await Assert.That(vm.IsLibraryEmpty).IsTrue();
        await Assert.That(vm.ShowEmptyLibraryHint).IsFalse();
        await Assert.That(vm.ShowPickMacroHint).IsFalse();
    }

    [Test]
    public async Task Construction_ReadsTheFolder_AndSelectingAMacroLoadsIt()
    {
        using var daemon = new Panel();
        daemon.WriteExternally(Chain("первый"));
        daemon.WriteExternally(Chain("второй"));
        using var vm = CreateEditor(daemon);

        await Assert.That(vm.Macros).Count().IsEqualTo(2);
        await Assert.That(vm.HasOpenMacro).IsFalse();

        vm.SelectedMacro = vm.Macros.Single(m => m.Name == "второй");

        await Assert.That(vm.HasOpenMacro).IsTrue();
        await Assert.That(vm.MacroName).IsEqualTo("второй");
        await Assert.That(vm.Nodes).Count().IsEqualTo(3);
        await Assert.That(vm.IsDirty()).IsFalse();
    }

    [Test]
    // Библиотека — факт файловой системы, а не факт демона: переподключение к ней отношения не
    // имеет, а вот файл, положенный в папку, обязан появиться сам.
    public async Task LibraryChange_ShowsUp_WithoutAskingTheDaemon()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        await Assert.That(vm.Macros).IsEmpty();

        daemon.WriteExternally(Chain("появился-пока-нас-не-было"));

        await Assert.That(vm.Macros.Select(m => m.Name)).IsEquivalentTo(new[] { "появился-пока-нас-не-было" });
    }

    // Единственное, что после F3 означает пуш MacrosChanged: «демон перечитал папку и
    // перерегистрировал хоткеи». Значит, и делать по нему надо ровно одно — перечитать список
    // сочетаний, которые Windows не отдала.
    [Test]
    public async Task MacrosChangedPush_RereadsOnlyTheHotkeyFailures()
    {
        using var daemon = new Panel();
        daemon.Client.Respond(IpcMessageTypes.GetHotkeyFailures, _ => Array.Empty<HotkeyFailureDto>());
        using var vm = CreateEditor(daemon);
        var before = daemon.Client.CountOf(IpcMessageTypes.GetHotkeyFailures);

        daemon.Client.RaiseEvent(IpcMessageTypes.MacrosChanged);

        await Assert.That(daemon.Client.CountOf(IpcMessageTypes.GetHotkeyFailures)).IsEqualTo(before + 1);
        // Библиотеку при этом никто не спрашивает: её панель читает сама.
        await Assert.That(vm.Macros).IsEmpty();
    }

    [Test]
    public async Task ExternalEdit_ReloadsACleanEditor()
    {
        using var daemon = new Panel();
        daemon.WriteExternally(Chain("живой"));
        using var vm = CreateEditor(daemon);
        vm.SelectedMacro = vm.Macros.Single();

        // Кто-то правит файл у нас за спиной; его подхватывает наблюдатель самой панели.
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
    public async Task ExternalEdit_DoesNotClobberUnsavedEdits()
    {
        using var daemon = new Panel();
        daemon.WriteExternally(Chain("живой"));
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
        using var daemon = new Panel();
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
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);

        vm.NewMacro();

        await Assert.That(vm.HasOpenMacro).IsTrue();
        await Assert.That(vm.Nodes).Count().IsEqualTo(1);
        await Assert.That(daemon.Count).IsEqualTo(0);

        await Assert.That(await vm.SaveAsync()).IsTrue();
        await Assert.That(daemon.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteMacro_RemovesTheFile_AndClosesTheEditor()
    {
        using var daemon = new Panel();
        daemon.WriteExternally(Chain("на-удаление"));
        using var vm = CreateEditor(daemon);
        vm.SelectedMacro = vm.Macros.Single();

        var deleted = vm.DeleteMacro(vm.Macros.Single());

        await Assert.That(deleted).IsTrue();
        await Assert.That(File.Exists(daemon.Library.PathFor("на-удаление"))).IsFalse();
        await Assert.That(vm.Macros).IsEmpty();
        await Assert.That(vm.HasOpenMacro).IsFalse();
    }

    [Test]
    public async Task DeleteMacro_ReportsAFileThatWillNotGo()
    {
        using var daemon = new Panel();
        daemon.WriteExternally(Chain("упрямый"));
        using var vm = CreateEditor(daemon);

        // Файл держат открытым БЕЗ права на удаление — так выглядит чужая программа, вцепившаяся
        // в бандл. Windows отвечает на File.Delete отказом, и молчать об этом нельзя.
        using (File.Open(daemon.Library.PathFor("упрямый"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var deleted = vm.DeleteMacro(vm.Macros.Single());
            await Assert.That(deleted).IsFalse();
        }

        await Assert.That(vm.ErrorMessage).IsNotNull();
        await Assert.That(vm.Macros).Count().IsEqualTo(1);
    }

    [Test]
    public async Task FolderPath_IsTheMacrosFolderOfTheInstallationRoot()
    {
        using var daemon = new Panel();
        using var vm = CreateEditor(daemon);
        await Assert.That(vm.FolderPath).IsEqualTo(daemon.Library.FolderPath);
    }

    // ---- запуск, остановка, хоткеи -------------------------------------------------------------

    [Test]
    public async Task Run_GoesThroughTheLauncher()
    {
        using var daemon = new Panel();
        daemon.WriteExternally(Chain("запускаемый"));
        var launcher = A.Fake<IMacroLauncher>();
        using var vm = CreateEditor(daemon, launcher: launcher);

        vm.Run(vm.Macros.Single());

        A.CallTo(() => launcher.RunMacro("запускаемый")).MustHaveHappenedOnceExactly();
        await Assert.That(vm.ErrorMessage).IsNull();
    }

    [Test]
    public async Task RunState_FollowsTheDaemonsRunList_AndStopSendsStopMacro()
    {
        using var daemon = new Panel();
        daemon.WriteExternally(Chain("бегущий"));
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
        using var daemon = new Panel();
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
        using var daemon = new Panel();
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
    public async Task Dispose_UnsubscribesFromBothTheFolderAndTheDaemon()
    {
        using var daemon = new Panel();
        var vm = CreateEditor(daemon);
        vm.Dispose();

        daemon.WriteExternally(Chain("после-dispose"));
        daemon.Client.RaiseConnected();

        await Assert.That(vm.Macros).IsEmpty();
    }
}
