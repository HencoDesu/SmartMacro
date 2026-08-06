using SmartMacro.App.Ipc;
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

/// <summary>
/// Вырезка шаблона со свежего снимка окна (issue #29) — всё, кроме самого окна.
///
/// Диалог сюда не приходит: он шов (<see cref="IRegionCapturePrompt"/>), ровно как вопрос о
/// занятом имени, и по той же причине — иначе половина проверок потребовала бы живой Avalonia и
/// живого клиента игры. Проверяется то, что происходит ПОСЛЕ ответа человека, а там и лежит вся
/// содержательная часть: файл в бандле, четыре поля области, имя шаблона в ноде.
///
/// <b>Что здесь принципиально НЕ проверяется:</b> сам снимок и сама вырезка. Первое требует
/// живого клиента PW под <c>PrintWindow</c>, второе — растрового движка Avalonia. Обоим место в
/// «запусти и посмотри», и в отчёте по волне это сказано прямо.
/// </summary>
public class RegionCaptureTests
{
    // ---- запас у области ------------------------------------------------------------------

    // ГЛАВНОЕ решение волны. Область, совпадающая с шаблоном пиксель в пиксель, превращает
    // «найти» в «проверить, что оно ровно здесь»: позиция скольжения ровно одна, и сдвиг элемента
    // на один пиксель роняет оценку ниже порога.
    [Test]
    public async Task TheRegionIsTheSelectionWithAMarginOnEverySide()
    {
        var region = SearchRegion.Around(new ScreenRect(400, 300, 100, 40), 3840, 2160);

        await Assert.That(region.X).IsEqualTo(400 - SearchRegion.Margin);
        await Assert.That(region.Y).IsEqualTo(300 - SearchRegion.Margin);
        await Assert.That(region.Width).IsEqualTo(100 + (2 * SearchRegion.Margin));
        await Assert.That(region.Height).IsEqualTo(40 + (2 * SearchRegion.Margin));
    }

    // Обрезка по кадру обязательна: без неё выделение у края экрана дало бы отрицательные
    // координаты и ширину за пределами кадра. Сопоставление подожмёт такую область молча — то
    // есть в инспекторе стояло бы одно, а искалось бы в другом.
    [Test]
    public async Task TheMarginIsClippedToTheFrame()
    {
        var region = SearchRegion.Around(new ScreenRect(0, 0, 20, 20), 30, 30);

        await Assert.That(region.X).IsEqualTo(0);
        await Assert.That(region.Y).IsEqualTo(0);
        await Assert.That(region.Width).IsEqualTo(30);
        await Assert.That(region.Height).IsEqualTo(30);
    }

    // Кадр неизвестного размера (картинка с диска, которую никто не мерил) не должен обрезать
    // ничего: соврать про правый край хуже, чем не знать его.
    [Test]
    public async Task AnUnknownFrameSizeClipsNothing()
    {
        var region = SearchRegion.Around(new ScreenRect(500, 500, 5, 5), 0, 0);

        await Assert.That(region.Width).IsEqualTo(5 + (2 * SearchRegion.Margin));
        await Assert.That(region.Height).IsEqualTo(5 + (2 * SearchRegion.Margin));
    }

    // Левый край режется нулём, а не уходит в минус: отрицательная координата в поле области —
    // это число, которое сопоставление молча подожмёт, и инспектор перестанет говорить правду.
    [Test]
    public async Task ASelectionAtTheEdgeDoesNotProduceNegativeCoordinates()
    {
        var region = SearchRegion.Around(new ScreenRect(4, 0, 20, 20), 3840, 2160);

        await Assert.That(region.X).IsEqualTo(0);
        await Assert.That(region.Y).IsEqualTo(0);
        await Assert.That(region.Width).IsEqualTo(4 + 20 + SearchRegion.Margin);
        await Assert.That(region.Height).IsEqualTo(20 + SearchRegion.Margin);
    }

    // ---- аренда побудки на стороне панели --------------------------------------------------

    // Порядок несущий: сперва аренда (ответ на неё означает «окно разбужено и устаканилось»),
    // потом снимок, и только потом отдача. Снимок раньше аренды — это чёрный или устаревший кадр
    // замороженного клиента, то есть ровно та беда, ради которой вся эта переписка и заведена.
    [Test]
    public async Task CaptureTakesTheLease_ThenReleasesIt()
    {
        var client = new FakeIpcClient();
        var service = new IpcWindowCaptureService(client);

        // Нулевой дескриптор — не окно; CapturePng падает на нём сам, и это единственный способ
        // добраться до пути захвата, не имея живого клиента PW под рукой.
        await Assert.That(async () => await service.CaptureAsync(0)).Throws<InvalidOperationException>();

        await Assert.That(client.Requests.Select(request => request.Type)).IsEquivalentTo(new[]
        {
            IpcMessageTypes.AcquireCaptureHook,
            IpcMessageTypes.ReleaseCaptureHook,
        });
    }

    // Отдача стоит в finally, и это половина механизма (вторая — отдача на разрыве у демона).
    // Без неё отказ PrintWindow оставлял бы клиента PW размороженным до самой смены фокуса
    // пользователем — тот же дефект, что четыре скобки без try/finally, найденные ревью.
    [Test]
    public async Task AFailedCaptureStillReleasesTheLease()
    {
        var client = new FakeIpcClient();
        var service = new IpcWindowCaptureService(client);

        await Assert.That(async () => await service.CaptureAsync(0)).Throws<InvalidOperationException>();

        await Assert.That(client.CountOf(IpcMessageTypes.ReleaseCaptureHook)).IsEqualTo(1);
        await Assert.That(client.PayloadsOf<CaptureHookRequest>(IpcMessageTypes.ReleaseCaptureHook)[0].Hwnd)
            .IsEqualTo(0L);
    }

    // Отказ демона («такого окна нет») обязан долететь до вызывающего целиком: панели надо
    // сказать пользователю, ЧТО не так, а не показать чёрный прямоугольник. Аренды при этом нет,
    // значит и отдавать нечего.
    [Test]
    public async Task ARefusedLeaseDoesNotEvenTryToCapture()
    {
        var client = new FakeIpcClient().Fail(IpcMessageTypes.AcquireCaptureHook, "окна нет");
        var service = new IpcWindowCaptureService(client);

        await Assert.That(async () => await service.CaptureAsync(0x10)).Throws<IpcRequestException>();

        await Assert.That(client.CountOf(IpcMessageTypes.ReleaseCaptureHook)).IsEqualTo(0);
    }

    // ---- что делает редактор с ответом человека --------------------------------------------

    /// <summary>Диалог, отвечающий наперёд заданным ответом. Записывает, о чём его спросили.</summary>
    private sealed class FakePrompt : IRegionCapturePrompt
    {
        private readonly Func<RegionCaptureRequest, RegionCaptureResult?> _answer;

        public FakePrompt(Func<RegionCaptureRequest, RegionCaptureResult?> answer) => _answer = answer;

        public FakePrompt(RegionCaptureResult? answer) : this(_ => answer)
        {
        }

        public List<RegionCaptureRequest> Asked { get; } = [];

        public Task<RegionCaptureResult?> AskAsync(RegionCaptureRequest request)
        {
            Asked.Add(request);
            return Task.FromResult(_answer(request));
        }
    }

    private static readonly byte[] Cut = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];

    private static MacroGraph WithFind(string name = "поиск") => new()
    {
        Name = name,
        StartNodeId = Ids.Of("f"),
        Nodes = [new FindElementNode { Id = Ids.Of("f"), DisplayName = "find-1", Template = string.Empty }],
    };

    private static MacroGraph WithRecognize(string set, string name = "опознание") => new()
    {
        Name = name,
        StartNodeId = Ids.Of("r"),
        Nodes =
        [
            new RecognizeTagNode
            {
                Id = Ids.Of("r"),
                DisplayName = "recognize-1",
                TemplateSet = set,
                Region = new ScreenRect(0, 0, 10, 10),
            },
        ],
    };

    private static MacroEditorViewModel Open(TempLibrary library, MacroGraph graph, IRegionCapturePrompt? prompt)
    {
        library.WriteExternally(graph);
        var vm = new MacroEditorViewModel(
            new FakeIpcClient(), library.Library, null, null, ImmediateUiDispatcher.Instance, null, prompt);
        vm.SelectedMacro = vm.Macros.Single(row => row.Name == graph.Name);
        return vm;
    }

    // Счастливый путь целиком: файл лёг в бандл, четыре поля области заполнены выделением С
    // ЗАПАСОМ, а имя шаблона попало в ноду.
    [Test]
    public async Task AnAcceptedCapture_LandsInTheBundle_AndFillsTheNode()
    {
        using var library = new TempLibrary();
        var prompt = new FakePrompt(new RegionCaptureResult(
            "ServerSelect", Cut, new ScreenRect(400, 300, 100, 40), 3840, 2160));
        using var vm = Open(library, WithFind(), prompt);
        var node = (FindElementNodeRowViewModel)vm.Nodes[0];

        await vm.CaptureRegionAsync(node);

        await Assert.That(library.Library.ReadTemplate("поиск", null, "ServerSelect")).IsEquivalentTo(Cut);
        await Assert.That(node.Template).IsEqualTo("ServerSelect");
        await Assert.That(node.Region.XText).IsEqualTo((400 - SearchRegion.Margin).ToString());
        await Assert.That(node.Region.YText).IsEqualTo((300 - SearchRegion.Margin).ToString());
        await Assert.That(node.Region.WidthText).IsEqualTo((100 + (2 * SearchRegion.Margin)).ToString());
        await Assert.That(node.Region.HeightText).IsEqualTo((40 + (2 * SearchRegion.Margin)).ToString());
        await Assert.That(vm.ErrorMessage).IsNull();
    }

    // Для RecognizeTag вырезка едет В НАБОР ноды, а введённое имя — это ТЕГ. Поле набора при этом
    // не трогается: вписать туда тег значило бы направить сопоставление в templates/Лучник/
    // вместо templates/classes/.
    [Test]
    public async Task ACaptureForRecognizeTag_GoesIntoTheNodesSet_AndLeavesTheSetAlone()
    {
        using var library = new TempLibrary();
        var prompt = new FakePrompt(new RegionCaptureResult(
            "Лучник", Cut, new ScreenRect(100, 100, 60, 20), 3840, 2160));
        using var vm = Open(library, WithRecognize("classes"), prompt);
        var node = (RecognizeTagNodeRowViewModel)vm.Nodes[0];

        await vm.CaptureRegionAsync(node);

        await Assert.That(library.Library.ReadTemplate("опознание", "classes", "Лучник")).IsEquivalentTo(Cut);
        await Assert.That(node.TemplateSet).IsEqualTo("classes");
        await Assert.That(prompt.Asked[0].Kind).IsEqualTo(RegionCaptureKind.Tag);
        await Assert.That(prompt.Asked[0].Set).IsEqualTo("classes");
    }

    // Без набора файлу нет пути внутри бандла — и отказ звучит вслух, а не гаснет кнопкой:
    // пользователь нажал ровно ту кнопку, которая ему нужна, и обязан узнать, чего не хватает.
    [Test]
    public async Task RecognizeTagWithoutASet_RefusesBeforeOpeningTheDialog()
    {
        using var library = new TempLibrary();
        var prompt = new FakePrompt(new RegionCaptureResult(
            "Лучник", Cut, new ScreenRect(0, 0, 10, 10), 100, 100));
        using var vm = Open(library, WithRecognize(string.Empty), prompt);

        await vm.CaptureRegionAsync(vm.Nodes[0]);

        await Assert.That(prompt.Asked).IsEmpty();
        await Assert.That(vm.ErrorMessage).IsNotNull();
        await Assert.That(vm.ErrorMessage!).Contains("recognize-1");
    }

    // Отмена — законный и самый частый исход, и после неё не должно остаться ни файла, ни правки.
    [Test]
    public async Task ACancelledDialogChangesNothing()
    {
        using var library = new TempLibrary();
        var prompt = new FakePrompt((RegionCaptureResult?)null);
        using var vm = Open(library, WithFind(), prompt);
        var node = (FindElementNodeRowViewModel)vm.Nodes[0];

        await vm.CaptureRegionAsync(node);

        await Assert.That(node.Template).IsEqualTo(string.Empty);
        await Assert.That(node.Region.WidthText).IsEqualTo("0");
        await Assert.That(vm.Templates.Templates).IsEmpty();
    }

    // Диалог обязан знать, что в этом наборе уже лежит: заменить свой же шаблон свежей вырезкой —
    // обычное дело, но человек должен об этом узнать ДО того, как нажмёт «Вырезать».
    [Test]
    public async Task TheDialogIsToldWhichNamesAreAlreadyTaken()
    {
        using var library = new TempLibrary();
        var prompt = new FakePrompt((RegionCaptureResult?)null);
        using var vm = Open(library, WithFind(), prompt);
        library.Library.AddTemplate("поиск", null, "ServerSelect", Cut);
        vm.Templates.Refresh();

        await vm.CaptureRegionAsync(vm.Nodes[0]);

        await Assert.That(prompt.Asked[0].ExistingNames).Contains("ServerSelect");
    }

    // ⚠️ Ловушка автосохранения. Оба писателя работают в потоке UI и оба синхронны, так что
    // вклиниться им негде; проверяется ВТОРАЯ половина довода — правка шаблонов кладёт граф
    // обратно тем же, каким прочла, поэтому «изменён на диске» не загорается, автосохранение не
    // встаёт, и следующая его запись наследует свежий шаблон из файла.
    [Test]
    public async Task AddingATemplateDoesNotMakeTheEditorThinkTheFileChangedUnderIt()
    {
        using var library = new TempLibrary();
        var prompt = new FakePrompt(new RegionCaptureResult(
            "ServerSelect", Cut, new ScreenRect(400, 300, 100, 40), 3840, 2160));
        using var vm = Open(library, WithFind(), prompt);

        await vm.CaptureRegionAsync(vm.Nodes[0]);

        await Assert.That(vm.ChangedOnDisk).IsFalse();

        // Заполненная область — это несохранённая правка; дописываем её и убеждаемся, что шаблон
        // из файла не пропал.
        vm.FlushAutoSave();

        await Assert.That(vm.ChangedOnDisk).IsFalse();
        await Assert.That(library.Library.ReadTemplate("поиск", null, "ServerSelect")).IsEquivalentTo(Cut);
        var saved = (FindElementNode)library.Library.TryGet("поиск")!.Graph!.Nodes[0];
        await Assert.That(saved.Region!.Value.Width).IsEqualTo(100 + (2 * SearchRegion.Margin));
    }

    // Путь внутри бандла показывается человеку строкой, и считает её ТОТ ЖЕ
    // MacroBundleFormat.TemplatePath, которым потом кладётся файл. Второй копии правила имён в
    // проекте нет — это то самое условие, при котором «что показал редактор» и «что найдёт
    // движок» совпадают по построению.
    [Test]
    public async Task ThePathPreviewIsTheRealPathInsideTheBundle()
    {
        var single = new RegionCaptureRequest(
            RegionCaptureKind.Template, "pw-login", "find-1", null, string.Empty, [], []);
        var inSet = new RegionCaptureRequest(
            RegionCaptureKind.Tag, "pw-login", "recognize-1", "classes", string.Empty, [], []);

        await Assert.That(single.PathPreview("Find")).IsEqualTo(
            MacroBundleFormat.TemplateFolder + "Find.png");
        await Assert.That(inSet.PathPreview("  Лучник  ")).IsEqualTo(
            MacroBundleFormat.TemplateFolder + "classes/Лучник.png");
        await Assert.That(single.PathPreview("   ")).IsEqualTo(string.Empty);
        await Assert.That(inSet.NameLabel).IsEqualTo(Strings.Dialog_Region_NameLabelTag);
        await Assert.That(single.NameLabel).IsEqualTo(Strings.Dialog_Region_NameLabelTemplate);
    }

    // ⚠️ Слеш в имени — не выдуманный случай, и он ТИХИЙ. «classes/Лучник» в поле одиночного
    // шаблона проходит и запись, и разбор, но разбирается как ПАРА «набор classes + имя Лучник»,
    // тогда как FindElement ищет одиночный шаблон, который так и зовётся. Файл лёг бы в бандл, а
    // исполнитель не нашёл бы его никогда — то есть ровно тот класс ошибки, ради которого вся
    // волна и делалась.
    [Test]
    public async Task ANameThatWouldLandSomewhereElseIsRefused()
    {
        var single = new RegionCaptureRequest(
            RegionCaptureKind.Template, "pw-login", "find-1", null, string.Empty, [], []);
        var inSet = new RegionCaptureRequest(
            RegionCaptureKind.Tag, "pw-login", "recognize-1", "classes", string.Empty, [], []);

        await Assert.That(single.IsNameUsable("Find")).IsTrue();
        await Assert.That(single.IsNameUsable("  Find  ")).IsTrue();
        await Assert.That(inSet.IsNameUsable("Лучник")).IsTrue();

        await Assert.That(single.IsNameUsable("classes/Лучник")).IsFalse();
        await Assert.That(single.IsNameUsable(@"classes\Лучник")).IsFalse();
        await Assert.That(inSet.IsNameUsable("а/б")).IsFalse();
        await Assert.That(single.IsNameUsable(string.Empty)).IsFalse();
        await Assert.That(single.IsNameUsable("   ")).IsFalse();

        // А вот «..» проходит, и это НЕ упущение: правило здесь одно — «имя пережило круг через
        // MacroBundleFormat без изменений», — и «..» его переживает (запись «...png» в корне
        // templates/ разбирается обратно в одиночный шаблон с именем «..»). Из бандла ничего не
        // распаковывается на диск, так что выхода за папку тут нет; нода такой шаблон найдёт.
        // Заводить второе правило ради странного, но работающего имени значило бы завести копию,
        // которой предстоит разъехаться.
        await Assert.That(single.IsNameUsable("..")).IsTrue();
    }

    // Список окон едет в диалог СНИМКОМ — тем же, из которого редактор считает бейдж целей.
    // Пустой список не тупик: пустое состояние диалога ведёт к двери «файл с диска».
    [Test]
    public async Task TheDialogGetsTheWindowsTheDaemonReports()
    {
        using var library = new TempLibrary();
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetWindows,
            new[] { new WindowDto(0x140804, "elementclient_64", ["Лучник"]) });
        library.WriteExternally(WithFind());
        var prompt = new FakePrompt((RegionCaptureResult?)null);
        using var vm = new MacroEditorViewModel(
            client, library.Library, null, null, ImmediateUiDispatcher.Instance, null, prompt);
        vm.SelectedMacro = vm.Macros.Single(row => row.Name == "поиск");

        await vm.CaptureRegionAsync(vm.Nodes[0]);

        await Assert.That(prompt.Asked[0].Windows.Select(window => window.Hwnd))
            .IsEquivalentTo(new[] { 0x140804L });
    }

    // Без диалога вырезка ничего не делает и ничего не ломает — тот же исход, что у сохранения
    // без IMacroNameConflictPrompt. Отсутствие шва бывает только на пути дизайнера и в
    // headless-тестах; в собранной панели он есть всегда.
    [Test]
    public async Task WithoutAPrompt_NothingHappens()
    {
        using var library = new TempLibrary();
        using var vm = Open(library, WithFind(), prompt: null);
        var node = (FindElementNodeRowViewModel)vm.Nodes[0];

        await vm.CaptureRegionAsync(node);

        await Assert.That(vm.Templates.Templates).IsEmpty();
        await Assert.That(node.Template).IsEqualTo(string.Empty);
        await Assert.That(vm.ErrorMessage).IsNull();
    }

    // Нода не распознавания — не наше дело: у «Паузы» нет ни шаблона, ни области, и кнопки этой
    // в её блоке параметров нет. Проверка стоит потому, что метод принимает базовый тип строки.
    [Test]
    public async Task ANodeWithoutARegionIsIgnored()
    {
        using var library = new TempLibrary();
        var prompt = new FakePrompt(new RegionCaptureResult(
            "x", Cut, new ScreenRect(0, 0, 10, 10), 100, 100));
        library.WriteExternally(new MacroGraph
        {
            Name = "пауза",
            StartNodeId = Ids.Of("d"),
            Nodes = [new DelayNode { Id = Ids.Of("d"), DisplayName = "delay-1", Ms = 100 }],
        });
        using var vm = new MacroEditorViewModel(
            new FakeIpcClient(), library.Library, null, null, ImmediateUiDispatcher.Instance, null, prompt);
        vm.SelectedMacro = vm.Macros.Single(row => row.Name == "пауза");

        await vm.CaptureRegionAsync(vm.Nodes[0]);

        await Assert.That(prompt.Asked).IsEmpty();
        await Assert.That(vm.Templates.Templates).IsEmpty();
    }
}
