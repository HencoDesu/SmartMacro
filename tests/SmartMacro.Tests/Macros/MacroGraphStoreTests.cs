using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// Хранилище демона — с волны F3 ТОЛЬКО ЧИТАТЕЛЬ. Всё, что про запись, переехало в
// MacroBundleFolderTests: пишет теперь панель, а реализация общая и лежит в Shared.
//
// Осталось здесь три темы, и все три — про демона:
//   * загрузка не падает на испорченном файле и не трогает его;
//   * ВАЛИДАЦИЯ ПРИ ЗАГРУЗКЕ и Armed — без них «оно в библиотеке ⇒ демон его принял» сменилось бы
//     на «кто-то положил туда файл», а симптомом был бы молчащий хоткей;
//   * наблюдатель — единственный способ демона узнать о новом макросе, ведь панель ему об этом не
//     сообщает.
//
// Подавления собственных записей больше нет и тест на него удалён вместе с механизмом: демон не
// пишет, глушить нечего.
//
// Тесты наблюдателя помечены [NotInParallel] и работают на настоящих временных папках:
// FileSystemWatcher — то единственное здесь, что нельзя подделать, не проверяя вместо него
// собственный мок.
public class MacroGraphStoreTests
{
    private static MacroGraph SimpleMacro(string name, VirtualKey key = VirtualKey.F1) => new()
    {
        Name = name,
        StartNodeId = Ids.Of("n0"),
        Nodes = [new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = key, Target = new TargetSelector() }],
    };

    /// <summary>Граф с хоткеем, но со СЛОМАННЫМ стартом: валидатор обязан назвать это ошибкой.</summary>
    private static MacroGraph BrokenMacro(string name) => new()
    {
        Name = name,
        Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F9)],
        StartNodeId = Ids.Of("нет-такой-ноды"),
        Nodes = [new DelayNode { Id = Ids.Of("d0"), DisplayName = "d0", Ms = 10 }],
    };

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MacroGraphStore CreateStore(string baseDirectory) =>
        new(baseDirectory, NullLogger<MacroGraphStore>.Instance);

    private static string MacrosDir(string dir) => Path.Combine(dir, "macros");

    private static string BundlePath(string dir, string name) =>
        Path.Combine(MacrosDir(dir), name + MacroBundleFormat.Extension);

    /// <summary>Пишет бандл мимо хранилища — так выглядит файл, положенный туда панелью.</summary>
    private static void WriteBundle(string dir, string fileStem, MacroGraph graph,
        params (string Path, string Bytes)[] templates)
    {
        Directory.CreateDirectory(MacrosDir(dir));
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
    // «изменить состояние на диске». С волны F3 инвариант распространился на весь класс: демон не
    // пишет вообще ничего.
    [Test]
    public async Task Construction_ReadsTheFolder_AndWritesNothingIntoIt()
    {
        var dir = CreateTempDir();
        try
        {
            using (var fresh = CreateStore(dir))
            {
                // Папку завести можно — это единственная уступка; класть в неё что-либо нельзя.
                await Assert.That(Directory.Exists(MacrosDir(dir))).IsTrue();
                await Assert.That(Directory.EnumerateFileSystemEntries(MacrosDir(dir))).IsEmpty();
                await Assert.That(fresh.All).IsEmpty();
            }

            // Второй заход, уже с содержимым: чужой файл рядом не трогается, своих не появляется.
            WriteBundle(dir, "мой", SimpleMacro("мой"));
            var legacy = Path.Combine(dir, "macros.json");
            File.WriteAllText(legacy, """{"Macros":[{"Name":"старьё","ActionsByClass":{"Лучник":[]}}]}""");

            using var store = CreateStore(dir);

            await Assert.That(store.All.Select(graph => graph.Name).ToList())
                .IsEquivalentTo(new List<string> { "мой" });
            await Assert.That(Directory.EnumerateFiles(MacrosDir(dir)).Select(Path.GetFileName).ToList())
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
    public async Task Load_ReadsBundlesWrittenByThePanel_TemplatesIncluded()
    {
        var dir = CreateTempDir();
        try
        {
            WriteBundle(dir, "с-шаблонами", SimpleMacro("с-шаблонами", VirtualKey.F8),
                ("classes/Лучник.png", "archer"));

            using var store = CreateStore(dir);

            var entry = store.TryGetEntry("с-шаблонами");
            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.TemplatePaths).IsEquivalentTo(new List<string> { "classes/Лучник.png" });
            await Assert.That(entry.Templates.Has("classes", isSet: true)).IsTrue();
            await Assert.That(((KeyPressNode)entry.Graph.Nodes[0]).Key).IsEqualTo(VirtualKey.F8);
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
            WriteBundle(dir, "good", SimpleMacro("good"));
            File.WriteAllText(Path.Combine(MacrosDir(dir), "broken.hsm"), "это вообще не zip");

            using var store = CreateStore(dir);

            await Assert.That(store.All).Count().IsEqualTo(1);
            await Assert.That(store.All[0].Name).IsEqualTo("good");
            await Assert.That(store.TryGet("broken")).IsNull();
            await Assert.That(Directory.EnumerateFiles(MacrosDir(dir)).Select(Path.GetFileName).Order().ToList())
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

    // F3: гарантия «оно в библиотеке ⇒ демон его принял» пропала вместе с SaveMacro. Теперь судит
    // сам демон при загрузке — тем же валидатором из Shared, каким судит панель, — и невалидный
    // макрос в Armed не попадает. Без этого «хоткей нажимается, ничего не происходит» вернулось бы
    // с другой стороны.
    [Test]
    public async Task Load_ValidatesEveryBundle_AndKeepsBrokenGraphsOutOfArmed()
    {
        var dir = CreateTempDir();
        try
        {
            WriteBundle(dir, "здоровый", SimpleMacro("здоровый"));
            WriteBundle(dir, "битый", BrokenMacro("битый"));

            using var store = CreateStore(dir);

            // В библиотеке он есть — файл существует, и панель обязана его показать.
            await Assert.That(store.All.Select(m => m.Name).Order().ToList())
                .IsEquivalentTo(new List<string> { "битый", "здоровый" }.Order().ToList());
            // А вооружать его триггеры нечем.
            await Assert.That(store.Armed.Select(m => m.Name).ToList())
                .IsEquivalentTo(new List<string> { "здоровый" });

            var broken = store.TryGetEntry("битый");
            await Assert.That(broken!.HasErrors).IsTrue();
            await Assert.That(broken.FirstError).IsNotNull();
            await Assert.That(store.TryGetEntry("здоровый")!.HasErrors).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Предупреждение — не ошибка: макрос с ним исполняется и хоткей у него вооружён. Разница
    // важна, потому что «нода называет шаблон, которого в бандле нет» и «до ноды не добраться» —
    // это именно предупреждения, а блокировать ими запуск значило бы запретить нормальный порядок
    // работы («сначала набрать имя, потом положить файл»).
    [Test]
    public async Task Load_ArmsMacrosThatOnlyHaveWarnings()
    {
        var dir = CreateTempDir();
        try
        {
            var graph = new MacroGraph
            {
                Name = "с-предупреждением",
                Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F10)],
                StartNodeId = Ids.Of("n0"),
                Nodes =
                [
                    new KeyPressNode
                    {
                        Id = Ids.Of("n0"),
                        DisplayName = "key-1",
                        Key = VirtualKey.F1,
                        Target = new TargetSelector(),
                    },
                    // Сюда ничто не ведёт → недостижима → предупреждение, а не ошибка.
                    new DelayNode { Id = Ids.Of("orphan"), DisplayName = "orphan", Ms = 100 },
                ],
            };
            WriteBundle(dir, "с-предупреждением", graph);

            using var store = CreateStore(dir);

            var entry = store.TryGetEntry("с-предупреждением");
            await Assert.That(entry!.Issues).IsNotEmpty();
            await Assert.That(entry.HasErrors).IsFalse();
            await Assert.That(store.Armed.Select(m => m.Name).ToList())
                .IsEquivalentTo(new List<string> { "с-предупреждением" });
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // F3 завела гонку: панель пишет файл, а демон узнаёт о нём наблюдателем с гашением дребезга.
    // «Сохранить», а сразу следом «Запустить» попадают в промежуток, где макроса в снимке ещё нет,
    // — Refresh закрывает его, не дожидаясь наблюдателя.
    [Test]
    public async Task Refresh_PicksUpAJustWrittenBundle_WithoutWaitingForTheWatcher()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = CreateStore(dir);
            await Assert.That(store.TryGet("только-что")).IsNull();

            WriteBundle(dir, "только-что", SimpleMacro("только-что"));

            await Assert.That(store.Refresh()).IsTrue();
            await Assert.That(store.TryGet("только-что")).IsNotNull();
            // Состав не изменился — событие не поднимаем: перерегистрация хоткеев и сброс кэша
            // шаблонов не должны быть платой за промах мимо снимка.
            await Assert.That(store.Refresh()).IsFalse();
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
            store.MacrosChanged += () => Interlocked.Increment(ref events);

            // Так выглядит запись панели (а равно проводника или git checkout): всплеск записей
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
        // на нетронутой папке поле пустое, и двойное освобождение молча безвредно.
        var dir = CreateTempDir();
        try
        {
            var store = CreateStore(dir);
            var changed = 0;
            store.MacrosChanged += () => Interlocked.Increment(ref changed);

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
            // сейчас. Читатель бандла отличает это от порчи, и трогать файл нельзя: следующая
            // перезагрузка прочитает его как ни в чём не бывало.
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
