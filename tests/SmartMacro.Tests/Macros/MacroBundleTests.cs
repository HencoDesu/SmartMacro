using System.Globalization;
using System.IO.Compression;
using System.Text;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Resources;

namespace SmartMacro.Tests.Macros;

// F1: формат .hsm. Проверяется не «сериализация работает», а те четыре свойства, ради которых
// формат такой, какой есть:
//   * round trip ничего не теряет — включая шаблоны с кириллическими именами, а они у нас
//     единственные настоящие («Лучник.png»);
//   * метаданные читаются ОТДЕЛЬНО от графа и переживают его порчу — иначе в библиотеке вместо
//     имени макроса будет «файл X — ошибка»;
//   * файл чужой версии формата и файл повреждённый — это РАЗНЫЕ новости;
//   * zip без сжатия, то есть содержимое видно прямо в файле.
public class MacroBundleTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 3, 14, 15, 9, 26, TimeSpan.Zero);

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-bundle-{Guid.NewGuid():N}");
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

    private static MacroBundleMetadata Metadata() => MacroBundleMetadata.CreateNew(
        "полный",
        description: "макрос с кириллицей в описании — «Лучник»",
        author: "автор",
        now: Stamp);

    private static MacroBundleContent Content(MacroBundleMetadata? metadata = null) => new()
    {
        Metadata = metadata ?? Metadata(),
        Graph = FullMacroGraphFixture.Build(),
        Templates =
        [
            new MacroBundleFile("classes/Лучник.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]),
            new MacroBundleFile("classes/Жрец.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 4, 5, 6]),
            new MacroBundleFile("CharacterSelectButton.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 7]),
        ],
    };

    // ── Круг ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RoundTrip_PreservesMetadataGraphAndTemplateBytes()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "полный.hsm");
            var original = Content();
            MacroBundleWriter.Write(path, original);

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.IsOk).IsTrue();
            await Assert.That(read.Metadata.Metadata).IsEqualTo(original.Metadata);
            await Assert.That(read.Metadata.FormatVersion).IsEqualTo(MacroBundleFormat.CurrentVersion);

            // Граф сверяется формой JSON: у модели поля-List, и записи не сравниваются по
            // значению — тот же приём, что в MacroGraphJsonTests.
            await Assert.That(MacroGraphJson.Serialize(read.Graph!))
                .IsEqualTo(MacroGraphJson.Serialize(original.Graph));

            await Assert.That(read.TemplatePaths).IsEquivalentTo(
                new[] { "CharacterSelectButton.png", "classes/Жрец.png", "classes/Лучник.png" });
            await Assert.That(read.Submacros).IsEmpty();

            foreach (var template in original.Templates)
            {
                var bytes = MacroBundleReader.ReadTemplate(path, template.Path);
                await Assert.That(bytes).IsNotNull();
                await Assert.That(bytes!.SequenceEqual(template.Bytes)).IsTrue();
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task RoundTrip_FindsATemplateAskedForWithWindowsSeparators()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "sep.hsm");
            MacroBundleWriter.Write(path, Content());

            // Вызывающий на Windows естественно напишет обратный слеш; в zip лежит прямой.
            await Assert.That(MacroBundleReader.ReadTemplate(path, @"classes\Лучник.png")).IsNotNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task ReadTemplate_ReturnsNullForSomethingThatIsNotThere()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "нет.hsm");
            MacroBundleWriter.Write(path, Content());

            await Assert.That(MacroBundleReader.ReadTemplate(path, "classes/Друид.png")).IsNull();
            // И то, что пытается вылезти за свою папку, — тоже «нет».
            await Assert.That(MacroBundleReader.ReadTemplate(path, "../nodes.json")).IsNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    /// <summary>
    /// Записи <c>submacro/</c>, которые под правило имени не подходят (заметка автора, файл
    /// будущей версии формата), переживают перезапись бандла байт в байт и НЕ становятся
    /// вердиктом о поломке.
    ///
    /// Это то же свойство, ради которого папка была предусмотрена ещё в F1: сегодняшний код не
    /// имеет права терять то, чего он не понимает.
    /// </summary>
    [Test]
    public async Task SubmacroFolder_CarriesUnknownEntriesThroughByteForByte()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "f4.hsm");
            MacroBundleWriter.Write(path, Content() with
            {
                Submacros = [new MacroBundleFile("заметка.txt", Encoding.UTF8.GetBytes("не под-макрос"))],
            });

            var read = MacroBundleReader.Read(path);

            // Разбор её под-макросом не считает — и молча, без обвинений автора.
            await Assert.That(read.Submacros).IsEmpty();
            await Assert.That(read.SubmacroFaults).IsEmpty();

            // А писатель обязан вернуть её на место.
            var content = MacroBundleReader.ReadContent(path).Content;
            await Assert.That(content!.Submacros.Select(file => file.Path))
                .IsEquivalentTo(new[] { "заметка.txt" });
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task StreamOverloads_AreTheSeamTheStoreWillWriteThroughAtomically()
    {
        // Хранилищу (F3) писать придётся во временный файл в той же папке и заменять
        // переименованием, так что круг «поток -> поток» обязан работать сам по себе.
        using var buffer = new MemoryStream();
        MacroBundleWriter.Write(buffer, Content());

        buffer.Position = 0;
        var metadata = MacroBundleReader.ReadMetadata(buffer);
        buffer.Position = 0;
        var full = MacroBundleReader.Read(buffer);

        await Assert.That(metadata.IsOk).IsTrue();
        await Assert.That(metadata.Metadata!.Name).IsEqualTo("полный");
        await Assert.That(full.IsOk).IsTrue();
        await Assert.That(full.TemplatePaths).Count().IsEqualTo(3);
        // Поток остаётся открытым — им распоряжается вызывающий.
        await Assert.That(buffer.CanRead).IsTrue();
    }

    // ── Раздельное чтение метаданных ──────────────────────────────────────────────────────

    [Test]
    public async Task ReadMetadata_WorksWhenTheGraphIsGarbage()
    {
        // Ради этого случая metadata.json и nodes.json разнесены: библиотека обязана показать
        // нормальное имя с пометкой об ошибке, а не строку «файл X — ошибка», из которой не
        // понять даже, какой это макрос.
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "битый-граф.hsm");
            MacroBundleWriter.Write(path, Content());
            ReplaceEntry(path, MacroBundleFormat.GraphEntry, "{ это не json ");

            var metadataOnly = MacroBundleReader.ReadMetadata(path);
            await Assert.That(metadataOnly.IsOk).IsTrue();
            await Assert.That(metadataOnly.Metadata!.Name).IsEqualTo("полный");

            var full = MacroBundleReader.Read(path);
            await Assert.That(full.IsOk).IsFalse();
            await Assert.That(full.Metadata.IsOk).IsTrue();
            await Assert.That(full.Metadata.Metadata!.Name).IsEqualTo("полный");
            await Assert.That(full.Graph).IsNull();
            await Assert.That(full.GraphFault).IsEqualTo(MacroBundleFault.Malformed);
            await Assert.That(full.GraphMessage).Contains(MacroBundleFormat.GraphEntry);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Read_WithoutTheGraphEntryAtAll_StillReadsMetadata()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "без-графа.hsm");
            MacroBundleWriter.Write(path, Content());
            DeleteEntry(path, MacroBundleFormat.GraphEntry);

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.Metadata.IsOk).IsTrue();
            await Assert.That(read.GraphFault).IsEqualTo(MacroBundleFault.EntryMissing);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ⚠️ Бандл, сделанный ДО переименования RecognizeTag → MatchTemplateSet, — не повреждённый
    // файл и не файл чужой версии формата: он целый, версия у него своя, и не хватает ровно
    // одного — типа ноды. Читатель обязан сказать именно это и НАЗВАТЬ тип, иначе искать в файле
    // нечего. Раньше здесь стоял вердикт «повреждён» с английской строкой каркаса.
    [Test]
    public async Task Read_OfAGraphWithAnUnknownNodeType_NamesTheTypeInsteadOfCryingCorruption()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "прошлая-нода.hsm");
            MacroBundleWriter.Write(path, Content());
            ReplaceEntry(path, MacroBundleFormat.GraphEntry,
                """
                {
                  "Name": "прошлая-нода",
                  "StartNodeId": "11111111-1111-1111-1111-111111111111",
                  "Nodes": [ { "$type": "recognizeTag", "Id": "11111111-1111-1111-1111-111111111111" } ]
                }
                """);

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.GraphFault).IsEqualTo(MacroBundleFault.UnknownType);
            await Assert.That(read.GraphMessage!).Contains("recognizeTag");
            await Assert.That(read.GraphMessage!).Contains(MacroBundleFormat.GraphEntry);
            // Паспорт при этом цел, и строка библиотеки покажет настоящее имя автора.
            await Assert.That(read.Metadata.IsOk).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Обратная сторона того же: по-настоящему битый JSON обязан остаться «повреждён». Разбор
    // незнакомого типа не имеет права подменять собой честный вердикт.
    [Test]
    public async Task Read_OfTrulyBrokenJson_StaysMalformed()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "битый.hsm");
            MacroBundleWriter.Write(path, Content());
            ReplaceEntry(path, MacroBundleFormat.GraphEntry, "{ \"Nodes\": [ ");

            await Assert.That(MacroBundleReader.Read(path).GraphFault).IsEqualTo(MacroBundleFault.Malformed);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ── Отказы, которые обязаны звучать по-разному ────────────────────────────────────────

    [Test]
    public async Task Read_OfSomethingThatIsNotAZip_SaysSo()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "мусор.hsm");
            File.WriteAllText(path, "это точно не zip");

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.Metadata.Fault).IsEqualTo(MacroBundleFault.NotAnArchive);
            await Assert.That(read.GraphFault).IsEqualTo(MacroBundleFault.NotAnArchive);
            await Assert.That(read.Metadata.Message).IsNotNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Read_OfAZipWithoutMetadata_SaysItIsNotOurBundle()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "чужой.zip.hsm");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("readme.txt", CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("hello");
            }

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.Metadata.Fault).IsEqualTo(MacroBundleFault.EntryMissing);
            await Assert.That(read.Metadata.Message).Contains(MacroBundleFormat.MetadataEntry);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Read_OfAFutureFormatVersion_SaysSoInsteadOfCallingItBroken()
    {
        // Ради этого поле версии и пишется в файл с первого дня.
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "будущее.hsm");
            MacroBundleWriter.Write(path, Content());
            ReplaceEntry(
                path,
                MacroBundleFormat.MetadataEntry,
                """{ "FormatVersion": 7, "ЧтоТоНовое": true }""");

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.Metadata.Fault).IsEqualTo(MacroBundleFault.UnsupportedVersion);
            await Assert.That(read.Metadata.FormatVersion).IsEqualTo(7);
            // Сообщение называет ОБЕ версии и то, в какую сторону они разошлись: «более ранней»
            // здесь была бы прямо противоположная новость.
            await Assert.That(Msg.Args(read.Metadata.Message, Strings.Bundle_Read_VersionMismatch))
                .IsEquivalentTo(new[]
                {
                    Strings.Bundle_Read_VersionNewer, "7",
                    MacroBundleFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                });
            // Граф чужой версии даже не пробуем разбирать, и причина у отказа та же самая.
            await Assert.That(read.GraphFault).IsEqualTo(MacroBundleFault.UnsupportedVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Read_OfMetadataWithoutAVersionField_CallsItMalformedNotAnOldVersion()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "без-версии.hsm");
            MacroBundleWriter.Write(path, Content());
            ReplaceEntry(path, MacroBundleFormat.MetadataEntry, """{ "Name": "полный" }""");

            var read = MacroBundleReader.ReadMetadata(path);

            await Assert.That(read.Fault).IsEqualTo(MacroBundleFault.Malformed);
            await Assert.That(read.FormatVersion).IsNull();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Read_OfMetadataMissingARequiredField_IsMalformedAndKeepsTheVersion()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "неполный-паспорт.hsm");
            MacroBundleWriter.Write(path, Content());
            ReplaceEntry(path, MacroBundleFormat.MetadataEntry, """{ "FormatVersion": 0 }""");

            var read = MacroBundleReader.ReadMetadata(path);

            await Assert.That(read.Fault).IsEqualTo(MacroBundleFault.Malformed);
            await Assert.That(read.FormatVersion).IsEqualTo(0);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Read_OfAFileThatIsNotThere_SaysMissingRatherThanBroken()
    {
        var dir = CreateTempDir();
        try
        {
            var read = MacroBundleReader.Read(Path.Combine(dir, "нет-такого.hsm"));

            await Assert.That(read.Metadata.Fault).IsEqualTo(MacroBundleFault.Missing);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ── Свойства самого файла ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Written_IsStoredNotDeflated_SoContentIsVisibleInTheFile()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "store.hsm");
            MacroBundleWriter.Write(path, Content());

            using (var stream = File.OpenRead(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    // Хранится как есть: сжатый размер равен исходному.
                    await Assert.That(entry.CompressedLength).IsEqualTo(entry.Length);
                }
            }

            // И это видно снаружи, без распаковки: имя макроса лежит в файле открытым текстом.
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            await Assert.That(raw).Contains("\"Name\": \"полный\"");
            await Assert.That(raw).Contains("\"$type\": \"matchTemplateSet\"");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Written_IsByteIdenticalForTheSameContent_SoGitHasSomethingToDelta()
    {
        var dir = CreateTempDir();
        try
        {
            var metadata = Metadata();
            var first = Path.Combine(dir, "a.hsm");
            var second = Path.Combine(dir, "b.hsm");

            MacroBundleWriter.Write(first, Content(metadata));
            // Порядок шаблонов у вызывающего другой — на файл это влиять не должно.
            MacroBundleWriter.Write(second, Content(metadata) with
            {
                Templates = [.. Content(metadata).Templates.Reverse()],
            });

            await Assert.That(File.ReadAllBytes(second).SequenceEqual(File.ReadAllBytes(first))).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Written_StampsTheFormatVersionEvenIfTheCallerPutSomethingElseThere()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "версия.hsm");
            MacroBundleWriter.Write(path, Content(Metadata() with { FormatVersion = 42 }));

            var read = MacroBundleReader.ReadMetadata(path);

            await Assert.That(read.IsOk).IsTrue();
            await Assert.That(read.Metadata!.FormatVersion).IsEqualTo(MacroBundleFormat.CurrentVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task Write_RefusesAPathThatWouldEscapeItsFolder()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "zip-slip.hsm");

            await Assert.That(() => MacroBundleWriter.Write(path, Content() with
            {
                Templates = [new MacroBundleFile("../../evil.png", [1])],
            })).Throws<ArgumentException>();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ⚠️ Здесь стояло обратное утверждение — «писатель ОТКАЗЫВАЕТ двум шаблонам, различающимся
    // регистром», с доводом про NTFS. Довод описывал сценарий, которого у нас нет: шаблоны никогда
    // не распаковываются на диск, а исполнитель и опись разрешают имена ordinal'но, потому что
    // «Лучник» и «лучник» — разные теги. Цена осторожности была невозвратной: импортированный
    // бандл (импорт — копирование файла, содержимое намеренно не разбирается) с двумя такими
    // записями читался, исполнялся и показывался двумя строками, но первое же «Сохранить» падало —
    // и починить это из интерфейса было нельзя, потому что удаление шаблона идёт через того же
    // писателя.
    //
    // Проверяем весь круг: читается, сохраняется, и ПОСЛЕ сохранения оба на месте, каждый со
    // своими байтами.
    [Test]
    public async Task ABundleWithTwoTemplatesDifferingOnlyInCaseSurvivesASaveWithBothIntact()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "регистр.hsm");
            MacroBundleWriter.Write(path, Content() with
            {
                Templates =
                [
                    new MacroBundleFile("classes/Лучник.png", [1]),
                    new MacroBundleFile("classes/лучник.png", [2]),
                ],
            });

            var read = MacroBundleReader.Read(path);
            await Assert.That(read.TemplatePaths)
                .IsEquivalentTo(new[] { "classes/Лучник.png", "classes/лучник.png" });

            // Цикл «прочитать всё → поменять одно → записать всё» — то самое место, где раньше
            // ломалось.
            var content = MacroBundleReader.ReadContent(path);
            await Assert.That(content.IsOk).IsTrue();
            MacroBundleWriter.Write(path, content.Content! with { Metadata = content.Content!.Metadata.Touch() });

            var again = MacroBundleReader.ReadAllTemplates(path);
            await Assert.That(again.Select(file => file.Path))
                .IsEquivalentTo(new[] { "classes/Лучник.png", "classes/лучник.png" });
            await Assert.That(again.Single(file => file.Path == "classes/Лучник.png").Bytes)
                .IsEquivalentTo(new byte[] { 1 });
            await Assert.That(again.Single(file => file.Path == "classes/лучник.png").Bytes)
                .IsEquivalentTo(new byte[] { 2 });
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Побуквенный дубль писатель по-прежнему отвергает: две записи с ОДНИМ И ТЕМ ЖЕ именем — это
    // архив, из которого читатель достанет одну, то есть молчаливая потеря второй.
    [Test]
    public async Task Write_RefusesTheSamePathTwice()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "дубль.hsm");

            await Assert.That(() => MacroBundleWriter.Write(path, Content() with
            {
                Templates =
                [
                    new MacroBundleFile("classes/Лучник.png", [1]),
                    new MacroBundleFile("classes\\Лучник.png", [2]),
                ],
            })).Throws<ArgumentException>();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ── Пределы распаковки ────────────────────────────────────────────────────────────────
    //
    // «Зип-бомб не бывает» верно только для бандлов, которые пишем МЫ: свойство «распакованный
    // размер равен размеру файла» обеспечивает писатель (store-only). Импорт — копирование чужого
    // файла байт в байт, и сжатие в нём никто не запрещал. Первый запуск такого макроса шёл прямо
    // в ReadAllTemplates («все шаблоны бандла разом в память»), то есть ронял по памяти ДЕМОНА —
    // резидентный движок со всеми хоткеями и прогонами.
    //
    // Отказ здесь общий на весь бандл, а не «пропустим эту запись»: пропущенная запись исчезла бы
    // при первом же «Сохранить», то есть та же потеря, только тише.

    [Test]
    public async Task AnEntryThatInflatesPastTheLimitMakesTheWholeBundleUnreadable()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "бомба.hsm");
            WriteCompressedBundle(path, ("templates/бомба.png", MacroBundleFormat.MaxEntryBytes + 1));

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.IsOk).IsFalse();
            await Assert.That(read.Metadata.Fault).IsEqualTo(MacroBundleFault.TooLarge);
            // Вердикт называет запись и оба числа — иначе «что-то не так с файлом» и всё.
            await Assert.That(Msg.Is(read.Metadata.Message, Strings.Bundle_Read_EntryTooLarge)).IsTrue();
            await Assert.That(read.Metadata.Message).Contains("templates/бомба.png");

            // Дорога, по которой демон и падал: шаблоны не читаются вовсе.
            await Assert.That(MacroBundleReader.ReadAllTemplates(path)).IsEmpty();
            await Assert.That(MacroBundleReader.ReadTemplateCatalog(path)).IsEmpty();

            // И перезапись отказывается: «прочитать всё → поменять одно → записать всё» не может
            // выполнить первый шаг, а писать поверх значило бы стереть содержимое.
            var content = MacroBundleReader.ReadContent(path);
            await Assert.That(content.IsOk).IsFalse();
            await Assert.That(content.IsMissing).IsFalse();
            await Assert.That(content.Fault).IsEqualTo(MacroBundleFault.TooLarge);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Одного предела на запись мало: тысяча записей по мегабайту кладёт демон ровно так же.
    [Test]
    public async Task ManyEntriesUnderTheEntryLimitStillTripTheBundleLimit()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "россыпь.hsm");
            var each = MacroBundleFormat.MaxEntryBytes - 1;
            var count = (int)(MacroBundleFormat.MaxBundleBytes / each) + 1;
            WriteCompressedBundle(path,
                [.. Enumerable.Range(0, count).Select(i => ($"templates/часть-{i}.png", each))]);

            var read = MacroBundleReader.Read(path);

            await Assert.That(read.Metadata.Fault).IsEqualTo(MacroBundleFault.TooLarge);
            await Assert.That(Msg.Is(read.Metadata.Message, Strings.Bundle_Read_BundleTooLarge)).IsTrue();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Второй рубеж. Сумма по оглавлению — это доверие к числу, которое написал тот же, кто прислал
    // файл. Здесь оглавление обещает 16 байт, а запись отдаёт мегабайт; проверка по оглавлению
    // проходит, и поймать это может только само чтение.
    [Test]
    public async Task AnEntryThatOutgrowsItsDeclaredSizeIsNotMaterialised()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "враньё.hsm");
            MacroBundleWriter.Write(path, Content() with
            {
                Templates = [new MacroBundleFile("Кадр.png", new byte[1024 * 1024])],
            });
            LieAboutUncompressedSize(path, "templates/Кадр.png", declared: 16);

            // Оглавление в пределах, так что бандл читается — и паспорт, и граф на месте.
            var read = MacroBundleReader.Read(path);
            await Assert.That(read.IsOk).IsTrue();

            // А байты записи не отдаются: не тот размер — значит, нечитаемая запись.
            await Assert.That(MacroBundleReader.ReadTemplate(path, "Кадр.png")).IsNull();
            await Assert.That(MacroBundleReader.ReadAllTemplates(path).Select(file => file.Path))
                .DoesNotContain("Кадр.png");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ── Guid ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateNew_HandsOutAFreshGuidEveryTime_BecauseDuplicatingIsCreating()
    {
        var first = MacroBundleMetadata.CreateNew("макрос", now: Stamp);
        var second = MacroBundleMetadata.CreateNew("макрос — копия", now: Stamp);

        await Assert.That(first.Id).IsNotEqualTo(second.Id);
        await Assert.That(first.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(first.Created).IsEqualTo(first.Modified);
        await Assert.That(first.FormatVersion).IsEqualTo(MacroBundleFormat.CurrentVersion);
    }

    [Test]
    public async Task Touch_MovesOnlyTheModifiedStamp()
    {
        var original = MacroBundleMetadata.CreateNew("макрос", now: Stamp);

        var touched = original.Touch(Stamp.AddHours(1));

        await Assert.That(touched.Id).IsEqualTo(original.Id);
        await Assert.That(touched.Created).IsEqualTo(original.Created);
        await Assert.That(touched.Modified).IsEqualTo(Stamp.AddHours(1));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────────────────

    private static void ReplaceEntry(string path, string entryName, string text)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void DeleteEntry(string path, string entryName)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
    }

    /// <summary>
    /// Собирает СЖАТЫЙ бандл с записями заданного распакованного размера — то, чего наш писатель
    /// не производит и чего импорт не запрещает.
    ///
    /// Нули пишутся потоком по мегабайту: дефлейт сжимает их в тысячу раз, так что файл на диске
    /// остаётся крошечным, а оглавление честно объявляет полный размер — ровно как настоящая
    /// зип-бомба.
    /// </summary>
    private static void WriteCompressedBundle(string path, params (string Entry, long Bytes)[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        Text(archive, MacroBundleFormat.MetadataEntry, MacroBundleMetadataJson.Serialize(Metadata()));
        Text(archive, MacroBundleFormat.GraphEntry, MacroGraphJson.Serialize(FullMacroGraphFixture.Build()));

        var chunk = new byte[1024 * 1024];
        foreach (var (name, size) in entries)
        {
            using var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            for (long written = 0; written < size;)
            {
                var take = (int)Math.Min(chunk.Length, size - written);
                target.Write(chunk, 0, take);
                written += take;
            }
        }

        static void Text(ZipArchive archive, string name, string text)
        {
            using var writer = new StreamWriter(
                archive.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    /// <summary>
    /// Правит в ОГЛАВЛЕНИИ архива объявленный распакованный размер одной записи, не трогая саму
    /// запись. Собрать такой файл через <see cref="ZipArchive"/> нельзя — он всегда пишет правду,
    /// а проверить надо именно недоверие к оглавлению.
    ///
    /// Раскладка записи центрального каталога (PK\x01\x02): подпись 4, дальше по два байта до
    /// crc32 на смещении 16, сжатый размер на 20, распакованный на 24, длина имени на 28.
    /// </summary>
    private static void LieAboutUncompressedSize(string path, string entryName, uint declared)
    {
        var bytes = File.ReadAllBytes(path);
        var name = Encoding.UTF8.GetBytes(entryName);
        ReadOnlySpan<byte> signature = [0x50, 0x4B, 0x01, 0x02];

        for (var i = 0; i + 46 + name.Length <= bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, 4).SequenceEqual(signature)
                || BitConverter.ToUInt16(bytes, i + 28) != name.Length
                || !bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
            {
                continue;
            }

            BitConverter.GetBytes(declared).CopyTo(bytes, i + 24);
            File.WriteAllBytes(path, bytes);
            return;
        }

        throw new InvalidOperationException($"В оглавлении архива нет записи «{entryName}».");
    }
}
