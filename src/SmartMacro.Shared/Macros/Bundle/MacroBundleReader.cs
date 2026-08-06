using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SmartMacro.Macros.Model;
using SmartMacro.Resources;


namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Чтение бандла <c>.hsm</c>.
///
/// <b>Три входа, и они разной цены — это и есть API.</b>
/// <list type="bullet">
///   <item><see cref="ReadMetadata(string)"/> — только паспорт. Библиотеке при показе списка
///     больше ничего не нужно, а граф чужого бандла может и не разобраться.</item>
///   <item><see cref="Read(string)"/> — паспорт, граф и ПЕРЕЧЕНЬ вложенных файлов без их
///     содержимого.</item>
///   <item><see cref="ReadTemplate(string,string)"/> — байты одного шаблона.</item>
/// </list>
/// Раздельность не украшение: список шаблонов весит десятки байт на запись, сами PNG —
/// килобайты, и «на всякий случай прочитаем всё» — это тот же промах, что миниатюры в строках
/// браузера шаблонов, только на диске.
///
/// Ни один метод не бросает на кривом файле: чтение бандла — это работа с чужими данными, и
/// исход у неё нормальный, а не исключительный. Исключения остаются за тем, что кривое у
/// ВЫЗЫВАЮЩЕГО (<c>null</c>, пустой путь).
///
/// <b>Размер проверяется ЗДЕСЬ, до первой распаковки, и это про потерю данных, а не про
/// аккуратность.</b> «Зип-бомб не бывает» верно только для бандлов, которые пишем мы: без сжатия
/// распакованный размер равен размеру файла. Импорт же — копирование чужого файла байт в байт
/// (иначе прогон через сегодняшнего писателя потерял бы всё, чего сегодняшний формат не знает),
/// так что читатель обязан считать сам. Первый запуск такого макроса шёл прямиком в
/// <see cref="ReadAllTemplates"/>, то есть «прочитать ВСЕ шаблоны бандла разом в память», —
/// и падал не редактор, а ДЕМОН, вместе со всеми хоткеями и прогонами.
///
/// Рубежа два, и они отвечают на разные вопросы:
/// <list type="number">
///   <item><b>По оглавлению архива, в <see cref="WithArchive{T}(Stream,Func{ZipArchive,T},Func{ValueTuple{MacroBundleFault,string},T})"/></b>
///     — сумма объявленных размеров против <see cref="MacroBundleFormat.MaxBundleBytes"/> и каждая
///     запись против <see cref="MacroBundleFormat.MaxEntryBytes"/>. Ничего не распаковывает, зато
///     отвечает ВСЕМ входам сразу, включая <see cref="Read(string)"/>, — а значит, вердикт
///     доезжает до строки библиотеки с красным «!», где пользователь его и увидит. Отказ здесь
///     общий на весь бандл: пропустить одну запись значило бы, что следующее «Сохранить»
///     перепишет файл без неё.</item>
///   <item><b>При самой распаковке</b> — запись отдаёт ровно столько байт, сколько объявила, иначе
///     считается нечитаемой. Оглавление пишет тот же, кто прислал файл, так что верить ему на
///     слово нельзя: без этой сверки сто записей по килобайту в оглавлении могли бы развернуться
///     в сто раз по шестьдесят мегабайт.</item>
/// </list>
/// </summary>
public static class MacroBundleReader
{
    /// <summary>Читает только <c>metadata.json</c>.</summary>
    public static MacroBundleMetadataResult ReadMetadata(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return WithArchive(
            path,
            ReadMetadata,
            fault => MacroBundleMetadataResult.Failed(fault.Fault, fault.Message));
    }

    /// <summary>Читает только <c>metadata.json</c> из уже открытого потока с архивом.</summary>
    public static MacroBundleMetadataResult ReadMetadata(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return WithArchive(
            source,
            ReadMetadata,
            fault => MacroBundleMetadataResult.Failed(fault.Fault, fault.Message));
    }

    /// <summary>Читает паспорт, граф и перечень вложенных файлов (без их содержимого).</summary>
    public static MacroBundleReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return WithArchive(path, ReadAll, Broken);
    }

    /// <summary>То же для уже открытого потока с архивом.</summary>
    public static MacroBundleReadResult Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return WithArchive(source, ReadAll, Broken);
    }

    /// <summary>
    /// Байты одного шаблона по пути ОТНОСИТЕЛЬНО <see cref="MacroBundleFormat.TemplateFolder"/>
    /// (<c>classes/Лучник.png</c>). <c>null</c> — такого шаблона в бандле нет, либо файл не
    /// читается: для вызывающего это одно и то же решение, а причину он уже узнал из
    /// <see cref="Read(string)"/>.
    /// </summary>
    public static byte[]? ReadTemplate(string path, string relativePath) =>
        ReadEntryBytes(path, MacroBundleFormat.TemplateFolder, relativePath);

    // Близнеца ReadTemplate для submacro/ здесь НЕТ, и это не пропуск. Шаблон читают по одному,
    // потому что это килобайты пикселей и смотрят на них по одному; под-макросы — это JSON
    // размером с nodes.json, и нужны они всегда все сразу: демону — исполнять, панели — показать
    // дерево. Их и читает Read() вместе с графом, разобранными (F4). Метод, отдающий байты
    // одного, звать было бы некому.

    /// <summary>
    /// ВСЕ шаблоны бандла разом, вместе с байтами, за одно открытие архива.
    ///
    /// ⚠️ <b>Исключение из правила «список — метаданные, пиксели — по требованию», и оно ровно
    /// одно.</b> Правило написано для БРАУЗЕРА: там пользователь смотрит на один шаблон, а «на
    /// всякий случай прочитаем всё» тянуло бы по трубе десятки килобайт ради одной картинки.
    /// Здесь потребитель другой — кэш исполнителя (<c>MacroTemplateCache</c>), который наполняется
    /// один раз на макрос и обслуживает потом каждый тик зрения, не открывая архив вовсе. Ему
    /// нужны именно все байты сразу: открывать zip по разу на имя значило бы платить открытием за
    /// каждую ноду, а тик сопоставления не имеет права ходить на диск.
    ///
    /// Записи, шаблоном не являющиеся (не PNG, глубже одного уровня), отбрасываются — см.
    /// <see cref="MacroBundleFormat.TryParseTemplatePath"/>. Нечитаемая запись пропускается: один
    /// битый PNG не должен лишать макрос остальных.
    /// </summary>
    /// <returns>Пути (относительно <see cref="MacroBundleFormat.TemplateFolder"/>) и содержимое; пусто, если файл не читается.</returns>
    public static IReadOnlyList<MacroBundleFile> ReadAllTemplates(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return WithArchive<IReadOnlyList<MacroBundleFile>>(
            path,
            archive => ReadFolder(archive, MacroBundleFormat.TemplateFolder, templatesOnly: true),
            _ => []);
    }

    /// <summary>
    /// Всё содержимое бандла целиком, вместе с байтами вложений, — то, что нужно ПЕРЕЗАПИСАТЬ
    /// бандл, поменяв в нём одну вещь.
    ///
    /// Хранилище правит бандл по частям (сохранили граф; добавили шаблон; удалили шаблон), а
    /// писатель умеет только «весь файл целиком» — иначе замена переименованием, на которой стоит
    /// атомарность, была бы невозможна. Поэтому цикл всегда один и тот же: прочитать всё,
    /// поменять одно, записать всё. <b>Под-макросы едут через этот метод байт в байт</b> — как
    /// файлы, а не как разобранные графы: тот, кто их не меняет, обязан вернуть на место в том
    /// числе запись, которую сегодняшний разбор под-макросом не считает.
    /// </summary>
    /// <returns>
    /// Исход чтения, а НЕ <c>MacroBundleContent?</c>. Разница между «файла нет» и «файл есть, но
    /// не прочитался» здесь несущая: на первом писать законно, на всех остальных запись обязана
    /// отказаться, иначе она сотрёт вложения. Довод целиком — у
    /// <see cref="MacroBundleContentResult"/>.
    /// </returns>
    public static MacroBundleContentResult ReadContent(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return WithArchive(
            path,
            archive =>
            {
                var metadata = ReadMetadata(archive);
                if (metadata.Metadata is not { } passport)
                {
                    return MacroBundleContentResult.Failed(metadata.Fault, metadata.Message);
                }

                var (graph, graphFault, graphMessage) = ReadGraph(archive);
                if (graph is null)
                {
                    return MacroBundleContentResult.Failed(graphFault, graphMessage);
                }

                return MacroBundleContentResult.Ok(new MacroBundleContent
                {
                    Metadata = passport,
                    Graph = graph,
                    Templates = ReadFolder(archive, MacroBundleFormat.TemplateFolder, templatesOnly: true),
                    Submacros = ReadFolder(archive, MacroBundleFormat.SubmacroFolder, templatesOnly: false),
                    Extras = ReadExtras(archive),
                });
            },
            fault => MacroBundleContentResult.Failed(fault.Fault, fault.Message));
    }

    /// <summary>
    /// Всё, что не опознано ни одной из трёх известных ролей: не паспорт, не граф, не шаблон по
    /// правилу разбора и не запись <c>submacro/</c>. Пути — ОТ КОРНЯ бандла.
    ///
    /// Существует ради перезаписи: писатель собирает архив с нуля, и то, чего нет в
    /// <see cref="MacroBundleContent"/>, исчезает молча. Довод — у
    /// <see cref="MacroBundleContent.Extras"/>.
    ///
    /// <c>submacro/</c> сюда не попадает целиком: её содержимое уже едет сырыми байтами в
    /// <see cref="MacroBundleContent.Submacros"/>, и второй экземпляр тех же записей писатель
    /// отверг бы как дубликат.
    /// </summary>
    private static IReadOnlyList<MacroBundleFile> ReadExtras(ZipArchive archive)
    {
        var files = new List<MacroBundleFile>();

        foreach (var entry in archive.Entries)
        {
            var path = MacroBundleFormat.NormalizeEntryPath(entry.FullName);
            if (!MacroBundleFormat.IsSafeRelativePath(path) || IsKnownRole(path))
            {
                continue;
            }

            try
            {
                files.Add(new MacroBundleFile(path, EntryContent(entry)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // Нечитаемая запись — единственное, что мы всё же теряем, и иначе никак: перенести
                // байты, которых не удалось получить, нечем.
            }
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    private static bool IsKnownRole(string path)
    {
        if (path is MacroBundleFormat.MetadataEntry or MacroBundleFormat.GraphEntry)
        {
            return true;
        }

        if (path.StartsWith(MacroBundleFormat.SubmacroFolder, StringComparison.Ordinal))
        {
            return true;
        }

        return path.StartsWith(MacroBundleFormat.TemplateFolder, StringComparison.Ordinal)
               && MacroBundleFormat.TryParseTemplatePath(
                   path[MacroBundleFormat.TemplateFolder.Length..], out _, out _);
    }

    /// <summary>
    /// Перечень шаблонов бандла для БРАУЗЕРА: путь, размеры в пикселях и вес — и ни одного
    /// пикселя.
    ///
    /// Тот же довод, что у <c>TemplateDto</c> в протоколе: одна запись весит десятки байт, так что
    /// весь список приезжает разом при открытии макроса, а картинка — за выделением, по одной.
    /// Размеры берутся из заголовка IHDR (24 байта), картинка не декодируется; вес — это
    /// <see cref="System.IO.Compression.ZipArchiveEntry.Length"/>, то есть РАСПАКОВАННЫЙ размер:
    /// вес самого PNG, а не место записи в файле. У наших бандлов это одно и то же (store-only),
    /// у импортированного сжатого — нет; довод целиком у <see cref="MacroBundleTemplateInfo.Bytes"/>.
    /// </summary>
    public static IReadOnlyList<MacroBundleTemplateInfo> ReadTemplateCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return WithArchive<IReadOnlyList<MacroBundleTemplateInfo>>(
            path,
            archive =>
            {
                var found = new List<MacroBundleTemplateInfo>();
                foreach (var (entry, relativePath) in EnumerateFolder(archive, MacroBundleFormat.TemplateFolder))
                {
                    if (!MacroBundleFormat.TryParseTemplatePath(relativePath, out _, out _))
                    {
                        continue;
                    }

                    found.Add(new MacroBundleTemplateInfo(relativePath, ReadPngSize(entry), entry.Length));
                }

                found.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
                return found;
            },
            _ => []);
    }

    private static byte[]? ReadEntryBytes(string path, string folder, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalized = MacroBundleFormat.NormalizeEntryPath(relativePath);
        if (!MacroBundleFormat.IsSafeRelativePath(normalized))
        {
            return null;
        }

        return WithArchive<byte[]?>(
            path,
            archive =>
            {
                var entry = archive.GetEntry(folder + normalized);
                if (entry is null)
                {
                    return null;
                }

                try
                {
                    return EntryContent(entry);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    return null;
                }
            },
            _ => null);
    }

    private static MacroBundleReadResult ReadAll(ZipArchive archive)
    {
        var metadata = ReadMetadata(archive);
        if (!metadata.IsOk)
        {
            // Граф не читаем даже не пытаясь: пока неизвестна версия формата, неизвестно и то,
            // чем считать содержимое nodes.json. Для бандла чужой версии это ВАЖНО — сказать
            // «повреждён» про целый файл будущей версии было бы прямой ложью. Причина отказа
            // переносится сюда как есть, чтобы вызывающему не пришлось смотреть в два поля,
            // чтобы понять одно.
            return new MacroBundleReadResult(
                metadata,
                null,
                metadata.Fault,
                string.Format(CultureInfo.CurrentCulture, Strings.Bundle_Read_GraphNotRead, Lower(metadata.Message)),
                [],
                [],
                []);
        }

        var (graph, graphFault, graphMessage) = ReadGraph(archive);
        var (submacros, submacroFaults) = ReadSubmacros(archive);
        return new MacroBundleReadResult(
            metadata,
            graph,
            graphFault,
            graphMessage,
            ListFolder(archive, MacroBundleFormat.TemplateFolder),
            submacros,
            submacroFaults);
    }

    /// <summary>
    /// Под-макросы бандла (волна F4): каждая запись <c>submacro/{Guid}.json</c> — граф.
    ///
    /// Не разобравшаяся запись НЕ роняет чтение и НЕ прячет родительский граф: она уезжает
    /// отдельным списком объяснений, из которого валидатор делает ошибку. Иначе один битый
    /// под-макрос превращал бы весь макрос в строку «файл X — ошибка», из которой не понять даже,
    /// какой это был макрос, — то самое, ради чего <c>metadata.json</c> и <c>nodes.json</c>
    /// разнесены.
    ///
    /// Записи, под правило имени не подходящие, молча пропускаются: писателю они всё равно
    /// поедут обратно байт в байт (см. <see cref="MacroBundleContent.Submacros"/>), а обвинять
    /// автора в чужом файле незачем.
    /// </summary>
    private static (IReadOnlyList<MacroSubmacro> Submacros, IReadOnlyList<string> Faults) ReadSubmacros(
        ZipArchive archive)
    {
        var found = new List<MacroSubmacro>();
        var faults = new List<string>();

        foreach (var (entry, relativePath) in EnumerateFolder(archive, MacroBundleFormat.SubmacroFolder))
        {
            if (!MacroBundleFormat.TryParseSubmacroPath(relativePath, out var id))
            {
                continue;
            }

            try
            {
                found.Add(new MacroSubmacro(id, MacroGraphJson.Deserialize(ReadText(entry))));
            }
            catch (JsonException ex)
            {
                faults.Add(string.Format(
                    CultureInfo.CurrentCulture, Strings.Bundle_Read_SubmacroMalformed, relativePath, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                faults.Add(string.Format(
                    CultureInfo.CurrentCulture, Strings.Bundle_Read_SubmacroUnreadable, relativePath, ex.Message));
            }
        }

        found.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        faults.Sort(StringComparer.Ordinal);
        return (found, faults);
    }

    private static MacroBundleMetadataResult ReadMetadata(ZipArchive archive)
    {
        var entry = archive.GetEntry(MacroBundleFormat.MetadataEntry);
        if (entry is null)
        {
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.EntryMissing,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Bundle_Read_MetadataEntryMissing,
                    MacroBundleFormat.MetadataEntry));
        }

        string text;
        try
        {
            text = ReadText(entry);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.NotAnArchive,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Bundle_Read_MetadataUnreadable,
                    MacroBundleFormat.MetadataEntry,
                    ex.Message));
        }

        // ВЕРСИЮ ДОСТАЁМ ПЕРВОЙ И ОТДЕЛЬНО, до разбора паспорта целиком. Иначе бандл чужой
        // версии, у которого поля паспорта другие, отвечал бы «файл повреждён» — то есть ровно
        // тем, чего поле версии призвано не допустить.
        int version;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(nameof(MacroBundleMetadata.FormatVersion), out var field)
                || !field.TryGetInt32(out version))
            {
                return MacroBundleMetadataResult.Failed(
                    MacroBundleFault.Malformed,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.Bundle_Read_MetadataNoVersionField,
                        MacroBundleFormat.MetadataEntry,
                        nameof(MacroBundleMetadata.FormatVersion)));
            }
        }
        catch (JsonException ex)
        {
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.Malformed,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Bundle_Read_MetadataNotJson,
                    MacroBundleFormat.MetadataEntry,
                    ex.Message));
        }

        if (version != MacroBundleFormat.CurrentVersion)
        {
            var relation = version > MacroBundleFormat.CurrentVersion
                ? Strings.Bundle_Read_VersionNewer
                : Strings.Bundle_Read_VersionOlder;
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.UnsupportedVersion,
                string.Format(
                    CultureInfo.InvariantCulture,
                    Strings.Bundle_Read_VersionMismatch,
                    relation,
                    version,
                    MacroBundleFormat.CurrentVersion),
                version);
        }

        try
        {
            return MacroBundleMetadataResult.Ok(MacroBundleMetadataJson.Deserialize(text));
        }
        catch (JsonException ex)
        {
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.Malformed,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Bundle_Read_MetadataMalformed,
                    MacroBundleFormat.MetadataEntry,
                    ex.Message),
                version);
        }
    }

    private static (MacroGraph? Graph, MacroBundleFault Fault, string? Message) ReadGraph(ZipArchive archive)
    {
        var entry = archive.GetEntry(MacroBundleFormat.GraphEntry);
        if (entry is null)
        {
            return (null, MacroBundleFault.EntryMissing, string.Format(
                CultureInfo.CurrentCulture, Strings.Bundle_Read_GraphEntryMissing, MacroBundleFormat.GraphEntry));
        }

        try
        {
            return (MacroGraphJson.Deserialize(ReadText(entry)), MacroBundleFault.None, null);
        }
        catch (JsonException ex)
        {
            return (null, MacroBundleFault.Malformed, string.Format(
                CultureInfo.CurrentCulture,
                Strings.Bundle_Read_GraphMalformed,
                MacroBundleFormat.GraphEntry,
                ex.Message));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (null, MacroBundleFault.NotAnArchive, string.Format(
                CultureInfo.CurrentCulture,
                Strings.Bundle_Read_GraphUnreadable,
                MacroBundleFormat.GraphEntry,
                ex.Message));
        }
    }

    private static IReadOnlyList<string> ListFolder(ZipArchive archive, string folder)
    {
        var files = new List<string>();
        foreach (var (_, relativePath) in EnumerateFolder(archive, folder))
        {
            files.Add(relativePath);
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    // Записи одной папки архива с уже отрезанным префиксом. Запись-«папка» (пустое имя) и всё,
    // что пытается вылезти наружу, не проходят — см. MacroBundleFormat.IsSafeRelativePath.
    private static IEnumerable<(ZipArchiveEntry Entry, string RelativePath)> EnumerateFolder(
        ZipArchive archive,
        string folder)
    {
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(folder, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = entry.FullName[folder.Length..];
            if (MacroBundleFormat.IsSafeRelativePath(relativePath))
            {
                yield return (entry, relativePath);
            }
        }
    }

    private static IReadOnlyList<MacroBundleFile> ReadFolder(ZipArchive archive, string folder, bool templatesOnly)
    {
        var files = new List<MacroBundleFile>();
        foreach (var (entry, relativePath) in EnumerateFolder(archive, folder))
        {
            if (templatesOnly && !MacroBundleFormat.TryParseTemplatePath(relativePath, out _, out _))
            {
                continue;
            }

            try
            {
                files.Add(new MacroBundleFile(relativePath, EntryContent(entry)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // Один битый PNG не должен лишать макрос остальных: нода, назвавшая его, пойдёт
                // по «не найдено» — ровно так же, как если бы файла не было вовсе.
            }
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    /// <summary>
    /// Ширина и высота PNG из заголовка IHDR: 8 байт сигнатуры, 8 байт длины и типа чанка, дальше
    /// два big-endian int32. Читаем 24 байта вместо того, чтобы декодировать картинку; запись, не
    /// похожая на PNG, отдаёт 0×0 и из перечня НЕ пропадает — она ведь лежит в бандле, и увидеть
    /// её пользователь должен именно затем, чтобы понять, что там не то.
    /// </summary>
    private static (int Width, int Height) ReadPngSize(ZipArchiveEntry entry)
    {
        try
        {
            Span<byte> header = stackalloc byte[24];
            using var stream = entry.Open();
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                   && header[..8].SequenceEqual(PngSignature)
                ? (BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
                    BinaryPrimitives.ReadInt32BigEndian(header[20..24]))
                : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (0, 0);
        }
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static string ReadText(ZipArchiveEntry entry)
    {
        // Через EntryContent, а не потоком напрямую: JSON бандла тоже приезжает из чужого файла, а
        // «прочитать nodes.json целиком» с той же готовностью развернёт зип-бомбу, что и шаблон.
        using var source = new MemoryStream(EntryContent(entry), writable: false);
        // detectEncodingFromByteOrderMarks: BOM мы не пишем, но правленный руками файл вполне
        // может приехать из редактора, который пишет.
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static MacroBundleReadResult Broken((MacroBundleFault Fault, string Message) fault) =>
        new(
            MacroBundleMetadataResult.Failed(fault.Fault, fault.Message),
            null,
            fault.Fault,
            fault.Message,
            [],
            [],
            []);

    private static T WithArchive<T>(
        string path,
        Func<ZipArchive, T> read,
        Func<(MacroBundleFault Fault, string Message), T> onFailure)
    {
        FileStream stream;
        try
        {
            // FileShare.Delete ОБЯЗАТЕЛЕН, и это половина атомарной записи (см.
            // MacroBundleWriter): замена файла переименованием — это MoveFileEx с
            // MOVEFILE_REPLACE_EXISTING, а он отказывает, если существующий получатель открыт без
            // разрешения на удаление. Без этого флага сохранение макроса падало бы ровно тогда,
            // когда его в этот момент кто-то читает, — то есть изредка и невоспроизводимо.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return onFailure((MacroBundleFault.Missing, string.Format(
                CultureInfo.CurrentCulture, Strings.Bundle_Read_FileMissing, path)));
        }
        catch (DirectoryNotFoundException)
        {
            return onFailure((MacroBundleFault.Missing, string.Format(
                CultureInfo.CurrentCulture, Strings.Bundle_Read_FileMissing, path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Занятый или недоступный файл — история ВРЕМЕННАЯ, и отличать её от порчи важно:
            // хранилище на порче отодвигает файл в сторону, а на этом — не должно.
            return onFailure((MacroBundleFault.NotAnArchive, string.Format(
                CultureInfo.CurrentCulture, Strings.Bundle_Read_FileNotOpened, path, ex.Message)));
        }

        using (stream)
        {
            return WithArchive(stream, read, onFailure);
        }
    }

    private static T WithArchive<T>(
        Stream source,
        Func<ZipArchive, T> read,
        Func<(MacroBundleFault Fault, string Message), T> onFailure)
    {
        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

            // Первый рубеж проверки размера — до того, как кто-нибудь распакует хоть байт, и на
            // ВСЕ входы сразу. Довод целиком — в шапке класса.
            if (Oversize(archive) is { } tooLarge)
            {
                return onFailure((MacroBundleFault.TooLarge, tooLarge));
            }

            return read(archive);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return onFailure((MacroBundleFault.NotAnArchive, string.Format(
                CultureInfo.CurrentCulture, Strings.Bundle_Read_NotAnArchive, ex.Message)));
        }
    }

    /// <summary>
    /// Вердикт по ОГЛАВЛЕНИЮ архива: <c>null</c> — размеры в пределах, иначе текст отказа.
    /// Ничего не распаковывает — перечисление записей читает центральный каталог.
    /// </summary>
    private static string? Oversize(ZipArchive archive)
    {
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MacroBundleFormat.MaxEntryBytes)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Bundle_Read_EntryTooLarge,
                    entry.FullName,
                    Megabytes(entry.Length),
                    Megabytes(MacroBundleFormat.MaxEntryBytes));
            }

            total += entry.Length;
        }

        return total > MacroBundleFormat.MaxBundleBytes
            ? string.Format(
                CultureInfo.CurrentCulture,
                Strings.Bundle_Read_BundleTooLarge,
                Megabytes(total),
                Megabytes(MacroBundleFormat.MaxBundleBytes))
            : null;
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024)).ToString("F0", CultureInfo.CurrentCulture);

    /// <summary>
    /// Содержимое одной записи — второй рубеж проверки размера.
    ///
    /// Читает РОВНО столько, сколько запись о себе объявила, и требует, чтобы поток на этом и
    /// кончился. Оглавление архива пишет тот, кто прислал файл, так что «объявила килобайт, а
    /// разворачивается в шестьдесят мегабайт» — это не теоретическая возможность, а сам приём;
    /// первый рубеж (сумма по оглавлению) от него не защищает.
    ///
    /// Бросает <see cref="InvalidDataException"/>, а не отдаёт <c>null</c>, намеренно: у всех
    /// вызывающих уже есть обработка нечитаемой записи (пропустить один битый PNG, объяснить
    /// нечитаемый <c>metadata.json</c>), и второй способ сказать то же самое им не нужен.
    /// </summary>
    private static byte[] EntryContent(ZipArchiveEntry entry)
    {
        var declared = entry.Length;
        if (declared > MacroBundleFormat.MaxEntryBytes)
        {
            throw new InvalidDataException(string.Format(
                CultureInfo.CurrentCulture,
                Strings.Bundle_Read_EntryTooLarge,
                entry.FullName,
                Megabytes(declared),
                Megabytes(MacroBundleFormat.MaxEntryBytes)));
        }

        using var source = entry.Open();
        var buffer = new byte[(int)declared];
        if (source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length
            || source.ReadByte() >= 0)
        {
            throw new InvalidDataException(string.Format(
                CultureInfo.CurrentCulture, Strings.Bundle_Read_EntryLengthMismatch, entry.FullName));
        }

        return buffer;
    }

    private static string Lower(string? message) =>
        string.IsNullOrEmpty(message)
            ? Strings.Bundle_Read_MetadataNotRead
            : char.ToLowerInvariant(message[0]) + message[1..];
}
