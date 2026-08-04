using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// Браузер шаблонов ОТКРЫТОГО МАКРОСА — бывший режим «Шаблоны», сложившийся внутрь редактора
// (волна F2). Здесь сходятся две половины, и каждая проверяется отдельно:
//
//   · что приехало от демона (GetTemplates — метаданные бандла, GetTemplateImage — байты одного
//     файла, AddMacroTemplate/DeleteMacroTemplate — правка бандла);
//   · что панель посчитала сама по ЖИВОМУ графу (какие ноды называют этот шаблон).
//
// Раздела «НЕТ ФАЙЛА» здесь больше нет: «нода называет шаблон, которого нет» ловит валидатор
// (MacroGraphValidatorTests), и это повышение класса ошибки — статическая проверка вместо
// раздела, куда надо было пойти.
//
// Плюс сама политика передачи картинок: по одной за выделением, с кэшем, без запроса за тем, что
// заведомо не пролезет.
public class TemplatesViewModelTests
{
    private const string Macro1 = "pw-boot";

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

    /// <summary>Собирает браузер и сразу наводит его на макрос — так его и заводит редактор.</summary>
    private static TemplatesViewModel Create(FakeIpcClient client, MacroGraph? graph = null)
    {
        var vm = new TemplatesViewModel(client, ImmediateUiDispatcher.Instance);
        vm.ShowMacro(graph?.Name ?? Macro1, graph ?? Macro(Macro1));
        return vm;
    }

    private static FakeIpcClient Client(TemplateDto[]? files = null) =>
        new FakeIpcClient().Respond(IpcMessageTypes.GetTemplates, files ?? []);

    private static FakeIpcClient WithImages(FakeIpcClient client) =>
        client.Respond(IpcMessageTypes.GetTemplateImage, payload =>
        {
            var request = (GetTemplateImageRequest)payload!;
            return new TemplateImageDto(request.MacroName, request.Set, request.Name, Png);
        });

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
        await Assert.That(vm.CountText).IsEqualTo("3");
    }

    [Test]
    public async Task EmptyBundle_SaysSo()
    {
        using var vm = Create(Client());

        await Assert.That(vm.IsEmpty).IsTrue();
        await Assert.That(vm.Templates).IsEmpty();
        await Assert.That(vm.CountText).IsEqualTo("0");
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
        // Демон отдаёт 0×0, когда заголовок не разобрался. Прятать такую запись нельзя — она
        // лежит в бандле, и матчер на ней споткнётся; вот это и есть та причина, которую надо
        // показать.
        using var vm = Create(Client([new TemplateDto(null, "Сломанный", 0, 0, 12)]));

        await Assert.That(vm.Templates[0].SizeText).IsEqualTo("не PNG");
        await Assert.That(vm.Templates[0].IsDecodable).IsFalse();
    }

    // ---- «какие ноды называют» ---------------------------------------------------------------

    [Test]
    public async Task Rows_KnowWhichNodesNeedThem_WithoutAnyExtraRequest()
    {
        var client = Client([File(null, "ServerSelectButton"), File(null, "Ничей"), File("classes", "Лучник")]);
        using var vm = Create(client, Macro(
            Macro1,
            new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "ServerSelectButton" },
            new RecognizeTagNode { Id = Ids.Of("rec"), DisplayName = "rec", TemplateSet = "classes", Region = Region }));

        var button = vm.Templates.Single(t => t.Name == "ServerSelectButton");
        var archer = vm.Templates.Single(t => t.Name == "Лучник");
        var orphan = vm.Templates.Single(t => t.Name == "Ничей");

        await Assert.That(button.UsageText).IsEqualTo("1 нода");
        // RecognizeTag называет НАБОР целиком, поэтому ссылка достаётся каждому его файлу: иначе
        // «Лучник.png не используется» было бы враньём про шаблон, которым опознают лучника.
        await Assert.That(archer.UsageText).IsEqualTo("1 нода");
        await Assert.That(orphan.IsUnused).IsTrue();
        await Assert.That(orphan.UsageText).IsEqualTo("не используется");

        // И ни одного запроса сверх одного: перечень шаблонов бандла, больше ничего. Библиотеку
        // браузер не спрашивает вовсе — граф ему подаёт редактор.
        await Assert.That(client.Requests.Select(r => r.Type).Distinct())
            .IsEquivalentTo(new[] { IpcMessageTypes.GetTemplates });
    }

    // Имя, набранное в ноде, обязано снять пометку «не используется» НЕМЕДЛЕННО: редактор подаёт
    // живой граф, а не тот, что лежит на диске.
    [Test]
    public async Task ShowMacro_WithANewGraph_RecountsUsage_WithoutRefetchingTheList()
    {
        var client = Client([File(null, "Кнопка")]);
        using var vm = Create(client);
        await Assert.That(vm.Templates[0].IsUnused).IsTrue();
        var before = client.CountOf(IpcMessageTypes.GetTemplates);

        vm.ShowMacro(Macro1, Macro(
            Macro1,
            new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "Кнопка" }));

        await Assert.That(vm.Templates[0].IsUnused).IsFalse();
        await Assert.That(vm.Templates[0].UsageText).IsEqualTo("1 нода");
        await Assert.That(client.CountOf(IpcMessageTypes.GetTemplates)).IsEqualTo(before);
    }

    [Test]
    public async Task ShowMacro_OfAnotherMacro_RefetchesTheList()
    {
        var client = Client([File(null, "A")]);
        using var vm = Create(client);
        var before = client.CountOf(IpcMessageTypes.GetTemplates);

        client.Respond(IpcMessageTypes.GetTemplates, new[] { File(null, "B") });
        vm.ShowMacro("другой", Macro("другой"));

        await Assert.That(client.CountOf(IpcMessageTypes.GetTemplates)).IsEqualTo(before + 1);
        await Assert.That(vm.Templates.Select(t => t.Name)).IsEquivalentTo(new[] { "B" });
    }

    [Test]
    public async Task ShowMacro_OfADraft_ShowsNothingAndAsksNothing()
    {
        // У несохранённого черновика файла нет, а значит, нет и бандла, в который класть шаблон.
        var client = Client([File(null, "A")]);
        using var vm = Create(client);

        vm.ShowMacro(null, null);

        await Assert.That(vm.HasMacro).IsFalse();
        await Assert.That(vm.Templates).IsEmpty();
    }

    [Test]
    public async Task MacrosChangedPush_RereadsTheBundle()
    {
        // Одно и то же событие отзывается и на нашу собственную правку (добавили шаблон), и на
        // чужую (бандл подменили в проводнике).
        var client = Client();
        using var vm = Create(client);
        await Assert.That(vm.Templates).IsEmpty();

        client.Respond(IpcMessageTypes.GetTemplates, new[] { File(null, "Кнопка") });
        client.RaiseEvent(IpcMessageTypes.MacrosChanged);

        await Assert.That(vm.Templates).Count().IsEqualTo(1);
    }

    // ---- правка бандла ----------------------------------------------------------------------

    [Test]
    public async Task Import_SendsTheFileStemAsTheName_AndTheSetFromTheField()
    {
        var client = Client();
        AddMacroTemplateRequest? seen = null;
        client.Respond(IpcMessageTypes.AddMacroTemplate, payload =>
        {
            seen = (AddMacroTemplateRequest)payload!;
            return new[] { File("classes", "Лучник") };
        });
        using var vm = Create(client);
        vm.ImportSet = "classes";

        var ok = await vm.ImportAsync("Лучник.png", Png);

        await Assert.That(ok).IsTrue();
        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.MacroName).IsEqualTo(Macro1);
        await Assert.That(seen.Set).IsEqualTo("classes");
        await Assert.That(seen.Name).IsEqualTo("Лучник");
        // Ответ несёт новый перечень — второго запроса за списком не нужно.
        await Assert.That(vm.Templates.Select(t => t.Name)).IsEquivalentTo(new[] { "Лучник" });
    }

    [Test]
    public async Task Import_WithAnEmptySetField_MeansASingleTemplate()
    {
        var client = Client();
        AddMacroTemplateRequest? seen = null;
        client.Respond(IpcMessageTypes.AddMacroTemplate, payload =>
        {
            seen = (AddMacroTemplateRequest)payload!;
            return Array.Empty<TemplateDto>();
        });
        using var vm = Create(client);

        await vm.ImportAsync("ServerSelectButton.png", Png);

        await Assert.That(seen!.Set).IsNull();
    }

    [Test]
    public async Task Import_OverTheCeiling_IsNotEvenSent()
    {
        var client = Client();
        using var vm = Create(client);

        var ok = await vm.ImportAsync("Огромный.png", new byte[TemplateLimits.MaxImageBytes + 1]);

        await Assert.That(ok).IsFalse();
        await Assert.That(vm.HasImportProblem).IsTrue();
        await Assert.That(vm.ImportProblem).Contains("потолк");
        await Assert.That(client.CountOf(IpcMessageTypes.AddMacroTemplate)).IsEqualTo(0);
    }

    [Test]
    public async Task Import_ReportsARefusal()
    {
        var client = Client();
        client.Fail(IpcMessageTypes.AddMacroTemplate, "бандл не читается");
        using var vm = Create(client);

        var ok = await vm.ImportAsync("A.png", Png);

        await Assert.That(ok).IsFalse();
        await Assert.That(vm.ImportProblem).Contains("бандл не читается");
    }

    [Test]
    public async Task Delete_RemovesTheRow_FromTheAnswer()
    {
        var client = Client([File("classes", "Лучник"), File("classes", "Жрец")]);
        client.Respond(IpcMessageTypes.DeleteMacroTemplate, _ => new[] { File("classes", "Жрец") });
        using var vm = Create(client);

        await vm.DeleteAsync(vm.Templates.Single(t => t.Name == "Лучник"));

        await Assert.That(vm.Templates.Select(t => t.Name)).IsEquivalentTo(new[] { "Жрец" });
    }

    // ---- превью ----------------------------------------------------------------------------

    [Test]
    public async Task Preview_IsFetchedOnlyForTheSelectedTemplate()
    {
        var client = WithImages(Client([File(null, "A"), File(null, "B")]));
        using var vm = Create(client);

        // Открытие макроса картинок не тянет: список — это метаданные, и в этом весь смысл.
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
        var client = WithImages(Client([File(null, "A"), File(null, "B")]));
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
        // существует ровно для этого — и с переездом браузера внутрь редактора устареть успевает
        // не только строка, но и весь макрос, поэтому в эхо вошло и его имя.
        var client = Client([File(null, "A"), File(null, "B")])
            .Respond(IpcMessageTypes.GetTemplateImage, _ => new TemplateImageDto(Macro1, null, "Посторонний", Png));
        using var vm = Create(client);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
    }

    [Test]
    public async Task Preview_IgnoresAnAnswerAboutAnotherMacro()
    {
        var client = Client([File(null, "A")])
            .Respond(IpcMessageTypes.GetTemplateImage, _ => new TemplateImageDto("чужой", null, "A", Png));
        using var vm = Create(client);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
    }

    [Test]
    public async Task Deselecting_ClearsThePreview()
    {
        var client = WithImages(Client([File(null, "A")]));
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

        // Иначе добавление соседнего шаблона выбрасывало бы пользователя из того, который он
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
