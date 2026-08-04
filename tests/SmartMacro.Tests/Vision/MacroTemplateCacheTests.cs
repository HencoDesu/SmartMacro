using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;
using SmartMacro.Vision;

namespace SmartMacro.Tests.Vision;

// Волна F2: шаблоны переехали внутрь бандла, и «сходить за шаблоном» превратилось из «прочитать
// PNG» в «открыть zip и распаковать запись». Тик сопоставления не имеет права этого делать, а
// прежний кэш на всё время жизни процесса имел названную цену — «пока демон не перезапущен, он
// матчит теми байтами, что попали в кэш первыми». Здесь проверяется, что новый кэш этой цены не
// платит.
//
// Три вещи, ради которых он вообще устроен именно так: ключ — ИМЯ МАКРОСА, наполнение — целым
// бандлом за одно открытие архива, сброс — по MacrosChanged. Плюс главное свойство: сброс не
// имеет права порвать идущий прогон.
public class MacroTemplateCacheTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Root = Path.Combine(Path.GetTempPath(), $"smartmacro-tplcache-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Store = new MacroGraphStore(Root, NullLogger<MacroGraphStore>.Instance);
            Cache = new MacroTemplateCache(Store, NullLogger<MacroTemplateCache>.Instance);
        }

        public string Root { get; }

        public MacroGraphStore Store { get; }

        public MacroTemplateCache Cache { get; }

        public Task SaveAsync(string name) => Store.SaveAsync(new MacroGraph
        {
            Name = name,
            StartNodeId = Ids.Of("n0"),
            Nodes = [new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = VirtualKey.F1, Target = new TargetSelector() }],
        });

        public Task AddAsync(string macro, string? set, string name, string content) =>
            Store.AddTemplateAsync(macro, set, name, Encoding.UTF8.GetBytes(content));

        public void Dispose()
        {
            Cache.Dispose();
            Store.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Text(byte[]? bytes) => bytes is null ? "(нет)" : Encoding.UTF8.GetString(bytes);

    [Test]
    public async Task Source_ResolvesSinglesAndSets_FromTheMacrosOwnBundle()
    {
        using var harness = new Harness();
        await harness.SaveAsync("макрос");
        await harness.AddAsync("макрос", null, "Кнопка", "single");
        await harness.AddAsync("макрос", "classes", "Лучник", "archer");
        await harness.AddAsync("макрос", "classes", "Жрец", "priest");

        var source = harness.Cache.For("макрос");

        await Assert.That(Text(source.TryGetTemplate("Кнопка"))).IsEqualTo("single");
        // Корень и подпапки — разные пространства имён: Find по имени тега из набора ничего не
        // находит, иначе «шаблон» и «набор» перестали бы быть разными вещами.
        await Assert.That(source.TryGetTemplate("Лучник")).IsNull();
        await Assert.That(source.GetSet("classes").Keys.Order().ToList())
            .IsEquivalentTo(new List<string> { "Жрец", "Лучник" }.Order().ToList());
        await Assert.That(source.GetSet("нет-такого")).IsEmpty();
    }

    // Одно и то же имя у двух макросов — два РАЗНЫХ файла. Ровно это и делает разрешение
    // порунным, и ровно это глобальное дерево обеспечить не могло.
    [Test]
    public async Task TheSameNameInTwoMacros_ResolvesToTwoDifferentFiles()
    {
        using var harness = new Harness();
        await harness.SaveAsync("первый");
        await harness.SaveAsync("второй");
        await harness.AddAsync("первый", null, "Кнопка", "из первого");
        await harness.AddAsync("второй", null, "Кнопка", "из второго");

        await Assert.That(Text(harness.Cache.For("первый").TryGetTemplate("Кнопка"))).IsEqualTo("из первого");
        await Assert.That(Text(harness.Cache.For("второй").TryGetTemplate("Кнопка"))).IsEqualTo("из второго");
    }

    [Test]
    public async Task Source_OfAMacroThatIsNotInTheLibrary_ResolvesNothing()
    {
        using var harness = new Harness();

        var source = harness.Cache.For("черновик");

        await Assert.That(source.TryGetTemplate("Что-угодно")).IsNull();
        await Assert.That(source.GetSet("classes")).IsEmpty();
    }

    // Наполнение ЛЕНИВОЕ: пока прогон не спросил шаблон, архив не открывался, и в кэше пусто.
    // Резидентный демон, за которым никто не следит, не должен читать бандлы просто так.
    [Test]
    public async Task Nothing_IsRead_UntilTheFirstTemplateIsAskedFor()
    {
        using var harness = new Harness();
        await harness.SaveAsync("макрос");
        await harness.AddAsync("макрос", null, "Кнопка", "png");

        var source = harness.Cache.For("макрос");
        await Assert.That(harness.Cache.CachedMacros).IsEqualTo(0);

        source.TryGetTemplate("Кнопка");

        await Assert.That(harness.Cache.CachedMacros).IsEqualTo(1);
    }

    // Правка бандла обязана доехать до СЛЕДУЮЩЕГО прогона. Прежний провайдер этого не умел, и
    // цена была названа честно: «пока демон не перезапущен».
    [Test]
    public async Task EditingTheBundle_DropsTheCache_SoTheNextRunSeesTheNewBytes()
    {
        using var harness = new Harness();
        await harness.SaveAsync("макрос");
        await harness.AddAsync("макрос", null, "Кнопка", "старое");
        await Assert.That(Text(harness.Cache.For("макрос").TryGetTemplate("Кнопка"))).IsEqualTo("старое");

        await harness.AddAsync("макрос", null, "Кнопка", "новое");

        await Assert.That(harness.Cache.CachedMacros).IsEqualTo(0);
        await Assert.That(Text(harness.Cache.For("макрос").TryGetTemplate("Кнопка"))).IsEqualTo("новое");
    }

    // …но идущий прогон рвать нельзя: источник держит СНИМОК, а сброс всего лишь подменяет запись
    // в словаре. Иначе макрос, начавший распознавание одним набором, закончил бы его другим.
    [Test]
    public async Task ASourceHandedToARun_KeepsItsSnapshot_AcrossACacheDrop()
    {
        using var harness = new Harness();
        await harness.SaveAsync("макрос");
        await harness.AddAsync("макрос", null, "Кнопка", "старое");

        var running = harness.Cache.For("макрос");
        await Assert.That(Text(running.TryGetTemplate("Кнопка"))).IsEqualTo("старое");

        await harness.AddAsync("макрос", null, "Кнопка", "новое");

        await Assert.That(Text(running.TryGetTemplate("Кнопка"))).IsEqualTo("старое");
        await Assert.That(Text(harness.Cache.For("макрос").TryGetTemplate("Кнопка"))).IsEqualTo("новое");
    }

    // Разбор пути внутри бандла общий на весь проект: то, что видно в браузере, и то, что найдёт
    // исполнитель, — по построению одно множество. Записи, шаблоном не являющиеся, не попадают
    // ни туда, ни туда.
    [Test]
    public async Task EntriesThatAreNotTemplates_AreIgnoredByBothTheSnapshotAndTheInventory()
    {
        using var harness = new Harness();
        await harness.SaveAsync("макрос");
        var path = Path.Combine(harness.Root, "macros", "макрос" + MacroBundleFormat.Extension);
        var content = MacroBundleReader.ReadContent(path)!;
        MacroBundleWriter.Write(path, content with
        {
            Templates =
            [
                new MacroBundleFile("Кнопка.png", Encoding.UTF8.GetBytes("ok")),
                new MacroBundleFile("заметки.txt", Encoding.UTF8.GetBytes("не шаблон")),
                new MacroBundleFile("a/b/глубоко.png", Encoding.UTF8.GetBytes("слишком глубоко")),
            ],
        });

        var snapshot = MacroTemplateSnapshot.FromBundle(MacroBundleReader.ReadAllTemplates(path));
        var inventory = MacroTemplateInventory.FromPaths(MacroBundleReader.Read(path).TemplatePaths);

        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(snapshot.Singles.Keys).IsEquivalentTo(new[] { "Кнопка" });
        await Assert.That(inventory.Singles).IsEquivalentTo(new[] { "Кнопка" });
        await Assert.That(inventory.Sets).IsEmpty();
    }
}
