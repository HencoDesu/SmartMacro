using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// W0.2b: библиотека макросов поверх папки — круг CRUD, устойчивость к файлу, испорченному
// руками, проверка имени, а также подавление дребезга и собственных записей у наблюдателя.
//
// Тесты наблюдателя помечены [NotInParallel] и работают на настоящих временных папках:
// FileSystemWatcher — то единственное здесь, что нельзя подделать, не проверяя вместо него
// собственный мок.
public class MacroGraphStoreTests
{
    private static MacroGraph Chain(string name, params MacroNode[] nodes) =>
        new() { Name = name, StartNodeId = nodes[0].Id, Nodes = [.. nodes] };

    private static MacroGraph SimpleMacro(string name, VirtualKey key = VirtualKey.F1) =>
        Chain(name, new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = key, Target = new TargetSelector() });

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MacroGraphStore CreateStore(string baseDirectory) =>
        new(baseDirectory, NullLogger<MacroGraphStore>.Instance);

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
    // навесили обратно: примеры теперь раздаются файлами из examples/ рядом с демоном.
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
            File.WriteAllText(Path.Combine(macrosDir, "мой.json"), MacroGraphJson.Serialize(SimpleMacro("мой")));
            var legacy = Path.Combine(dir, "macros.json");
            File.WriteAllText(legacy, """{"Macros":[{"Name":"старьё","ActionsByClass":{"Лучник":[]}}]}""");

            using var store = CreateStore(dir);

            await Assert.That(store.All.Select(graph => graph.Name).ToList())
                .IsEquivalentTo(new List<string> { "мой" });
            await Assert.That(Directory.EnumerateFiles(macrosDir).Select(Path.GetFileName).ToList())
                .IsEquivalentTo(new List<string?> { "мой.json" });
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
    public async Task Save_WritesOneFilePerGraph_AndRoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            using (var store = CreateStore(dir))
            {
                await store.SaveAsync(SimpleMacro("иммунка", VirtualKey.F8));
                await store.SaveAsync(SimpleMacro("ассист", VirtualKey.F2));

                await Assert.That(store.All).Count().IsEqualTo(2);
                await Assert.That(File.Exists(Path.Combine(dir, "macros", "иммунка.json"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(dir, "macros", "ассист.json"))).IsTrue();
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

    [Test]
    public async Task Load_SkipsBrokenFile_ButKeepsTheRest()
    {
        var dir = CreateTempDir();
        try
        {
            var macrosDir = Path.Combine(dir, "macros");
            Directory.CreateDirectory(macrosDir);
            File.WriteAllText(Path.Combine(macrosDir, "good.json"), MacroGraphJson.Serialize(SimpleMacro("good")));
            File.WriteAllText(Path.Combine(macrosDir, "broken.json"), "{ this is not json ");
            File.WriteAllText(Path.Combine(macrosDir, "unknown-node.json"),
                """{"Name":"unknown-node","StartNodeId":"a","Nodes":[{"$type":"teleport","Id":"a"}]}""");

            using var store = CreateStore(dir);

            await Assert.That(store.All).Count().IsEqualTo(1);
            await Assert.That(store.All[0].Name).IsEqualTo("good");
            await Assert.That(store.TryGet("broken")).IsNull();

            // Пропустить и записать строчку в лог мало: после смены модели ноды НИ ОДИН старый
            // файл больше не разбирается, и пользователь открыл бы панель с пустой библиотекой и
            // без единого следа того, куда делись его макросы. Отодвинутый файл виден в
            // проводнике прямо там же — он и есть объяснение.
            await Assert.That(Directory.EnumerateFiles(macrosDir).Select(Path.GetFileName).Order().ToList())
                .IsEquivalentTo(new List<string?>
                {
                    "broken.json.incompatible",
                    "good.json",
                    "unknown-node.json.incompatible",
                }.Order().ToList());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Load_FileNameWins_OverTheNameFieldInJson()
    {
        var dir = CreateTempDir();
        try
        {
            var macrosDir = Path.Combine(dir, "macros");
            Directory.CreateDirectory(macrosDir);
            // Изображает переименование файла пользователем: основа имени и есть личность макроса.
            File.WriteAllText(Path.Combine(macrosDir, "renamed.json"),
                MacroGraphJson.Serialize(SimpleMacro("old-name")));

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
            await Assert.That(File.Exists(Path.Combine(dir, "macros", "gone.json"))).IsFalse();
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

            var macrosDir = Path.Combine(dir, "macros");
            var path = Path.Combine(macrosDir, "external.json");

            // Кто-то правит папку у нас за спиной (текстовый редактор, git checkout).
            // Всплеск записей обязан схлопнуться в одну-единственную перезагрузку.
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllText(path, MacroGraphJson.Serialize(SimpleMacro("external", VirtualKey.F5)));
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

            File.WriteAllText(
                Path.Combine(dir, "macros", "external.json"),
                MacroGraphJson.Serialize(SimpleMacro("external")));
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
            var macrosDir = Path.Combine(dir, "macros");
            Directory.CreateDirectory(macrosDir);
            var path = Path.Combine(macrosDir, "занят.json");
            File.WriteAllText(path, MacroGraphJson.Serialize(SimpleMacro("занят")));

            // Файл держат открытым эксклюзивно — так выглядит редактор, сохраняющий его прямо
            // сейчас. В отличие от сбоя РАЗБОРА, это состояние временное, и переименовать такой
            // файл было бы прямым вредительством: следующая перезагрузка прочитала бы его.
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var store = CreateStore(dir);
                await Assert.That(store.All).IsEmpty();
            }

            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(File.Exists(path + MacroGraphStore.IncompatibleSuffix)).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
