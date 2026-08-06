using System.IO.Compression;
using System.Text;
using SmartMacro.Io;
using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Запись бандла <c>.hsm</c>.
///
/// <b>Запись АТОМАРНА, и живёт эта атомарность не здесь.</b> В F1 она была оставлена вызывающему;
/// в F2 её опустили сюда сознательно (в F3 писать стала ПАНЕЛЬ, а не демон, и логика «как положить
/// файл на диск, чтобы наблюдатель не поймал половину» обязана переиспользоваться, а не
/// переписываться вторым экземпляром в другом процессе). Теперь тот же довод увёл её на шаг
/// дальше: у демона есть <c>settings.json</c> с такими же наблюдателем и ценой усечения, так что
/// схема живёт в <see cref="AtomicFile"/> — там же и все измеренные доводы про
/// <c>ReplaceFile</c> против <c>MoveFileEx</c> и про <c>FileShare.Delete</c> у читателя.
///
/// Здесь остаётся то, что действительно про бандл: <b>имя временного файла — <c>{имя}.hsm.tmp</c></b>,
/// и это не произвольный выбор. Наблюдатель хранилища отфильтрован по <c>*.hsm</c>, а короткое имя
/// 8.3 берёт первые три символа ПОСЛЕДНЕГО расширения, то есть <c>FOOHSM~1.TMP</c>, — под фильтр не
/// попадает ни длинное имя, ни короткое. Читатель со своей стороны обязан открывать архив с
/// <c>FileShare.Delete</c>, иначе замена упадёт ровно в тот момент, когда файл кто-то читает; см.
/// <see cref="MacroBundleReader"/>.
///
/// Перегрузка на <see cref="Stream"/> остаётся: ею пользуются тесты и всё, что собирает бандл в
/// памяти (например, чтобы отдать его по проводу).
/// </summary>
public static class MacroBundleWriter
{
    /// <summary>
    /// Пишет бандл по указанному пути атомарно: целиком во временный файл рядом, затем замена
    /// переименованием. Читатель либо увидит прежний файл, либо новый, но никогда — половину.
    /// </summary>
    /// <exception cref="ArgumentException">Относительный путь файла внутри бандла небезопасен или повторяется.</exception>
    public static void Write(string path, MacroBundleContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        AtomicFile.Write(path, stream => Write(stream, content));
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

        // Одно множество занятых имён на ВЕСЬ архив, а не по одному на папку: с появлением
        // Extras пути приезжают из четырёх источников, и столкнуться они могут между собой, а не
        // только внутри своей папки.
        //
        // ⚠️ Сравнение — ORDINAL, и это исправление, а не небрежность. Здесь стоял
        // OrdinalIgnoreCase с доводом «NTFS регистр не различает, так что «Лучник.png» и
        // «лучник.png» у получателя распакуются в один файл». Довод описывает сценарий, которого
        // у нас нет: шаблоны НИКОГДА не распаковываются на диск — исполнитель, опись и браузер
        // читают записи архива в память и сравнивают имена ordinal'но, потому что «Лучник» и
        // «лучник» — разные теги (см. MacroTemplateInventory). А цена осторожности была
        // невозвратной: импортированный бандл (импорт — копирование файла, содержимое намеренно
        // не разбирается) с двумя такими записями читался, исполнялся и показывался двумя
        // строками, но первое же «Сохранить» падало с «Путь встречается в бандле дважды» — и
        // починить это из интерфейса было нельзя, потому что удаление шаблона идёт через того же
        // писателя. Правило отбора дубликатов обязано совпадать с правилом разрешения имён, иначе
        // писатель запрещает то, что читатель и исполнитель считают законным.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        WriteText(archive, MacroBundleFormat.MetadataEntry, MacroBundleMetadataJson.Serialize(metadata), stamp, seen);
        WriteText(archive, MacroBundleFormat.GraphEntry, MacroGraphJson.Serialize(content.Graph), stamp, seen);

        WriteFolder(archive, MacroBundleFormat.TemplateFolder, content.Templates, stamp, seen);
        WriteFolder(archive, MacroBundleFormat.SubmacroFolder, content.Submacros, stamp, seen);
        // Неопознанное — последним и с путями ОТ КОРНЯ: это ровно то, что писатель не понимает,
        // и трогать его нельзя ничем, кроме переноса байт в байт (см. MacroBundleContent.Extras).
        WriteFolder(archive, folder: string.Empty, content.Extras, stamp, seen);
    }

    private static void WriteFolder(
        ZipArchive archive,
        string folder,
        IReadOnlyList<MacroBundleFile> files,
        DateTimeOffset stamp,
        HashSet<string> seen)
    {
        // Порядок записей — по возрастанию пути, а не в порядке, в каком их дал вызывающий.
        // Тот же довод, что и у отказа от сжатия: два сохранения одного и того же макроса
        // обязаны давать одинаковый файл, иначе git нечего дельтить, а diff показывает
        // перестановку там, где ничего не менялось.
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

            var entryName = folder + relativePath;
            if (!seen.Add(entryName))
            {
                throw new ArgumentException($"Путь «{entryName}» встречается в бандле дважды.", nameof(files));
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            entry.LastWriteTime = stamp;
            using var target = entry.Open();
            target.Write(file.Bytes);
        }
    }

    private static void WriteText(
        ZipArchive archive,
        string entryName,
        string text,
        DateTimeOffset stamp,
        HashSet<string> seen)
    {
        seen.Add(entryName);
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
