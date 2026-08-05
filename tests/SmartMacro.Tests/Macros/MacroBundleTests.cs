using System.IO.Compression;
using System.Text;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;

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
            await Assert.That(read.Metadata.Message).Contains("v7");
            await Assert.That(read.Metadata.Message).Contains("более новой");
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
            await Assert.That(raw).Contains("\"$type\": \"recognizeTag\"");
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

    [Test]
    public async Task Write_RefusesTwoTemplatesThatDifferOnlyInCase()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "регистр.hsm");

            // На NTFS это не два файла, а один — у получателя бандл распаковался бы неполным.
            await Assert.That(() => MacroBundleWriter.Write(path, Content() with
            {
                Templates =
                [
                    new MacroBundleFile("classes/Лучник.png", [1]),
                    new MacroBundleFile("classes/лучник.png", [2]),
                ],
            })).Throws<ArgumentException>();
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
}
