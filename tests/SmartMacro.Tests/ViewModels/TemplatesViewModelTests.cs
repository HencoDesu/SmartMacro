using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// Режим «Шаблоны». Здесь сходятся две половины, и каждая проверяется отдельно:
//
//   · что приехало от демона (GetTemplates — метаданные, GetTemplateImage — байты одного файла);
//   · что панель посчитала сама по библиотеке макросов (кому нужен шаблон и какого шаблона нет).
//
// Плюс сама политика передачи картинок: по одной за выделением, с кэшем, без запроса за тем, что
// заведомо не пролезет.
public class TemplatesViewModelTests
{
    private static readonly ScreenRect Region = new(0, 0, 100, 40);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static TemplateDto File(string? set, string name, long bytes = 400) =>
        new(set, name, 96, 18, bytes);

    private static MacroGraph Macro(string name, params MacroNode[] nodes) => new()
    {
        Name = name,
        StartNodeId = nodes.Length > 0 ? nodes[0].Id : Ids.Of("n1"),
        Nodes = [.. nodes],
    };

    private static TemplatesViewModel Create(FakeIpcClient client) =>
        new(client, ImmediateUiDispatcher.Instance);

    private static FakeIpcClient Client(TemplateDto[]? files = null, MacroGraph[]? macros = null) =>
        new FakeIpcClient()
            .Respond(IpcMessageTypes.GetTemplates, files ?? [])
            .Respond(IpcMessageTypes.GetMacros, macros ?? []);

    // ---- список --------------------------------------------------------------------------

    [Test]
    public async Task Groups_PutSinglesFirst_ThenEachSet()
    {
        using var vm = Create(Client([
            File(null, "ServerSelectButton", 35889),
            File("classes", "Лучник"),
            File("classes", "Жрец"),
        ]));

        await Assert.That(vm.Templates).Count().IsEqualTo(3);
        await Assert.That(vm.Groups.Select(g => $"{g.Title} {g.CountText}"))
            .IsEquivalentTo(new[] { "одиночные 1", "classes 2" });
        await Assert.That(vm.Groups[0].IsSet).IsFalse();
        await Assert.That(vm.Groups[1].IsSet).IsTrue();
        await Assert.That(vm.SummaryText).IsEqualTo("3 файла · наборов: 1");
    }

    [Test]
    public async Task EmptyTree_SaysSo()
    {
        using var vm = Create(Client());

        await Assert.That(vm.IsEmpty).IsTrue();
        await Assert.That(vm.Templates).IsEmpty();
        await Assert.That(vm.SummaryText).IsEqualTo("дерево шаблонов пусто");
    }

    [Test]
    public async Task Rows_CarryPixelSizeAndByteSize()
    {
        using var vm = Create(Client([
            new TemplateDto(null, "Большой", 210, 44, 35889),
            new TemplateDto("classes", "Мелкий", 96, 18, 469),
        ]));

        await Assert.That(vm.Templates[0].SizeText).IsEqualTo("210×44");
        await Assert.That(vm.Templates[0].BytesText).IsEqualTo("35 КБ");
        await Assert.That(vm.Templates[0].IsDecodable).IsTrue();
        // Меньше килобайта — в байтах: у шаблонов классов это типичный размер, и «0,5 КБ» про
        // них говорило бы меньше, чем точное число.
        await Assert.That(vm.Templates[1].BytesText).IsEqualTo("469 Б");
    }

    [Test]
    public async Task AFileThatIsNotAPng_StaysInTheList_ButSaysWhatIsWrong()
    {
        // Демон отдаёт 0×0, когда заголовок не разобрался. Прятать такой файл нельзя — он лежит
        // в папке, и матчер на нём споткнётся; вот это и есть та причина, которую надо показать.
        using var vm = Create(Client([new TemplateDto(null, "Сломанный", 0, 0, 12)]));

        await Assert.That(vm.Templates[0].SizeText).IsEqualTo("не PNG");
        await Assert.That(vm.Templates[0].IsDecodable).IsFalse();
    }

    // ---- «кому нужен» --------------------------------------------------------------------

    [Test]
    public async Task Rows_KnowWhichMacrosNeedThem_WithoutAnyExtraRequest()
    {
        var client = Client(
            [File(null, "ServerSelectButton"), File(null, "Ничей"), File("classes", "Лучник")],
            [
                Macro("pw-boot", new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "ServerSelectButton" }),
                Macro("pw-identify", new RecognizeTagNode { Id = Ids.Of("rec"), DisplayName = "rec", TemplateSet = "classes", Region = Region }),
            ]);
        using var vm = Create(client);

        var button = vm.Templates.Single(t => t.Name == "ServerSelectButton");
        var archer = vm.Templates.Single(t => t.Name == "Лучник");
        var orphan = vm.Templates.Single(t => t.Name == "Ничей");

        await Assert.That(button.UsageText).IsEqualTo("1 макрос");
        // RecognizeTag называет НАБОР целиком, поэтому ссылка достаётся каждому его файлу: иначе
        // «Лучник.png не используется» было бы враньём про шаблон, которым опознают лучника.
        await Assert.That(archer.UsageText).IsEqualTo("1 макрос");
        await Assert.That(orphan.IsUnused).IsTrue();
        await Assert.That(orphan.UsageText).IsEqualTo("не используется");

        // И ни одного запроса сверх двух: список файлов и библиотека, больше ничего.
        await Assert.That(client.Requests.Select(r => r.Type).Distinct())
            .IsEquivalentTo(new[] { IpcMessageTypes.GetTemplates, IpcMessageTypes.GetMacros });
    }

    [Test]
    public async Task ANodeNamingATemplateThatIsNotThere_ShowsUpBeforeAnyoneRunsIt()
    {
        using var vm = Create(Client(
            [File("classes", "Лучник")],
            [
                Macro("pw-boot", new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "КнопкаКоторойНет" }),
                Macro("боссы", new RecognizeTagNode { Id = Ids.Of("rec"), DisplayName = "rec", TemplateSet = "bosses", Region = Region }),
            ]));

        await Assert.That(vm.HasMissing).IsTrue();
        await Assert.That(vm.MissingHeaderText).IsEqualTo("НЕТ ФАЙЛА · 2");
        await Assert.That(vm.Missing.Select(m => $"{m.Kind}:{m.Name}"))
            .IsEquivalentTo(new[] { "набор:bosses", "шаблон:КнопкаКоторойНет" });
        await Assert.That(vm.Missing.Single(m => m.IsSet).UsageText).IsEqualTo("боссы / rec");
    }

    [Test]
    public async Task Missing_IsEmptyWhenEveryNameHasItsFile()
    {
        using var vm = Create(Client(
            [File(null, "ServerSelectButton"), File("classes", "Лучник")],
            [
                Macro("pw-boot", new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "ServerSelectButton" }),
                Macro("pw-identify", new RecognizeTagNode { Id = Ids.Of("rec"), DisplayName = "rec", TemplateSet = "classes", Region = Region }),
            ]));

        await Assert.That(vm.HasMissing).IsFalse();
        await Assert.That(vm.Missing).IsEmpty();
    }

    [Test]
    public async Task MacrosChangedPush_RecountsUsage_WithoutTheUserDoingAnything()
    {
        var client = Client([File(null, "Кнопка")]);
        using var vm = Create(client);
        await Assert.That(vm.Templates[0].IsUnused).IsTrue();

        client.Respond(
            IpcMessageTypes.GetMacros,
            new[] { Macro("новый", new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "Кнопка" }) });
        client.RaiseEvent(IpcMessageTypes.MacrosChanged);

        await Assert.That(vm.Templates[0].IsUnused).IsFalse();
        await Assert.That(vm.Templates[0].UsageText).IsEqualTo("1 макрос");
    }

    // ---- превью ----------------------------------------------------------------------------

    [Test]
    public async Task Preview_IsFetchedOnlyForTheSelectedTemplate()
    {
        var client = Client([File(null, "A"), File(null, "B")])
            .Respond(IpcMessageTypes.GetTemplateImage, payload =>
            {
                var request = (GetTemplateImageRequest)payload!;
                return new TemplateImageDto(request.Set, request.Name, Png);
            });
        using var vm = Create(client);

        // Открытие режима картинок не тянет: список — это метаданные, и в этом весь смысл.
        await Assert.That(client.CountOf(IpcMessageTypes.GetTemplateImage)).IsEqualTo(0);
        await Assert.That(vm.HasPreview).IsFalse();

        vm.Selected = vm.Templates[0];

        await Assert.That(client.CountOf(IpcMessageTypes.GetTemplateImage)).IsEqualTo(1);
        await Assert.That(vm.PreviewPng).IsEquivalentTo(Png);
        await Assert.That(vm.Templates[0].IsSelected).IsTrue();
    }

    [Test]
    public async Task Preview_IsCached_SoGoingBackAndForthCostsNothing()
    {
        var client = Client([File(null, "A"), File(null, "B")])
            .Respond(IpcMessageTypes.GetTemplateImage, payload =>
            {
                var request = (GetTemplateImageRequest)payload!;
                return new TemplateImageDto(request.Set, request.Name, Png);
            });
        using var vm = Create(client);

        vm.Selected = vm.Templates[0];
        vm.Selected = vm.Templates[1];
        vm.Selected = vm.Templates[0];

        await Assert.That(client.CountOf(IpcMessageTypes.GetTemplateImage)).IsEqualTo(2);
        await Assert.That(vm.HasPreview).IsTrue();
    }

    [Test]
    public async Task Preview_IsNotEvenAskedFor_WhenTheFileIsOverTheCeiling()
    {
        // Размер известен из списка, а демон отвергнет по тому же порогу, — так что round trip
        // ради отказа не делается вовсе.
        var client = Client([File(null, "Огромный", TemplateLimits.MaxImageBytes + 1)]);
        using var vm = Create(client);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
        await Assert.That(vm.HasPreviewProblem).IsTrue();
        await Assert.That(vm.PreviewProblem).Contains("потолк");
        await Assert.That(client.CountOf(IpcMessageTypes.GetTemplateImage)).IsEqualTo(0);
    }

    [Test]
    public async Task Preview_OfSomethingThatIsNotAPng_SaysSoInsteadOfAsking()
    {
        var client = Client([new TemplateDto(null, "Сломанный", 0, 0, 12)]);
        using var vm = Create(client);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
        await Assert.That(vm.PreviewProblem).IsNotNull();
        await Assert.That(client.CountOf(IpcMessageTypes.GetTemplateImage)).IsEqualTo(0);
    }

    [Test]
    public async Task Preview_ReportsARefusalRatherThanShowingNothing()
    {
        var client = Client([File(null, "A")]);
        client.Fail(IpcMessageTypes.GetTemplateImage, "файл исчез");
        using var vm = Create(client);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
        await Assert.That(vm.PreviewProblem).Contains("файл исчез");
    }

    [Test]
    public async Task Preview_IgnoresAnAnswerAboutSomethingElse()
    {
        // Гонка выделения: демон отвечает не про ту строку, что подсвечена сейчас. Эхо в ответе
        // существует ровно для этого.
        var client = Client([File(null, "A"), File(null, "B")])
            .Respond(IpcMessageTypes.GetTemplateImage, _ => new TemplateImageDto(null, "Посторонний", Png));
        using var vm = Create(client);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
    }

    [Test]
    public async Task Deselecting_ClearsThePreview()
    {
        var client = Client([File(null, "A")])
            .Respond(IpcMessageTypes.GetTemplateImage, _ => new TemplateImageDto(null, "A", Png));
        using var vm = Create(client);
        vm.Selected = vm.Templates[0];
        await Assert.That(vm.HasPreview).IsTrue();

        vm.Selected = null;

        await Assert.That(vm.HasSelection).IsFalse();
        await Assert.That(vm.HasPreview).IsFalse();
    }

    // ---- обновление -------------------------------------------------------------------------

    [Test]
    public async Task Refresh_KeepsTheSelectionWhenTheFileIsStillThere()
    {
        var client = Client([File(null, "A"), File(null, "B")]);
        using var vm = Create(client);
        vm.Selected = vm.Templates[1];

        await vm.RefreshAsync();

        // Иначе кнопка «Обновить» выбрасывала бы пользователя из того шаблона, который он
        // разглядывает.
        await Assert.That(vm.Selected).IsNotNull();
        await Assert.That(vm.Selected!.Name).IsEqualTo("B");
    }

    [Test]
    public async Task Refresh_DropsTheSelectionWhenTheFileIsGone()
    {
        var client = Client([File(null, "A"), File(null, "B")]);
        using var vm = Create(client);
        vm.Selected = vm.Templates[1];

        client.Respond(IpcMessageTypes.GetTemplates, new[] { File(null, "A") });
        await vm.RefreshAsync();

        await Assert.That(vm.Selected).IsNull();
        await Assert.That(vm.Templates).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Reconnect_ReseedsEverything()
    {
        // Сервер выбрасывает клиента, переставшего вычерпывать события, поэтому переподключение —
        // это обычное дело, и всё, что пришло за время разрыва, потеряно.
        var client = Client();
        using var vm = Create(client);
        await Assert.That(vm.Templates).IsEmpty();

        client.Respond(IpcMessageTypes.GetTemplates, new[] { File(null, "A") });
        client.RaiseConnected();

        await Assert.That(vm.Templates).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Dispose_Unsubscribes()
    {
        var client = Client();
        var vm = Create(client);
        vm.Dispose();

        client.Respond(IpcMessageTypes.GetTemplates, new[] { File(null, "A") });
        client.RaiseConnected();

        await Assert.That(vm.Templates).IsEmpty();
    }
}
