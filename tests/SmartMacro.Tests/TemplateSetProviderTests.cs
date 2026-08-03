using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Vision;

namespace SmartMacro.Tests;

// W0.2b: превращение имён шаблонов, которые несут ноды макросов, в байты PNG. Наследник
// ClassTemplateLoader, у которого он перенял договорённость «просканировать папку, ключ — основа
// имени файла» (эта основа И ЕСТЬ тег, который навешивает RecognizeTagNode). Байты здесь никогда
// не декодируются, так что содержимого-пустышки достаточно.
public class TemplateSetProviderTests
{
    private static string CreateAssetsRoot(params (string Folder, string Stem, byte[] Bytes)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"smartmacro-assets-{Guid.NewGuid():N}");
        foreach (var (folder, stem, bytes) in files)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, stem), bytes);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    private static TemplateSetProvider Create(string root) =>
        new(root, NullLogger<TemplateSetProvider>.Instance);

    [Test]
    public async Task ClassesSet_MapsOntoTheLegacyGameClassNamesFolder_KeyedByStem()
    {
        var root = CreateAssetsRoot(
            ("GameClassNames", "Лучник.png", [1, 2, 3]),
            ("GameClassNames", "Жрец.png", [4, 5]));
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
    public async Task OtherSets_ComeFromTemplatesSubfolders()
    {
        var root = CreateAssetsRoot(("templates/bosses", "Дракон.png", [7]));
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
            ("GameClassNames", "Boss.png", [1]),
            ("GameClassNames", "readme.txt", [2]),
            ("GameClassNames", "Шаман.jpg", [3]));
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
    public async Task SingleTemplates_ResolveByStem_FromGameUiElements()
    {
        var root = CreateAssetsRoot(("GameUiElements", "ServerSelectButton.png", [9, 9]));
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
    public async Task Templates_AreCachedAfterTheFirstRead()
    {
        var root = CreateAssetsRoot(("GameUiElements", "Cached.png", [1]));
        try
        {
            var provider = Create(root);
            var first = provider.TryGetTemplate("Cached");

            // Правка ассетов на ходу не поддерживается — шаблоны грузятся один раз на процесс.
            File.WriteAllBytes(Path.Combine(root, "GameUiElements", "Cached.png"), [2, 2, 2]);

            await Assert.That(provider.TryGetTemplate("Cached")).IsEquivalentTo(first!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
