using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2b: библиотека макросов поверх папки — круг CRUD, устойчивость к испорченному файлу,
// проверка имени, а также подавление дребезга и собственных записей у наблюдателя.
//
// F2: файл на диске стал бандлом .hsm, внутри которого лежит и граф, и собственные шаблоны
// макроса. Отсюда три новые темы: шаблоны переживают сохранение графа, переименование их не
// теряет, а запись атомарна.
//
// Тесты наблюдателя помечены [NotInParallel] и работают на настоящих временных папках:
// FileSystemWatcher — то единственное здесь, что нельзя подделать, не проверяя вместо него
// собственный мок.
public class MacroGraphStoreTests
{
    private static MacroGraph Chain(string name, params MacroNode[] nodes) =>
        new() { Name = name, StartNodeId = nodes[0].Id, Nodes = [.. nodes] };

    private static MacroGraph SimpleMacro(string name, VirtualKey key = VirtualKey.F1) =>
        Chain(name,
            new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = key, Target = new TargetSelector() });

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MacroGraphStore CreateStore(string baseDirectory) =>
        new(baseDirectory, NullLogger<MacroGraphStore>.Instance);

    private static string BundlePath(string dir, string name) =>
        Path.Combine(dir, "macros", name + MacroBundleFormat.Extension);

    /// <summary>Пишет бандл мимо хранилища — так выглядит файл, принесённый со стороны.</summary>
    private static void WriteBundle(string dir, string fileStem, MacroGraph graph, params (string Path, string Bytes)[] templates)
    {
        Directory.CreateDirectory(Path.Combine(dir, "macros"));
        MacroBundleWriter.Write(BundlePath(dir, fileStem), new MacroBundleContent
        {
            Metadata = MacroBundleMetadata.CreateNew(graph.Name),
            Graph = graph,
            Templates = [.. templates.Select(t => new MacroBundleFile(t.Path, Encoding.UTF8.GetBytes(t.Bytes)))],
        });
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Наблюдатель может ещё мгновение держать папку; уборка временных файлов — не то,
            // что здесь проверяется.
        }
    }

    /// <summary>Опрашивает, пока <paramref name="condition"/> не выполнится или не выйдет отпущенное время.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    // Инвариант хранилища: построить объект — значит прочитать папку, и только. Раньше
    // конструктор мигрировал старый macros.json, переименовывал его в *.migrated, сеял шесть
    // примеров pw-* и ставил маркер .examples-seeded — то есть «создать объект» означало
    // «изменить состояние на диске». Тест стоит здесь именно затем, чтобы побочные эффекты не
    // навесили обратно: примеров нет вовсе — ни в коде, ни отдельной раздаточной папкой.
    [Test]
    public async Task Construction_ReadsTheFolder_AndWritesNothingIntoIt()
    {
        var dir = CreateTempDir();
        try
        {
            var macrosDir = Path.Combine(dir, "macros");

            using (var fresh = CreateStore(dir))
            {
                // Папку завести можно — это единственная уступка; класть в неё что-либо нельзя.
                await Assert.That(Directory.Exists(macrosDir)).IsTrue();
                await Assert.That(Directory.EnumerateFileSystemEntries(macrosDir)).IsEmpty();
                await Assert.That(fresh.All).IsEmpty();
            }

            // Второй заход, уже с содержимым: чужой файл рядом не трогается, своих не появляется.
            WriteBundle(dir, "мой", SimpleMacro("мой"));
            var legacy = Path.Combine(dir, "macros.json");
            File.WriteAllText(legacy, """{"Macros":[{"Name":"старьё","ActionsByClass":{"Лучник":[]}}]}""");

            using var store = CreateStore(dir);

            await Assert.That(store.All.Select(graph => graph.Name).ToList())
                .IsEquivalentTo(new List<string> { "мой" });
            await Assert.That(Directory.EnumerateFiles(macrosDir).Select(Path.GetFileName).ToList())
                .IsEquivalentTo(new List<string?> { "мой.hsm" });
            // Унаследованный файл остаётся ровно там, где лежал: ни разбора, ни переименования.
            await Assert.That(File.Exists(legacy)).IsTrue();
            await Assert.That(File.Exists(legacy + ".migrated")).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_WritesOneBundlePerGraph_AndRoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            using (var store = CreateStore(dir))
            {
                await store.SaveAsync(SimpleMacro("иммунка", VirtualKey.F8));
                await store.SaveAsync(SimpleMacro("ассист", VirtualKey.F2));

                await Assert.That(store.All).Count().IsEqualTo(2);
                await Assert.That(File.Exists(BundlePath(dir, "иммунка"))).IsTrue();
                await Assert.That(File.Exists(BundlePath(dir, "ассист"))).IsTrue();
            }

            // Новое хранилище — это настоящая загрузка с диска, а не снимок из памяти.
            using var reloaded = CreateStore(dir);
            await Assert.That(reloaded.All).Count().IsEqualTo(2);
            var macro = reloaded.TryGet("иммунка");
            await Assert.That(macro).IsNotNull();
            await Assert.That(macro!.Nodes).Count().IsEqualTo(1);
            await Assert.That(((KeyPressNode)macro.Nodes[0]).Key).IsEqualTo(VirtualKey.F8);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Бандл — это zip без сжатия, и это видно: содержимое обязано читаться любым просмотрщиком, а
    // git — уметь считать по нему дельту.
    [Test]
    public async Task Save_WritesAStoreOnlyZip_WithBothEntries()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("бандл"));

            using var archive = ZipFile.OpenRead(BundlePath(dir, "бандл"));
            await Assert.That(archive.Entries.Select(e => e.FullName).Order().ToList())
                .IsEquivalentTo(new List<string> { "metadata.json", "nodes.json" }.Order().ToList());
            await Assert.That(archive.Entries.All(e => e.CompressedLength == e.Length)).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Главное свойство бандла: сохранение ГРАФА не имеет права стереть шаблоны, иначе первое же
    // «Сохранить» после импорта картинок обнулило бы работу.
    [Test]
    public async Task Save_KeepsTheTemplatesThatAreAlreadyInTheBundle()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("с-шаблонами"));
            await store.AddTemplateAsync("с-шаблонами", "classes", "Лучник", Encoding.UTF8.GetBytes("archer"));

            await store.SaveAsync(SimpleMacro("с-шаблонами", VirtualKey.F9));

            var entry = store.TryGetEntry("с-шаблонами");
            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.TemplatePaths).IsEquivalentTo(new List<string> { "classes/Лучник.png" });
            await Assert.That(entry.Templates.Has("classes", isSet: true)).IsTrue();
            await Assert.That(((KeyPressNode)entry.Graph.Nodes[0]).Key).IsEqualTo(VirtualKey.F9);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Переименование делается «записать новый → удалить старый», то есть под новым именем бандла
    // ещё нет. Без подсказки renamedFrom шаблоны переименованного макроса просто исчезли бы —
    // молча, ровно тем способом, ради недопущения которого весь формат и заведён.
    [Test]
    public async Task Save_WithRenamedFrom_CarriesTemplatesAndIdentityOver()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("старое-имя"));
            await store.AddTemplateAsync("старое-имя", null, "Кнопка", Encoding.UTF8.GetBytes("png"));
            var id = store.TryGetEntry("старое-имя")!.Metadata.Id;

            await store.SaveAsync(SimpleMacro("новое-имя"), renamedFrom: "старое-имя");
            await store.DeleteAsync("старое-имя");

            var renamed = store.TryGetEntry("новое-имя");
            await Assert.That(renamed).IsNotNull();
            await Assert.That(renamed!.TemplatePaths).IsEquivalentTo(new List<string> { "Кнопка.png" });
            // Guid при переименовании НЕ меняется: переименование — это не дублирование.
            await Assert.That(renamed.Metadata.Id).IsEqualTo(id);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task AddTemplate_ThenDelete_RoundTripsThroughTheBundle()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("правки"));

            var added = await store.AddTemplateAsync("правки", "classes", "Жрец", Encoding.UTF8.GetBytes("priest"));
            await Assert.That(added).IsTrue();
            await Assert.That(store.ReadTemplate("правки", "classes", "Жрец")).IsNotNull();
            await Assert.That(store.TemplateCatalog("правки").Select(t => t.Path))
                .IsEquivalentTo(new List<string> { "classes/Жрец.png" });

            var removed = await store.DeleteTemplateAsync("правки", "classes", "Жрец");
            await Assert.That(removed).IsTrue();
            await Assert.That(store.TryGetEntry("правки")!.TemplatePaths).IsEmpty();

            // Удалить несуществующий — пустая операция, а не ошибка.
            await Assert.That(await store.DeleteTemplateAsync("правки", "classes", "Жрец")).IsFalse();
            // Как и положить шаблон в макрос, которого нет.
            await Assert.That(await store.AddTemplateAsync("нет-такого", null, "X", [1])).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // *.incompatible больше нет, и это решение: читатель бандла различает «повреждён» и «сделан
    // другой версией формата», а отодвинуть в сторону файл БУДУЩЕЙ версии значило бы соврать ровно
    // тем способом, ради недопущения которого поле версии и заведено.
    [Test]
    public async Task Load_SkipsABrokenBundle_ButKeepsTheRestAndLeavesTheFileAlone()
    {
        var dir = CreateTempDir();
        try
        {
            var macrosDir = Path.Combine(dir, "macros");
            WriteBundle(dir, "good", SimpleMacro("good"));
            File.WriteAllText(Path.Combine(macrosDir, "broken.hsm"), "это вообще не zip");

            using var store = CreateStore(dir);

            await Assert.That(store.All).Count().IsEqualTo(1);
            await Assert.That(store.All[0].Name).IsEqualTo("good");
            await Assert.That(store.TryGet("broken")).IsNull();
            await Assert.That(Directory.EnumerateFiles(macrosDir).Select(Path.GetFileName).Order().ToList())
                .IsEquivalentTo(new List<string?> { "broken.hsm", "good.hsm" }.Order().ToList());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Load_FileNameWins_OverTheNameFieldInMetadata()
    {
        var dir = CreateTempDir();
        try
        {
            // Изображает переименование файла пользователем: основа имени и есть личность макроса.
            WriteBundle(dir, "renamed", SimpleMacro("old-name"));

            using var store = CreateStore(dir);

            await Assert.That(store.TryGet("renamed")).IsNotNull();
            await Assert.That(store.TryGet("old-name")).IsNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Delete_RemovesFileAndEntry_UnknownNameIsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("gone"));

            var deleted = await store.DeleteAsync("gone");
            var missing = await store.DeleteAsync("never-existed");

            await Assert.That(deleted).IsTrue();
            await Assert.That(missing).IsFalse();
            await Assert.That(store.All).IsEmpty();
            await Assert.That(File.Exists(BundlePath(dir, "gone"))).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Save_RejectsNamesThatAreNotUsableFileNames()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);

            await Assert.That(async () => await store.SaveAsync(SimpleMacro("bad/name")))
                .Throws<ArgumentException>();
            await Assert.That(store.All).IsEmpty();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    [Arguments("normal-name", true)]
    [Arguments("кириллица тоже", true)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("has/slash", false)]
    [Arguments("has:colon", false)]
    [Arguments("trailing.", false)]
    [Arguments("CON", false)]
    [Arguments("COM1", false)]
    public async Task ValidateName_MatchesNtfsRules(string name, bool expectedValid)
    {
        var error = MacroGraphStore.ValidateName(name);
        await Assert.That(error is null).IsEqualTo(expectedValid);
    }

    // Временный файл атомарной записи назван так, чтобы под фильтр наблюдателя (*.hsm) не попасть
    // ни длинным именем, ни коротким 8.3. Проверяем следствие: после записи в папке ровно один
    // файл, и это бандл.
    [Test]
    public async Task Save_IsAtomic_AndLeavesNoTemporaryFileBehind()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("атомарно"));
            await store.SaveAsync(SimpleMacro("атомарно", VirtualKey.F4));

            await Assert.That(Directory.EnumerateFiles(Path.Combine(dir, "macros")).Select(Path.GetFileName).ToList())
                .IsEquivalentTo(new List<string?> { "атомарно.hsm" });
            await Assert.That(File.Exists(BundlePath(dir, "атомарно") + MacroBundleWriter.TempSuffix)).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Замена переименованием обязана проходить и тогда, когда бандл в этот момент читают:
    // MoveFileEx с MOVEFILE_REPLACE_EXISTING отказывает, если получатель открыт без FileShare.Delete,
    // и это ровно тот отказ, который проявлялся бы изредка и невоспроизводимо.
    [Test]
    public async Task Save_SucceedsWhileTheBundleIsBeingRead()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await store.SaveAsync(SimpleMacro("занят-чтением"));

            var path = BundlePath(dir, "занят-чтением");
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                await Assert.That(async () => await store.SaveAsync(SimpleMacro("занят-чтением", VirtualKey.F7)))
                    .ThrowsNothing();
                await Assert.That(reader.Length).IsGreaterThan(0);
            }

            await Assert.That(((KeyPressNode)store.TryGet("занят-чтением")!.Nodes[0]).Key).IsEqualTo(VirtualKey.F7);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Watcher_SuppressesOurOwnWrites()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            var events = 0;
            store.MacrosChanged += _ => Interlocked.Increment(ref events);

            await store.SaveAsync(SimpleMacro("mine"));

            // SaveAsync поднимает ровно одно событие; событие наблюдателя, вызванное его же
            // собственной записью, обязано быть проглочено. Ждём дольше 300 мс дребезга плюс запас.
            await Task.Delay(900);
            await Assert.That(Volatile.Read(ref events)).IsEqualTo(1);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Watcher_PicksUpExternalEdits_Debounced()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            var events = 0;
            store.MacrosChanged += _ => Interlocked.Increment(ref events);

            // Кто-то правит папку у нас за спиной (проводник, git checkout). Всплеск записей
            // обязан схлопнуться в одну-единственную перезагрузку.
            for (var i = 0; i < 3; i++)
            {
                WriteBundle(dir, "external", SimpleMacro("external", VirtualKey.F5));
                await Task.Delay(30);
            }

            var reloaded = await WaitUntilAsync(() => store.TryGet("external") is not null);

            await Assert.That(reloaded).IsTrue();
            await Assert.That(Volatile.Read(ref events)).IsEqualTo(1);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Dispose_IsIdempotent_EvenAfterTheWatcherArmedAReload()
    {
        // Регрессия (нашлась на живом демоне стадии 2B): хранилище зарегистрировано в DI дважды —
        // само по себе и как IMacroGraphResolver через фабрику, — так что область видимости
        // держит ОДИН экземпляр в своём списке освобождаемых ДВАЖДЫ и при выключении зовёт
        // Dispose два раза. Второй вызов раньше делал Cancel уже освобождённому
        // CancellationTokenSource и утаскивал за собой всё выключение хоста («завершилось
        // неожиданно», ненулевой код возврата).
        //
        // Воспроизводится это только после того, как событие наблюдателя взвело перезагрузку:
        // на нетронутой папке поле пустое, и двойное освобождение молча безвредно, — потому-то
        // всё и оставалось незамеченным, пока в том же сеансе не случалась внешняя правка.
        var dir = CreateTempDir();
        try
        {
            var store = CreateStore(dir);
            var changed = 0;
            store.MacrosChanged += _ => Interlocked.Increment(ref changed);

            WriteBundle(dir, "external", SimpleMacro("external"));
            var armed = await WaitUntilAsync(() => Volatile.Read(ref changed) > 0);
            await Assert.That(armed).IsTrue();

            store.Dispose();
            await Assert.That(store.Dispose).ThrowsNothing();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Load_LeavesAnUnreadableFileAlone_BecauseThatIsTemporary()
    {
        var dir = CreateTempDir();
        try
        {
            WriteBundle(dir, "занят", SimpleMacro("занят"));
            var path = BundlePath(dir, "занят");

            // Файл держат открытым эксклюзивно — так выглядит редактор, сохраняющий его прямо
            // сейчас. Читатель бандла отличает это от порчи (NotAnArchive против Malformed), и
            // трогать файл нельзя: следующая перезагрузка прочитает его как ни в чём не бывало.
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var store = CreateStore(dir);
                await Assert.That(store.All).IsEmpty();
            }

            await Assert.That(File.Exists(path)).IsTrue();
            using var reloaded = CreateStore(dir);
            await Assert.That(reloaded.TryGet("занят")).IsNotNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
