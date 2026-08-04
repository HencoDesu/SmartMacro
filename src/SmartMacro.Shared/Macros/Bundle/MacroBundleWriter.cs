using System.IO.Compression;
using System.Text;
using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Запись бандла <c>.hsm</c>.
///
/// ⚠️ <b>Запись НЕ атомарна, и это не недосмотр.</b> Атомарность (писать во временный файл В ТОЙ
/// ЖЕ ПАПКЕ и заменять переименованием — только в пределах тома работает <c>MoveFileEx</c>)
/// принадлежит хранилищу: это оно знает папку, за которой следит <c>FileSystemWatcher</c>, и
/// это оно потом отвечает за то, чтобы наблюдатель не поймал наполовину записанный файл. Здесь
/// же есть перегрузка на <see cref="Stream"/> — она и есть шов, в который хранилище вставит
/// свой временный файл. Переименовывать СУЩЕСТВУЮЩИЙ файл во временный при этом нельзя: это
/// создаёт окно, в котором макроса нет вовсе. См. §13.1 спеки.
/// </summary>
public static class MacroBundleWriter
{
    /// <summary>
    /// Пишет бандл по указанному пути, затирая существующий файл.
    /// </summary>
    /// <exception cref="ArgumentException">Относительный путь файла внутри бандла небезопасен или повторяется.</exception>
    public static void Write(string path, MacroBundleContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(stream, content);
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
