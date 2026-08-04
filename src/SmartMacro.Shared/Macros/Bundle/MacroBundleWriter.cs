using System.IO.Compression;
using System.Text;
using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Запись бандла <c>.hsm</c>.
///
/// <b>Запись АТОМАРНА, и живёт эта атомарность здесь, а не в хранилище.</b> В F1 она была
/// оставлена вызывающему; в F2 её опустили сюда сознательно: в F3 писать будет ПАНЕЛЬ, а не демон,
/// и логика «как положить файл на диск, чтобы наблюдатель не поймал половину» обязана
/// переиспользоваться, а не переписываться вторым экземпляром в другом процессе.
///
/// Схема (§13.1 спеки):
/// <list type="number">
///   <item>записать целиком во временный файл <b>в той же папке</b> — замена атомарна только в
///     пределах тома, так что временный файл в <c>%TEMP%</c> обесценил бы всю затею;</item>
///   <item>заменить существующий файл этим временным.</item>
/// </list>
/// <b>Существующий файл во временный НЕ переименовывается.</b> Это выглядит как аккуратная
/// подстраховка («сохраним старый на случай сбоя»), а на деле создаёт окно, в котором макроса нет
/// вовсе: наблюдатель, попавший в это окно, честно доложит, что макрос удалили, — и хоткей
/// снимется с регистрации.
///
/// ⚠️ <b>Замена делается <see cref="File.Replace(string,string,string?,bool)"/>, а НЕ
/// <see cref="File.Move(string,string,bool)"/>, и это измерено, а не вычитано.</b> Очевидный
/// выбор — <c>Move</c> с перезаписью, то есть <c>MoveFileEx</c> с <c>MOVEFILE_REPLACE_EXISTING</c>.
/// Он падает с <c>UnauthorizedAccessException</c>, если получателя кто-то держит открытым, —
/// причём падает ДАЖЕ ТОГДА, когда читатель честно открыл файл с <c>FileShare.Delete</c>.
/// <c>ReplaceFile</c> (за которым стоит <c>File.Replace</c>) в этой же ситуации отрабатывает.
/// Проверено на .NET 10 / Windows, все четыре сочетания:
/// <code>
///   Move    + FileShare.Read|Delete → UnauthorizedAccessException
///   Move    + FileShare.Read        → UnauthorizedAccessException
///   Replace + FileShare.Read|Delete → успех
///   Replace + FileShare.Read        → IOException «файл занят другим процессом»
/// </code>
/// Отсюда два следствия, и оба несущие. Первое: замена делается <c>Replace</c>. Второе:
/// <c>FileShare.Delete</c> у читателя (<see cref="MacroBundleReader"/>) — не украшение, а вторая
/// половина того же механизма; сняв его, мы получим сохранение, которое падает ровно тогда, когда
/// бандл в этот момент читают, то есть изредка и невоспроизводимо.
///
/// <c>ReplaceFile</c> требует существующего получателя, поэтому первая запись макроса идёт обычным
/// переименованием: заменять там нечего, и открыть файл, которого ещё нет, тоже некому.
///
/// Имя временного файла — <c>{имя}.hsm.tmp</c>, и это не произвольный выбор: наблюдатель
/// хранилища отфильтрован по <c>*.hsm</c>, а короткое имя 8.3 берёт первые три символа
/// ПОСЛЕДНЕГО расширения, то есть <c>FOOHSM~1.TMP</c>, — под фильтр не попадает ни длинное имя,
/// ни короткое. Читатель со своей стороны обязан открывать архив с <c>FileShare.Delete</c>, иначе
/// замена упадёт ровно в тот момент, когда файл кто-то читает; см. <see cref="MacroBundleReader"/>.
///
/// Перегрузка на <see cref="Stream"/> остаётся: ею пользуются тесты и всё, что собирает бандл в
/// памяти (например, чтобы отдать его по проводу).
/// </summary>
public static class MacroBundleWriter
{
    /// <summary>Суффикс временного файла. Публичен, потому что на него смотрят и наблюдатель, и тесты.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Пишет бандл по указанному пути атомарно: целиком во временный файл рядом, затем замена
    /// переименованием. Читатель либо увидит прежний файл, либо новый, но никогда — половину.
    /// </summary>
    /// <exception cref="ArgumentException">Относительный путь файла внутри бандла небезопасен или повторяется.</exception>
    public static void Write(string path, MacroBundleContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var temp = path + TempSuffix;
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Write(stream, content);
                // Явный сброс до переименования: содержимое обязано быть на диске раньше, чем имя
                // начнёт обещать, что оно там есть.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // ignoreMetadataErrors: замена не должна падать из-за того, что не удалось
                // перенести атрибуты или ACL (сетевой диск, чужая папка) — содержимое важнее.
                // Резервная копия не запрашивается: старый файл нам не нужен, а лишний
                // «*.hsm.bak» рядом с макросами пользователь заметит и будет гадать, что это.
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (FileNotFoundException)
        {
            // Гонка: между проверкой и заменой файл успели удалить. Тогда это просто первая
            // запись, и переименования достаточно.
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Недописанный временный файл не оставляем: под фильтр наблюдателя он не попадает, но
            // мусор рядом с макросами пользователь увидит и будет гадать, что это.
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не вышло — и ладно: следующая запись этого макроса перезапишет его по тому же имени.
        }
    }

    /// <summary>
    /// Пишет бандл в поток. Поток остаётся открытым — им распоряжается вызывающий.
    /// </summary>
    /// <exception cref="ArgumentException">Относительный путь файла внутри бандла небезопасен или повторяется.</exception>
    public static void Write(Stream destination, MacroBundleContent content)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(content);

        // entryNameEncoding НЕ задаём намеренно. При null .NET кодирует имена записей в UTF-8 и
        // выставляет флаг 11 (EFS) там, где имя выходит за ASCII, — это то, чего ждут архиваторы
        // и проводник, и именно так «templates/classes/Лучник.png» открывается кириллицей у
        // получателя. Явно переданная кодировка меняет это поведение и стоила бы нам ровно тех
        // имён, которые в этом проекте и есть настоящие.
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var metadata = content.Metadata with { FormatVersion = MacroBundleFormat.CurrentVersion };
        var stamp = ZipStamp(metadata.Modified);

        WriteText(archive, MacroBundleFormat.MetadataEntry, MacroBundleMetadataJson.Serialize(metadata), stamp);
        WriteText(archive, MacroBundleFormat.GraphEntry, MacroGraphJson.Serialize(content.Graph), stamp);

        WriteFolder(archive, MacroBundleFormat.TemplateFolder, content.Templates, stamp);
        WriteFolder(archive, MacroBundleFormat.SubmacroFolder, content.Submacros, stamp);
    }

    private static void WriteFolder(
        ZipArchive archive,
        string folder,
        IReadOnlyList<MacroBundleFile> files,
        DateTimeOffset stamp)
    {
        // Порядок записей — по возрастанию пути, а не в порядке, в каком их дал вызывающий.
        // Тот же довод, что и у отказа от сжатия: два сохранения одного и того же макроса
        // обязаны давать одинаковый файл, иначе git нечего дельтить, а diff показывает
        // перестановку там, где ничего не менялось.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = files
            .Select(file => (File: file, Path: MacroBundleFormat.NormalizeEntryPath(file.Path)))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal);

        foreach (var (file, relativePath) in ordered)
        {
            if (!MacroBundleFormat.IsSafeRelativePath(relativePath))
            {
                throw new ArgumentException(
                    $"Путь «{file.Path}» не годится внутри бандла: ожидается относительный путь без «..».",
                    nameof(files));
            }

            // NTFS регистр не различает, так что «Лучник.png» и «лучник.png» в одном бандле —
            // это не два шаблона, а бандл, который у получателя распакуется в один файл.
            if (!seen.Add(relativePath))
            {
                throw new ArgumentException($"Путь «{relativePath}» встречается в бандле дважды.", nameof(files));
            }

            var entry = archive.CreateEntry(folder + relativePath, CompressionLevel.NoCompression);
            entry.LastWriteTime = stamp;
            using var target = entry.Open();
            target.Write(file.Bytes);
        }
    }

    private static void WriteText(ZipArchive archive, string entryName, string text, DateTimeOffset stamp)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        entry.LastWriteTime = stamp;
        using var target = entry.Open();
        // Без BOM: файл читают и человек в просмотрщике, и System.Text.Json, и никому из них
        // спецификатор в начале не нужен.
        target.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
    }

    /// <summary>
    /// Время записи, которое проставляется ВСЕМ записям архива, — <see cref="MacroBundleMetadata.Modified"/>.
    ///
    /// По умолчанию zip проставил бы «сейчас», и файл менялся бы при каждой записи, даже когда
    /// содержимое то же самое, — то есть ровно то, ради отсутствия чего выбран store-only
    /// (git-дельты). Дата в zip хранится в формате DOS и живёт с 1980 по 2107 год; всё, что за
    /// эти рамки, оставляем библиотеке по умолчанию, а не роняем запись из-за штампа.
    /// </summary>
    private static DateTimeOffset ZipStamp(DateTimeOffset modified) =>
        modified.Year is >= 1980 and <= 2107 ? modified : new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
