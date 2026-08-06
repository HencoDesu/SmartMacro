using System.IO.Compression;
using System.Text;
using SmartMacro.Io;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// F3: папка macros/ как библиотека — общий код чтения и записи, которым пользуются ОБА процесса.
//
// Сюда переехали все проверки записи, раньше стоявшие на MacroGraphStoreTests: с волны F3
// хранилище демона только читает, а пишет панель. Раз реализация одна, то и тесты у неё одни —
// иначе через полгода у двух сторон разъедутся не только реализации, но и представления о том,
// что считается правильным.
public class MacroBundleFolderTests
{
    private static MacroGraph SimpleMacro(string name, VirtualKey key = VirtualKey.F1) => new()
    {
        Name = name,
        StartNodeId = Ids.Of("n0"),
        Nodes = [new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = key, Target = new TargetSelector() }],
    };

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Уборка временных файлов — не то, что здесь проверяется.
        }
    }

    [Test]
    public async Task Save_WritesOneBundlePerGraph_AndRoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("иммунка", VirtualKey.F8));
            MacroBundleFolder.Save(dir, SimpleMacro("ассист", VirtualKey.F2));

            var entries = MacroBundleFolder.Read(dir);
            await Assert.That(entries).Count().IsEqualTo(2);
            await Assert.That(entries.Select(e => e.Name).ToList())
                .IsEquivalentTo(new List<string> { "ассист", "иммунка" });

            var immune = entries.Single(e => e.Name == "иммунка");
            await Assert.That(((KeyPressNode)immune.Graph!.Nodes[0]).Key).IsEqualTo(VirtualKey.F8);
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
            MacroBundleFolder.Save(dir, SimpleMacro("бандл"));

            using var archive = ZipFile.OpenRead(MacroBundleFolder.PathFor(dir, "бандл"));
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
            MacroBundleFolder.Save(dir, SimpleMacro("с-шаблонами"));
            MacroBundleFolder.EditTemplates(dir, "с-шаблонами",
                templates => [.. templates, new MacroBundleFile("classes/Лучник.png", Encoding.UTF8.GetBytes("archer"))]);

            MacroBundleFolder.Save(dir, SimpleMacro("с-шаблонами", VirtualKey.F9));

            var entry = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "с-шаблонами"));
            await Assert.That(entry.TemplatePaths).IsEquivalentTo(new List<string> { "classes/Лучник.png" });
            await Assert.That(entry.Templates.Has("classes", isSet: true)).IsTrue();
            await Assert.That(((KeyPressNode)entry.Graph!.Nodes[0]).Key).IsEqualTo(VirtualKey.F9);
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
            MacroBundleFolder.Save(dir, SimpleMacro("старое-имя"));
            MacroBundleFolder.EditTemplates(dir, "старое-имя",
                templates => [.. templates, new MacroBundleFile("Кнопка.png", Encoding.UTF8.GetBytes("png"))]);
            var id = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "старое-имя")).Metadata!.Id;

            MacroBundleFolder.Save(dir, SimpleMacro("новое-имя"), renamedFrom: "старое-имя");
            MacroBundleFolder.Delete(dir, "старое-имя");

            var renamed = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "новое-имя"));
            await Assert.That(renamed.TemplatePaths).IsEquivalentTo(new List<string> { "Кнопка.png" });
            // Guid при переименовании НЕ меняется: переименование — это не дублирование.
            await Assert.That(renamed.Metadata!.Id).IsEqualTo(id);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task EditTemplates_AddThenRemove_RoundTripsThroughTheBundle()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("правки"));

            var added = MacroBundleFolder.EditTemplates(dir, "правки",
                templates => [.. templates, new MacroBundleFile("classes/Жрец.png", Encoding.UTF8.GetBytes("priest"))]);
            await Assert.That(added).IsTrue();

            var path = MacroBundleFolder.PathFor(dir, "правки");
            await Assert.That(MacroBundleReader.ReadTemplate(path, "classes/Жрец.png")).IsNotNull();

            var removed = MacroBundleFolder.EditTemplates(dir, "правки",
                templates => templates.Where(f => f.Path != "classes/Жрец.png").ToList());
            await Assert.That(removed).IsTrue();
            await Assert.That(MacroBundleFolder.ReadEntry(path).TemplatePaths).IsEmpty();

            // Правка бандла, которого нет, — false, а не исключение.
            await Assert.That(MacroBundleFolder.EditTemplates(dir, "нет-такого", t => [.. t])).IsFalse();
            // Как и правка, которая ничего не меняет: null от редактора означает «не переписывай».
            await Assert.That(MacroBundleFolder.EditTemplates(dir, "правки", _ => null)).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ---- занятое имя ----------------------------------------------------------------------
    //
    // Личность макроса — имя его файла, поэтому «записать pw-login в pw-login.hsm» и
    // «уничтожить чужой макрос» папка различить не может: об этом ей говорит вызывающий.

    // Дефект, ради которого MacroSaveTarget и заведён: переименовать «pw-buff» в занятое
    // «pw-login» значило потерять оба макроса разом. Отказ обязан быть ПОЛНЫМ — чужой файл не
    // тронут ни байтом.
    [Test]
    public async Task Save_RefusesToWriteOverSomeoneElsesBundle()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("pw-login", VirtualKey.F1));
            MacroBundleFolder.EditTemplates(dir, "pw-login",
                templates => [.. templates, new MacroBundleFile("classes/Лучник.png", Encoding.UTF8.GetBytes("archer"))]);
            var before = File.ReadAllBytes(MacroBundleFolder.PathFor(dir, "pw-login"));

            await Assert.That(() => MacroBundleFolder.Save(
                    dir,
                    SimpleMacro("pw-login", VirtualKey.F9),
                    submacros: null,
                    renamedFrom: "pw-buff",
                    target: MacroSaveTarget.Foreign))
                .Throws<MacroNameTakenException>();

            await Assert.That(File.ReadAllBytes(MacroBundleFolder.PathFor(dir, "pw-login")))
                .IsEquivalentTo(before);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Вторая половина того же дефекта, и увидеть её можно только по СОДЕРЖИМОМУ получившегося
    // файла: вложения обязаны переехать из СВОЕГО бандла, а не из затираемого. Раньше previous
    // читался сперва по НОВОМУ пути, и в файле оказывался граф от одного макроса с шаблонами и
    // паспортом от другого.
    [Test]
    public async Task Save_ReplacingAnotherMacro_CarriesItsOwnTemplates_NotTheVictims()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("pw-buff"));
            MacroBundleFolder.EditTemplates(dir, "pw-buff",
                templates => [.. templates, new MacroBundleFile("Бафф.png", Encoding.UTF8.GetBytes("buff"))]);
            var buffId = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "pw-buff")).Metadata!.Id;

            MacroBundleFolder.Save(dir, SimpleMacro("pw-login"));
            MacroBundleFolder.EditTemplates(dir, "pw-login",
                templates => [.. templates, new MacroBundleFile("Вход.png", Encoding.UTF8.GetBytes("login"))]);

            // Пользователь согласился заменить pw-login тем, что было pw-buff.
            MacroBundleFolder.Save(
                dir,
                SimpleMacro("pw-login", VirtualKey.F9),
                submacros: null,
                renamedFrom: "pw-buff",
                target: MacroSaveTarget.ForeignReplace);

            var entry = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "pw-login"));
            await Assert.That(entry.TemplatePaths).IsEquivalentTo(new List<string> { "Бафф.png" });
            await Assert.That(entry.Metadata!.Id).IsEqualTo(buffId);
            await Assert.That(((KeyPressNode)entry.Graph!.Nodes[0]).Key).IsEqualTo(VirtualKey.F9);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Черновик, сохранённый под чужим именем с согласия пользователя, наследовать НЕ от чего:
    // своего бандла у него нет. Замена и означает, что того макроса больше нет.
    [Test]
    public async Task Save_ReplacingAnotherMacro_WithoutABundleOfItsOwn_WritesAFreshOne()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("pw-login"));
            MacroBundleFolder.EditTemplates(dir, "pw-login",
                templates => [.. templates, new MacroBundleFile("Вход.png", Encoding.UTF8.GetBytes("login"))]);
            var victimId = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "pw-login")).Metadata!.Id;

            MacroBundleFolder.Save(
                dir,
                SimpleMacro("pw-login", VirtualKey.F9),
                target: MacroSaveTarget.ForeignReplace);

            var entry = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "pw-login"));
            await Assert.That(entry.TemplatePaths).IsEmpty();
            await Assert.That(entry.Metadata!.Id).IsNotEqualTo(victimId);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ---- нечитаемый СВОЙ бандл ---------------------------------------------------------------

    // Дефект 2 целиком: «файла нет» и «файл не читается» сливались в один null, и запись шла
    // дальше с пустым набором шаблонов и новым Guid. Бандл БУДУЩЕЙ версии формата достижим без
    // всяких гонок — он лежит в библиотеке намеренно, — а внутри у него шаблоны, вырезанные из
    // игры руками.
    [Test]
    public async Task Save_RefusesWhenItsOwnBundleCannotBeRead()
    {
        var dir = CreateTempDir();
        try
        {
            var path = MacroBundleFolder.PathFor(dir, "из-будущего");
            WriteFutureBundle(path);
            var before = File.ReadAllBytes(path);

            await Assert.That(() => MacroBundleFolder.Save(dir, SimpleMacro("из-будущего")))
                .Throws<MacroBundleUnreadableException>();

            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Тот же отказ на переименовании: наследовать нечего, а старый файл редактор потом удалит —
    // то есть молчаливая запись уничтожила бы вложения окончательно.
    [Test]
    public async Task Save_RefusesWhenTheBundleItRenamesFromCannotBeRead()
    {
        var dir = CreateTempDir();
        try
        {
            WriteFutureBundle(MacroBundleFolder.PathFor(dir, "из-будущего"));

            await Assert.That(() => MacroBundleFolder.Save(
                    dir,
                    SimpleMacro("новое-имя"),
                    submacros: null,
                    renamedFrom: "из-будущего",
                    target: MacroSaveTarget.Foreign))
                .Throws<MacroBundleUnreadableException>();

            await Assert.That(File.Exists(MacroBundleFolder.PathFor(dir, "новое-имя"))).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Правка шаблонов идёт тем же циклом «прочитать всё → поменять одно → записать всё», значит и
    // отказывать обязана так же. Раньше это возвращалось тем же false, что и «такого макроса
    // нет», — двум разным бедам доставалось одно невнятное объяснение.
    [Test]
    public async Task EditTemplates_RefusesWhenTheBundleCannotBeRead()
    {
        var dir = CreateTempDir();
        try
        {
            var path = MacroBundleFolder.PathFor(dir, "из-будущего");
            WriteFutureBundle(path);
            var before = File.ReadAllBytes(path);

            await Assert.That(() => MacroBundleFolder.EditTemplates(dir, "из-будущего",
                    templates => [.. templates, new MacroBundleFile("Новый.png", Encoding.UTF8.GetBytes("png"))]))
                .Throws<MacroBundleUnreadableException>();

            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ---- неопознанные записи ------------------------------------------------------------------

    // Формат store-only ровно затем, чтобы в бандл лазили архиватором. Писатель же пересобирает
    // архив с нуля и переносил только то, что опознал: заметка автора рядом с шаблонами и файл,
    // положенный БУДУЩЕЙ версией формата, исчезали при первом же «Сохранить». Для submacro/ это
    // правило было записано и соблюдалось — теперь оно действует везде.
    [Test]
    public async Task Save_KeepsEntriesTheFormatDoesNotRecognise()
    {
        var dir = CreateTempDir();
        try
        {
            var path = MacroBundleFolder.PathFor(dir, "с-приложениями");
            MacroBundleFolder.Save(dir, SimpleMacro("с-приложениями"));
            var content = MacroBundleReader.ReadContent(path).Content!;
            MacroBundleWriter.Write(path, content with
            {
                Templates = [new MacroBundleFile("Кнопка.png", Encoding.UTF8.GetBytes("png"))],
                Extras =
                [
                    new MacroBundleFile("заметка.txt", Encoding.UTF8.GetBytes("как это работает")),
                    new MacroBundleFile("templates/источники.md", Encoding.UTF8.GetBytes("откуда вырезано")),
                    new MacroBundleFile("templates/куски/глубоко.png", Encoding.UTF8.GetBytes("не шаблон")),
                    new MacroBundleFile("будущее.json", Encoding.UTF8.GetBytes("{}")),
                ],
            });

            MacroBundleFolder.Save(dir, SimpleMacro("с-приложениями", VirtualKey.F9));

            using var archive = ZipFile.OpenRead(path);
            await Assert.That(archive.Entries.Select(e => e.FullName).Order().ToList())
                .IsEquivalentTo(new List<string>
                {
                    "metadata.json",
                    "nodes.json",
                    "templates/Кнопка.png",
                    "будущее.json",
                    "заметка.txt",
                    "templates/источники.md",
                    "templates/куски/глубоко.png",
                }.Order().ToList());

            // И содержимое, а не только имя: перенос обязан быть байт в байт.
            using var note = new StreamReader(archive.GetEntry("заметка.txt")!.Open());
            await Assert.That(note.ReadToEnd()).IsEqualTo("как это работает");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// Бандл, который эта сборка читать не умеет и НЕ ИМЕЕТ ПРАВА чинить: версия формата из
    /// будущего. Файл целый, паспорт разбирается, шаблон внутри лежит.
    /// </summary>
    private static void WriteFutureBundle(string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var metadata = archive.CreateEntry(MacroBundleFormat.MetadataEntry, CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(metadata.Open()))
        {
            writer.Write(MacroBundleMetadataJson.Serialize(
                MacroBundleMetadata.CreateNew("из-будущего") with
                {
                    FormatVersion = MacroBundleFormat.CurrentVersion + 7,
                }));
        }

        var graph = archive.CreateEntry(MacroBundleFormat.GraphEntry, CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(graph.Open()))
        {
            writer.Write(MacroGraphJson.Serialize(SimpleMacro("из-будущего")));
        }

        var template = archive.CreateEntry("templates/classes/Лучник.png", CompressionLevel.NoCompression);
        using var pixels = new StreamWriter(template.Open());
        pixels.Write("вырезано руками из игры");
    }

    [Test]
    public async Task Delete_RemovesFile_UnknownNameIsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("gone"));

            await Assert.That(MacroBundleFolder.Delete(dir, "gone")).IsTrue();
            await Assert.That(MacroBundleFolder.Delete(dir, "never-existed")).IsFalse();
            await Assert.That(File.Exists(MacroBundleFolder.PathFor(dir, "gone"))).IsFalse();
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
            await Assert.That(() => MacroBundleFolder.Save(dir, SimpleMacro("bad/name")))
                .Throws<ArgumentException>();
            await Assert.That(MacroBundleFolder.Read(dir)).IsEmpty();
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
        var error = MacroBundleFolder.ValidateName(name);
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
            MacroBundleFolder.Save(dir, SimpleMacro("атомарно"));
            MacroBundleFolder.Save(dir, SimpleMacro("атомарно", VirtualKey.F4));

            await Assert.That(Directory.EnumerateFiles(dir).Select(Path.GetFileName).ToList())
                .IsEquivalentTo(new List<string?> { "атомарно.hsm" });
            await Assert.That(File.Exists(AtomicFile.TempPathFor(MacroBundleFolder.PathFor(dir, "атомарно"))))
                .IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Замена переименованием обязана проходить и тогда, когда бандл в этот момент читают:
    // MoveFileEx с MOVEFILE_REPLACE_EXISTING отказывает, если получатель открыт без
    // FileShare.Delete, и это ровно тот отказ, который проявлялся бы изредка и невоспроизводимо.
    // С волны F3 это уже не теория: пишет панель, читает демон, и делают они это одновременно.
    [Test]
    public async Task Save_SucceedsWhileTheBundleIsBeingRead()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("занят-чтением"));
            var path = MacroBundleFolder.PathFor(dir, "занят-чтением");

            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                await Assert.That(() => MacroBundleFolder.Save(dir, SimpleMacro("занят-чтением", VirtualKey.F7)))
                    .ThrowsNothing();
                await Assert.That(reader.Length).IsGreaterThan(0);
            }

            var entry = MacroBundleFolder.ReadEntry(path);
            await Assert.That(((KeyPressNode)entry.Graph!.Nodes[0]).Key).IsEqualTo(VirtualKey.F7);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Личность макроса — основа имени ФАЙЛА. Переименовали проводником — переименовали макрос, и
    // оба процесса обязаны считать так одинаково: иначе список зовёт его одним именем, а хоткей
    // запускает под другим.
    [Test]
    public async Task ReadEntry_FileNameWins_OverTheNameInsideTheBundle()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("old-name"));
            File.Move(MacroBundleFolder.PathFor(dir, "old-name"), MacroBundleFolder.PathFor(dir, "renamed"));

            var entry = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "renamed"));

            await Assert.That(entry.Name).IsEqualTo("renamed");
            await Assert.That(entry.Graph!.Name).IsEqualTo("renamed");
            await Assert.That(entry.NameOverridden).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Тот случай, ради которого metadata.json и nodes.json лежат в бандле ПОРОЗНЬ: граф не
    // читается, а паспорт читается — и тогда в библиотеке видно настоящее имя с описанием, а не
    // «файл X — ошибка».
    [Test]
    public async Task ReadEntry_KeepsThePassport_WhenOnlyTheGraphIsBroken()
    {
        var dir = CreateTempDir();
        try
        {
            var path = MacroBundleFolder.PathFor(dir, "полусломан");
            using (var stream = new FileStream(path, FileMode.Create))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var metadata = archive.CreateEntry(MacroBundleFormat.MetadataEntry, CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(metadata.Open()))
                {
                    writer.Write(MacroBundleMetadataJson.Serialize(
                        MacroBundleMetadata.CreateNew("Загрузка лучника") with { Description = "вход в игру" }));
                }

                var graph = archive.CreateEntry(MacroBundleFormat.GraphEntry, CompressionLevel.NoCompression);
                using var broken = new StreamWriter(graph.Open());
                broken.Write("{ это не граф");
            }

            var entry = MacroBundleFolder.ReadEntry(path);

            await Assert.That(entry.Graph).IsNull();
            await Assert.That(entry.IsReadable).IsFalse();
            await Assert.That(entry.Metadata!.Name).IsEqualTo("Загрузка лучника");
            await Assert.That(entry.Metadata.Description).IsEqualTo("вход в игру");
            await Assert.That(entry.Fault).IsEqualTo(MacroBundleFault.Malformed);
            await Assert.That(entry.FaultMessage).IsNotNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Нечитаемый бандл ОСТАЁТСЯ в перечислении. До F3 хранилище демона его молча пропускало, и
    // файл существовал, не будучи виден нигде.
    [Test]
    public async Task Read_KeepsUnreadableBundlesInTheList()
    {
        var dir = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(dir, SimpleMacro("good"));
            File.WriteAllText(Path.Combine(dir, "broken.hsm"), "это вообще не zip");

            var entries = MacroBundleFolder.Read(dir);

            await Assert.That(entries.Select(e => e.Name).ToList())
                .IsEquivalentTo(new List<string> { "broken", "good" });
            await Assert.That(entries.Single(e => e.Name == "broken").IsReadable).IsFalse();
            await Assert.That(entries.Single(e => e.Name == "broken").Fault)
                .IsEqualTo(MacroBundleFault.NotAnArchive);
            await Assert.That(entries.Single(e => e.Name == "good").IsReadable).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Импорт — это КОПИРОВАНИЕ, а не разбор с пересборкой: отданный файл обязан работать у
    // получателя байт в байт, а пройдя через сегодняшнего писателя, он потерял бы всё, чего
    // сегодняшняя версия формата не знает.
    [Test]
    public async Task Import_CopiesTheFileByteForByte()
    {
        var source = CreateTempDir();
        var target = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(source, SimpleMacro("гость"));
            var sourcePath = MacroBundleFolder.PathFor(source, "гость");
            var sourceBytes = File.ReadAllBytes(sourcePath);

            var name = MacroBundleFolder.Import(target, sourcePath);

            await Assert.That(name).IsEqualTo("гость");
            await Assert.That(File.ReadAllBytes(MacroBundleFolder.PathFor(target, "гость")))
                .IsEquivalentTo(sourceBytes);
        }
        finally
        {
            DeleteTempDir(source);
            DeleteTempDir(target);
        }
    }

    // Занятое имя — вопрос к ЧЕЛОВЕКУ. Раньше импорт молча брал «гость-2»: файл ложился рядом с
    // тем, который пользователь, возможно, и собирался обновить, а сообщение об этом уезжало в
    // строку статуса. Оба исхода остались доступны, но теперь их называет вызывающий.
    [Test]
    public async Task Import_RefusesATakenNameUnlessTheCallerSaysWhatToDo()
    {
        var source = CreateTempDir();
        var target = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(source, SimpleMacro("гость"));
            var sourcePath = MacroBundleFolder.PathFor(source, "гость");
            MacroBundleFolder.Import(target, sourcePath);

            await Assert.That(() => MacroBundleFolder.Import(target, sourcePath))
                .Throws<MacroNameTakenException>();
            await Assert.That(MacroBundleFolder.Read(target).Select(e => e.Name).ToList())
                .IsEquivalentTo(new List<string> { "гость" });

            // «Взять свободное имя»: правило суффикса на месте, выбирает его вызывающий.
            var second = MacroBundleFolder.Import(
                target, sourcePath, MacroBundleFolder.FreeName(target, "гость"));
            await Assert.That(second).IsEqualTo("гость-2");
            // Личность — имя файла, так что копия под новым именем честно им и зовётся.
            await Assert.That(MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(target, "гость-2")).Graph!.Name)
                .IsEqualTo("гость-2");
        }
        finally
        {
            DeleteTempDir(source);
            DeleteTempDir(target);
        }
    }

    [Test]
    public async Task Import_WithReplace_OverwritesTheBundleThatWasThere()
    {
        var source = CreateTempDir();
        var target = CreateTempDir();
        try
        {
            MacroBundleFolder.Save(target, SimpleMacro("гость", VirtualKey.F1));
            MacroBundleFolder.Save(source, SimpleMacro("гость", VirtualKey.F9));
            var sourcePath = MacroBundleFolder.PathFor(source, "гость");

            var name = MacroBundleFolder.Import(target, sourcePath, replace: true);

            await Assert.That(name).IsEqualTo("гость");
            await Assert.That(MacroBundleFolder.Read(target)).Count().IsEqualTo(1);
            var entry = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(target, "гость"));
            await Assert.That(((KeyPressNode)entry.Graph!.Nodes[0]).Key).IsEqualTo(VirtualKey.F9);
            // Замена не оставляет после себя временного файла — механизм тот же, что у записи.
            await Assert.That(Directory.EnumerateFiles(target).Select(Path.GetFileName).ToList())
                .IsEquivalentTo(new List<string?> { "гость.hsm" });
        }
        finally
        {
            DeleteTempDir(source);
            DeleteTempDir(target);
        }
    }

    [Test]
    public async Task Import_RefusesWhatIsNotABundle()
    {
        var source = CreateTempDir();
        var target = CreateTempDir();
        try
        {
            var junk = Path.Combine(source, "мусор.hsm");
            File.WriteAllText(junk, "это вообще не zip");

            await Assert.That(() => MacroBundleFolder.Import(target, junk)).Throws<ArgumentException>();
            await Assert.That(MacroBundleFolder.Read(target)).IsEmpty();
        }
        finally
        {
            DeleteTempDir(source);
            DeleteTempDir(target);
        }
    }
}
