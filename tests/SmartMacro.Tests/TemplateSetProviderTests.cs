using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Vision;

namespace SmartMacro.Tests;

// Превращение имён шаблонов, которые несут ноды макросов, в байты PNG — и перечисление того же
// дерева для браузера шаблонов в панели.
//
// Раскладка теперь ровно одна: Assets/templates/{имя}.png для Find/Wait и
// Assets/templates/{набор}/{тег}.png для RecognizeTag. Псевдонима "classes" → GameClassNames
// больше нет, и главное, что здесь закреплено, — ИМЕНА при этом не поменялись: набор
// по-прежнему зовётся "classes", одиночный шаблон по-прежнему зовётся своим стемом.
//
// Байты на пути исполнителя никогда не декодируются, так что содержимого-пустышки достаточно;
// путь браузера читает заголовок PNG, и там нужен настоящий (крошечный) PNG.
public class TemplateSetProviderTests
{
    // 1×1 PNG. Заголовок IHDR настоящий — именно его и читает Catalog().
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static string CreateAssetsRoot(params (string Folder, string Stem, byte[] Bytes)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"smartmacro-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (folder, stem, bytes) in files)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, stem), bytes);
        }

        return root;
    }

    private static TemplateSetProvider Create(string root) =>
        new(root, NullLogger<TemplateSetProvider>.Instance);

    // ---- путь исполнителя ------------------------------------------------------------------

    [Test]
    public async Task ClassesSet_IsAnOrdinarySubfolder_KeyedByStem()
    {
        // Ни строчки особого разрешения за этим именем больше нет — и ровно поэтому графы,
        // ссылающиеся на "classes", продолжают работать после переезда файлов.
        var root = CreateAssetsRoot(
            (@"templates\classes", "Лучник.png", [1, 2, 3]),
            (@"templates\classes", "Жрец.png", [4, 5]));
        try
        {
            var set = Create(root).GetSet(TemplateSetProvider.ClassesSetName);

            await Assert.That(set).Count().IsEqualTo(2);
            await Assert.That(set["Лучник"]).IsEquivalentTo(new byte[] { 1, 2, 3 });
            await Assert.That(set["Жрец"]).IsEquivalentTo(new byte[] { 4, 5 });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task OtherSets_ComeFromTheirOwnSubfolders()
    {
        var root = CreateAssetsRoot((@"templates\bosses", "Дракон.png", [7]));
        try
        {
            var set = Create(root).GetSet("bosses");

            await Assert.That(set).Count().IsEqualTo(1);
            await Assert.That(set["Дракон"]).IsEquivalentTo(new byte[] { 7 });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Sets_IgnoreNonPngFiles_AndAreCaseSensitive()
    {
        var root = CreateAssetsRoot(
            (@"templates\classes", "Boss.png", [1]),
            (@"templates\classes", "readme.txt", [2]),
            (@"templates\classes", "Шаман.jpg", [3]));
        try
        {
            var set = Create(root).GetSet(TemplateSetProvider.ClassesSetName);

            await Assert.That(set).Count().IsEqualTo(1);
            await Assert.That(set.ContainsKey("Boss")).IsTrue();
            await Assert.That(set.ContainsKey("boss")).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task UnknownSet_IsEmpty_NotAnException()
    {
        var root = CreateAssetsRoot();
        try
        {
            // RecognizeTagNode, указывающая на набор, который пользователь ещё не завёл, обязана
            // просто никогда не срабатывать, а не ронять прогон (или загрузку библиотеки).
            await Assert.That(Create(root).GetSet("nope")).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task SingleTemplates_ResolveByStem_FromTheRootOfTheTree()
    {
        var root = CreateAssetsRoot(("templates", "ServerSelectButton.png", [9, 9]));
        try
        {
            var provider = Create(root);

            await Assert.That(provider.TryGetTemplate("ServerSelectButton")).IsEquivalentTo(new byte[] { 9, 9 });
            await Assert.That(provider.TryGetTemplate("serverselectbutton")).IsNull();
            await Assert.That(provider.TryGetTemplate("NoSuchTemplate")).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task SingleTemplates_DoNotSeeIntoSetFolders()
    {
        // Корень и подпапки — два разных пространства имён: Find/Wait по имени тега из набора
        // ничего найти не должны, иначе «шаблон» и «набор» перестали бы быть разными вещами.
        var root = CreateAssetsRoot((@"templates\classes", "Лучник.png", [1]));
        try
        {
            await Assert.That(Create(root).TryGetTemplate("Лучник")).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Templates_AreCachedAfterTheFirstRead()
    {
        var root = CreateAssetsRoot(("templates", "Cached.png", [1]));
        try
        {
            var provider = Create(root);
            var first = provider.TryGetTemplate("Cached");

            // Правка ассетов на ходу исполнителем не поддерживается — шаблоны грузятся один раз
            // на процесс. Браузер, в отличие от него, читает диск каждый раз (см. ниже).
            File.WriteAllBytes(Path.Combine(root, "templates", "Cached.png"), [2, 2, 2]);

            await Assert.That(provider.TryGetTemplate("Cached")).IsEquivalentTo(first!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- путь браузера ---------------------------------------------------------------------

    [Test]
    public async Task Catalog_ListsSinglesFirst_ThenEachSet_WithPixelSizeAndByteSize()
    {
        var root = CreateAssetsRoot(
            ("templates", "ServerSelectButton.png", OnePixelPng),
            (@"templates\classes", "Лучник.png", OnePixelPng),
            (@"templates\bosses", "Дракон.png", OnePixelPng),
            ("templates", "readme.txt", [1]));
        try
        {
            var catalog = Create(root).Catalog();

            await Assert.That(catalog.Select(t => $"{t.Set}/{t.Name}"))
                .IsEquivalentTo(new[] { "/ServerSelectButton", "bosses/Дракон", "classes/Лучник" });
            await Assert.That(catalog[0].Set).IsNull();
            await Assert.That(catalog[0].Width).IsEqualTo(1);
            await Assert.That(catalog[0].Height).IsEqualTo(1);
            await Assert.That(catalog[0].Bytes).IsEqualTo(OnePixelPng.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Catalog_IsEmptyRatherThanThrowing_WhenTheTreeIsMissing()
    {
        var root = CreateAssetsRoot();
        try
        {
            await Assert.That(Create(root).Catalog()).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Catalog_KeepsAFileThatIsNotAPng_ReportingZeroSize()
    {
        // Файл лежит в папке, значит исполнитель его увидит и попробует им матчить. Спрятать
        // такой из списка значило бы спрятать ровно ту причину, по которой нода не срабатывает.
        var root = CreateAssetsRoot(("templates", "Broken.png", [0, 1, 2, 3]));
        try
        {
            var catalog = Create(root).Catalog();

            await Assert.That(catalog).Count().IsEqualTo(1);
            await Assert.That(catalog[0].Width).IsEqualTo(0);
            await Assert.That(catalog[0].Height).IsEqualTo(0);
            await Assert.That(catalog[0].Bytes).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryReadFile_ReadsTheDisk_NotTheExecutorsCache()
    {
        var root = CreateAssetsRoot(("templates", "Live.png", [1]));
        try
        {
            var provider = Create(root);
            provider.TryGetTemplate("Live"); // прогревает кэш исполнителя

            File.WriteAllBytes(Path.Combine(root, "templates", "Live.png"), [2, 2, 2]);

            // Пользователь только что подправил шаблон и открыл превью — он обязан увидеть новый
            // файл, а не тот снимок, что демон сделал на старте.
            await Assert.That(provider.TryReadFile(null, "Live")).IsEquivalentTo(new byte[] { 2, 2, 2 });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryReadFile_FindsSetMembers_AndReturnsNullForWhatIsNotThere()
    {
        var root = CreateAssetsRoot((@"templates\classes", "Жрец.png", [4, 5]));
        try
        {
            var provider = Create(root);

            await Assert.That(provider.TryReadFile("classes", "Жрец")).IsEquivalentTo(new byte[] { 4, 5 });
            await Assert.That(provider.TryReadFile(null, "Жрец")).IsNull();
            await Assert.That(provider.TryReadFile("classes", "Нет")).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryReadFile_RefusesNamesThatCarryAPath()
    {
        // Имя приезжает по проводу. Разделитель пути в нём — это попытка выйти за Assets/, а не
        // шаблон, так что запрос отклоняется целиком.
        var root = CreateAssetsRoot(("templates", "Ok.png", [1]));
        try
        {
            var provider = Create(root);

            await Assert.That(provider.TryReadFile(null, @"..\..\appsettings")).IsNull();
            await Assert.That(provider.TryReadFile(null, "sub/Ok")).IsNull();
            await Assert.That(provider.TryReadFile("..", "Ok")).IsNull();
            await Assert.That(provider.TryReadFile(null, @"C:\Windows\win")).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
