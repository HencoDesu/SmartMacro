using System.Text;
using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.Tests.ViewModels;

// Браузер шаблонов ОТКРЫТОГО МАКРОСА — бывший режим «Шаблоны», сложившийся внутрь редактора
// (волна F2). Здесь сходятся две половины, и каждая проверяется отдельно:
//
//   · что лежит в бандле на диске (перечень, байты, правка);
//   · что панель посчитала сама по ЖИВОМУ графу (какие ноды называют этот шаблон).
//
// Волна F3 убрала отсюда демона целиком: четырёх запросов (GetTemplates, GetTemplateImage,
// AddMacroTemplate, DeleteMacroTemplate) больше нет, и у этой view-model нет IIpcClient вовсе —
// её конструктор этого доказательство. Вместе с трубой ушли и два теста, проверявшие гонку
// выделения («ответ пришёл про другую строку»): чтение стало синхронным, и гонки не существует.
//
// Раздела «НЕТ ФАЙЛА» здесь нет с F2: «нода называет шаблон, которого нет» ловит валидатор
// (MacroGraphValidatorTests), и это повышение класса ошибки — статическая проверка вместо
// раздела, куда надо было пойти.
public class TemplatesViewModelTests
{
    private const string Macro1 = "pw-boot";

    private static readonly ScreenRect Region = new(0, 0, 100, 40);

    /// <summary>Мельчайший корректный PNG: сигнатура плюс IHDR 96×18.</summary>
    private static byte[] Png(int width = 96, int height = 18, int padding = 0)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange([0, 0, 0, 13]);
        bytes.AddRange(Encoding.ASCII.GetBytes("IHDR"));
        bytes.AddRange([(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width]);
        bytes.AddRange([(byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height]);
        bytes.AddRange(new byte[padding]);
        return [.. bytes];
    }

    private static MacroGraph Macro(string name, params MacroNode[] nodes) => new()
    {
        Name = name,
        StartNodeId = nodes.Length > 0 ? nodes[0].Id : Ids.Of("n1"),
        Nodes = [.. nodes],
    };

    /// <summary>Собирает браузер и сразу наводит его на макрос — так его и заводит редактор.</summary>
    private static TemplatesViewModel Create(TempLibrary library, MacroGraph? graph = null)
    {
        var vm = new TemplatesViewModel(library.Library, ImmediateUiDispatcher.Instance);
        vm.ShowMacro(graph?.Name ?? Macro1, [graph ?? Macro(Macro1)]);
        return vm;
    }

    /// <summary>Кладёт бандл с готовым набором шаблонов.</summary>
    private static TempLibrary WithTemplates(params (string? Set, string Name, byte[] Bytes)[] files)
    {
        var library = new TempLibrary();
        library.WriteExternally(Macro(Macro1));
        foreach (var (set, name, bytes) in files)
        {
            library.Library.AddTemplate(Macro1, set, name, bytes);
        }

        return library;
    }

    // ---- список --------------------------------------------------------------------------

    [Test]
    public async Task Groups_PutSinglesFirst_ThenEachSet()
    {
        using var library = WithTemplates(
            (null, "ServerSelectButton", Png(210, 44)),
            ("classes", "Лучник", Png()),
            ("classes", "Жрец", Png()));
        using var vm = Create(library);

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
        using var library = WithTemplates();
        using var vm = Create(library);

        await Assert.That(vm.IsEmpty).IsTrue();
        await Assert.That(vm.Templates).IsEmpty();
        await Assert.That(vm.CountText).IsEqualTo("0");
    }

    [Test]
    public async Task Rows_CarryPixelSizeAndByteSize()
    {
        using var library = WithTemplates(
            (null, "Большой", Png(210, 44, padding: 36_000)),
            ("classes", "Мелкий", Png(padding: 430)));
        using var vm = Create(library);

        var big = vm.Templates.Single(t => t.Name == "Большой");
        var small = vm.Templates.Single(t => t.Name == "Мелкий");

        // Размеры берутся из заголовка IHDR — картинка не декодируется вовсе.
        await Assert.That(big.SizeText).IsEqualTo("210×44");
        await Assert.That(big.IsDecodable).IsTrue();
        await Assert.That(big.BytesText).Contains("КБ");
        // Меньше килобайта — в байтах: у шаблонов классов это типичный размер, и «0,5 КБ» про
        // них говорило бы меньше, чем точное число.
        await Assert.That(small.BytesText).Contains("Б");
    }

    [Test]
    public async Task AFileThatIsNotAPng_StaysInTheList_ButSaysWhatIsWrong()
    {
        // Прятать такую запись нельзя — она лежит в бандле, и матчер на ней споткнётся; вот это и
        // есть та причина, которую надо показать.
        using var library = WithTemplates((null, "Сломанный", Encoding.UTF8.GetBytes("не png")));
        using var vm = Create(library);

        await Assert.That(vm.Templates[0].SizeText).IsEqualTo(Strings.Editor_Templates_NotPng);
        await Assert.That(vm.Templates[0].IsDecodable).IsFalse();
    }

    // ---- «какие ноды называют» ---------------------------------------------------------------

    [Test]
    public async Task Rows_KnowWhichNodesNeedThem()
    {
        using var library = WithTemplates(
            (null, "ServerSelectButton", Png()),
            (null, "Ничей", Png()),
            ("classes", "Лучник", Png()));
        using var vm = Create(library, Macro(
            Macro1,
            new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "ServerSelectButton" },
            new RecognizeTagNode
                { Id = Ids.Of("rec"), DisplayName = "rec", TemplateSet = "classes", Region = Region }));

        var button = vm.Templates.Single(t => t.Name == "ServerSelectButton");
        var archer = vm.Templates.Single(t => t.Name == "Лучник");
        var orphan = vm.Templates.Single(t => t.Name == "Ничей");

        await Assert.That(Msg.Arg(button.UsageText, Strings.Editor_Templates_UsedBy_One)).IsEqualTo("1");
        // RecognizeTag называет НАБОР целиком, поэтому ссылка достаётся каждому его файлу: иначе
        // «Лучник.png не используется» было бы враньём про шаблон, которым опознают лучника.
        await Assert.That(Msg.Arg(archer.UsageText, Strings.Editor_Templates_UsedBy_One)).IsEqualTo("1");
        await Assert.That(orphan.IsUnused).IsTrue();
        // «Не используется» — отдельный ключ, а не «0 нод»: ноль в форме счётчика читался бы
        // как ошибка подсчёта.
        await Assert.That(orphan.UsageText).IsEqualTo(Strings.Editor_Templates_Unused);
    }

    // Имя, набранное в ноде, обязано снять пометку «не используется» НЕМЕДЛЕННО: редактор подаёт
    // живой граф, а не тот, что лежит на диске.
    [Test]
    public async Task ShowMacro_WithANewGraph_RecountsUsage()
    {
        using var library = WithTemplates((null, "Кнопка", Png()));
        using var vm = Create(library);
        await Assert.That(vm.Templates[0].IsUnused).IsTrue();

        vm.ShowMacro(Macro1, [
            Macro(
                Macro1,
                new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "Кнопка" })
        ]);

        await Assert.That(vm.Templates[0].IsUnused).IsFalse();
        await Assert.That(Msg.Arg(vm.Templates[0].UsageText, Strings.Editor_Templates_UsedBy_One)).IsEqualTo("1");
    }

    [Test]
    public async Task ShowMacro_OfAnotherMacro_ReadsThatBundleInstead()
    {
        using var library = WithTemplates((null, "A", Png()));
        library.WriteExternally(Macro("другой"));
        library.Library.AddTemplate("другой", null, "B", Png());
        using var vm = Create(library);

        vm.ShowMacro("другой", [Macro("другой")]);

        await Assert.That(vm.Templates.Select(t => t.Name)).IsEquivalentTo(new[] { "B" });
    }

    [Test]
    public async Task ShowMacro_OfADraft_ShowsNothing()
    {
        // У несохранённого черновика файла нет, а значит, нет и бандла, в который класть шаблон.
        using var library = WithTemplates((null, "A", Png()));
        using var vm = Create(library);

        vm.ShowMacro(null, null);

        await Assert.That(vm.HasMacro).IsFalse();
        await Assert.That(vm.Templates).IsEmpty();
        await Assert.That(vm.Inventory).IsNull();
    }

    [Test]
    public async Task LibraryChanged_RereadsTheBundle()
    {
        // Одно и то же событие отзывается и на нашу собственную правку (добавили шаблон), и на
        // чужую (бандл подменили в проводнике).
        using var library = WithTemplates();
        using var vm = Create(library);
        await Assert.That(vm.Templates).IsEmpty();

        library.Library.AddTemplate(Macro1, null, "Кнопка", Png());

        await Assert.That(vm.Templates).Count().IsEqualTo(1);
    }

    // ---- правка бандла ----------------------------------------------------------------------

    [Test]
    public async Task Import_UsesTheFileStemAsTheName_AndTheSetFromTheField()
    {
        using var library = WithTemplates();
        using var vm = Create(library);
        vm.ImportSet = "classes";

        var ok = vm.Import("Лучник.png", Png());

        await Assert.That(ok).IsTrue();
        await Assert.That(vm.Templates.Select(t => t.Name)).IsEquivalentTo(new[] { "Лучник" });
        await Assert.That(vm.Templates[0].Set).IsEqualTo("classes");
        // Байты действительно легли в бандл, а не только в список.
        await Assert.That(library.Library.ReadTemplate(Macro1, "classes", "Лучник")).IsNotNull();
    }

    [Test]
    public async Task Import_WithAnEmptySetField_MeansASingleTemplate()
    {
        using var library = WithTemplates();
        using var vm = Create(library);

        vm.Import("ServerSelectButton.png", Png());

        await Assert.That(vm.Templates[0].Set).IsNull();
    }

    // Потолок на импорт исчез вместе с трубой: он существовал ради того, чтобы многомегабайтная
    // строка base64 не встала перед событиями работающего макроса. Панель пишет файл сама, и
    // отказываться класть в бандл то, что формат прекрасно вмещает, ей больше не за чем.
    [Test]
    public async Task Import_OfALargeFile_IsAcceptedNow()
    {
        using var library = WithTemplates();
        using var vm = Create(library);

        var ok = vm.Import("Огромный.png", Png(padding: (int)TemplatesViewModel.MaxPreviewBytes + 1));

        await Assert.That(ok).IsTrue();
        await Assert.That(vm.HasImportProblem).IsFalse();
    }

    [Test]
    public async Task Import_IntoAMacroWithoutABundle_ReportsIt()
    {
        using var library = new TempLibrary();
        using var vm = Create(library, Macro("несохранённый"));

        var ok = vm.Import("A.png", Png());

        await Assert.That(ok).IsFalse();
        await Assert.That(vm.HasImportProblem).IsTrue();
        // Назван ИМЯ ШАБЛОНА, а не имя файла: под этим именем его и будут искать ноды.
        await Assert.That(Msg.Arg(vm.ImportProblem, Strings.Editor_Templates_AddFailedUnreadable)).IsEqualTo("A");
    }

    [Test]
    public async Task Delete_RemovesTheRow_AndTheFile()
    {
        using var library = WithTemplates(("classes", "Лучник", Png()), ("classes", "Жрец", Png()));
        using var vm = Create(library);

        vm.Delete(vm.Templates.Single(t => t.Name == "Лучник"));

        await Assert.That(vm.Templates.Select(t => t.Name)).IsEquivalentTo(new[] { "Жрец" });
        await Assert.That(library.Library.ReadTemplate(Macro1, "classes", "Лучник")).IsNull();
    }

    // ---- превью ----------------------------------------------------------------------------

    [Test]
    public async Task Preview_IsReadOnlyForTheSelectedTemplate()
    {
        using var library = WithTemplates((null, "A", Png()), (null, "B", Png()));
        using var vm = Create(library);

        // Открытие макроса пикселей не читает: список — это метаданные, и в этом весь смысл.
        await Assert.That(vm.HasPreview).IsFalse();

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.PreviewPng).IsNotNull();
        await Assert.That(vm.Templates[0].IsSelected).IsTrue();
    }

    [Test]
    public async Task Preview_IsCached_SoGoingBackAndForthCostsNothing()
    {
        using var library = WithTemplates((null, "A", Png()), (null, "B", Png()));
        using var vm = Create(library);

        vm.Selected = vm.Templates[0];
        var first = vm.PreviewPng;
        vm.Selected = vm.Templates[1];
        vm.Selected = vm.Templates[0];

        // Тот же самый массив, а не равный ему: кэш отдаёт свою запись, повторное чтение zip не
        // делается.
        await Assert.That(ReferenceEquals(vm.PreviewPng, first)).IsTrue();
    }

    [Test]
    public async Task Preview_IsNotEvenReadForAFileOverTheCeiling()
    {
        // Размер известен из перечня, а панель превью — это картинка размером с ладонь: файл
        // больше мегабайта в ней всё равно не разглядеть, а декодирование держит память Skia.
        using var library =
            WithTemplates((null, "Огромный", Png(padding: (int)TemplatesViewModel.MaxPreviewBytes + 1)));
        using var vm = Create(library);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
        await Assert.That(vm.HasPreviewProblem).IsTrue();
        await Assert.That(Msg.Is(vm.PreviewProblem, Strings.Editor_Templates_PreviewTooBig)).IsTrue();
    }

    [Test]
    public async Task Preview_OfSomethingThatIsNotAPng_SaysSoInsteadOfReading()
    {
        using var library = WithTemplates((null, "Сломанный", Encoding.UTF8.GetBytes("не png")));
        using var vm = Create(library);

        vm.Selected = vm.Templates[0];

        await Assert.That(vm.HasPreview).IsFalse();
        await Assert.That(vm.PreviewProblem).IsNotNull();
    }

    [Test]
    public async Task Preview_ReportsAVanishedFile_RatherThanShowingNothing()
    {
        using var library = WithTemplates((null, "A", Png()));
        using var vm = Create(library);
        var row = vm.Templates[0];

        // Бандл сменился между перечислением и чтением — так выглядит правка из другой панели.
        library.Library.DeleteTemplate(Macro1, null, "A");
        vm.Selected = row;

        await Assert.That(vm.HasPreview).IsFalse();
        await Assert.That(vm.PreviewProblem).IsNotNull();
    }

    [Test]
    public async Task Deselecting_ClearsThePreview()
    {
        using var library = WithTemplates((null, "A", Png()));
        using var vm = Create(library);
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
        using var library = WithTemplates((null, "A", Png()), (null, "B", Png()));
        using var vm = Create(library);
        vm.Selected = vm.Templates[1];

        vm.Refresh();

        // Иначе добавление соседнего шаблона выбрасывало бы пользователя из того, который он
        // разглядывает.
        await Assert.That(vm.Selected).IsNotNull();
        await Assert.That(vm.Selected!.Name).IsEqualTo("B");
    }

    [Test]
    public async Task Refresh_DropsTheSelectionWhenTheFileIsGone()
    {
        using var library = WithTemplates((null, "A", Png()), (null, "B", Png()));
        using var vm = Create(library);
        vm.Selected = vm.Templates[1];

        library.Library.DeleteTemplate(Macro1, null, "B");
        vm.Refresh();

        await Assert.That(vm.Selected).IsNull();
        await Assert.That(vm.Templates).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Inventory_IsWhatTheValidatorChecksAgainst()
    {
        using var library = WithTemplates((null, "Кнопка", Png()), ("classes", "Лучник", Png()));
        using var vm = Create(library);

        var inventory = vm.Inventory;

        await Assert.That(inventory).IsNotNull();
        await Assert.That(inventory!.Has("Кнопка", isSet: false)).IsTrue();
        await Assert.That(inventory.Has("classes", isSet: true)).IsTrue();
        await Assert.That(inventory.Has("Нет-такого", isSet: false)).IsFalse();
    }

    [Test]
    public async Task Dispose_Unsubscribes()
    {
        using var library = WithTemplates();
        var vm = Create(library);
        vm.Dispose();

        library.Library.AddTemplate(Macro1, null, "A", Png());

        await Assert.That(vm.Templates).IsEmpty();
    }
}
