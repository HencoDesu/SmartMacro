using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SmartMacro.Macros.Model;

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

    /// <summary>То же для <see cref="MacroBundleFormat.SubmacroFolder"/>.</summary>
    public static byte[]? ReadSubmacro(string path, string relativePath) =>
        ReadEntryBytes(path, MacroBundleFormat.SubmacroFolder, relativePath);

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
                    using var source = entry.Open();
                    using var buffer = new MemoryStream();
                    source.CopyTo(buffer);
                    return buffer.ToArray();
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
                $"Граф не читался: {Lower(metadata.Message)}",
                [],
                []);
        }

        var (graph, graphFault, graphMessage) = ReadGraph(archive);
        return new MacroBundleReadResult(
            metadata,
            graph,
            graphFault,
            graphMessage,
            ListFolder(archive, MacroBundleFormat.TemplateFolder),
            ListFolder(archive, MacroBundleFormat.SubmacroFolder));
    }

    private static MacroBundleMetadataResult ReadMetadata(ZipArchive archive)
    {
        var entry = archive.GetEntry(MacroBundleFormat.MetadataEntry);
        if (entry is null)
        {
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.EntryMissing,
                $"В бандле нет «{MacroBundleFormat.MetadataEntry}» — это не бандл SmartMacro.");
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
                $"Запись «{MacroBundleFormat.MetadataEntry}» не читается: {ex.Message}");
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
                    $"В «{MacroBundleFormat.MetadataEntry}» нет поля «{nameof(MacroBundleMetadata.FormatVersion)}» — файл сделан не этой программой либо повреждён.");
            }
        }
        catch (JsonException ex)
        {
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.Malformed,
                $"«{MacroBundleFormat.MetadataEntry}» не разбирается как JSON: {ex.Message}");
        }

        if (version != MacroBundleFormat.CurrentVersion)
        {
            var relation = version > MacroBundleFormat.CurrentVersion ? "более новой" : "более ранней";
            return MacroBundleMetadataResult.Failed(
                MacroBundleFault.UnsupportedVersion,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Бандл сделан {0} версией формата (v{1}); эта сборка читает v{2}.",
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
                $"«{MacroBundleFormat.MetadataEntry}» не разбирается: {ex.Message}",
                version);
        }
    }

    private static (MacroGraph? Graph, MacroBundleFault Fault, string? Message) ReadGraph(ZipArchive archive)
    {
        var entry = archive.GetEntry(MacroBundleFormat.GraphEntry);
        if (entry is null)
        {
            return (null, MacroBundleFault.EntryMissing, $"В бандле нет «{MacroBundleFormat.GraphEntry}».");
        }

        try
        {
            return (MacroGraphJson.Deserialize(ReadText(entry)), MacroBundleFault.None, null);
        }
        catch (JsonException ex)
        {
            return (null, MacroBundleFault.Malformed, $"«{MacroBundleFormat.GraphEntry}» не разбирается: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (null, MacroBundleFault.NotAnArchive, $"Запись «{MacroBundleFormat.GraphEntry}» не читается: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ListFolder(ZipArchive archive, string folder)
    {
        var files = new List<string>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(folder, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = entry.FullName[folder.Length..];
            // Запись-«папка» (пустое имя) и всё, что пытается вылезти наружу, в перечень не
            // попадают — см. MacroBundleFormat.IsSafeRelativePath.
            if (MacroBundleFormat.IsSafeRelativePath(relativePath))
            {
                files.Add(relativePath);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
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
            []);

    private static T WithArchive<T>(
        string path,
        Func<ZipArchive, T> read,
        Func<(MacroBundleFault Fault, string Message), T> onFailure)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            return onFailure((MacroBundleFault.Missing, $"Файла «{path}» нет."));
        }
        catch (DirectoryNotFoundException)
        {
            return onFailure((MacroBundleFault.Missing, $"Файла «{path}» нет."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Занятый или недоступный файл — история ВРЕМЕННАЯ, и отличать её от порчи важно:
            // хранилище на порче отодвигает файл в сторону, а на этом — не должно.
            return onFailure((MacroBundleFault.NotAnArchive, $"Файл «{path}» не открывается: {ex.Message}"));
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
            return read(archive);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return onFailure((MacroBundleFault.NotAnArchive, $"Файл не открывается как бандл: {ex.Message}"));
        }
    }

    private static string Lower(string? message) =>
        string.IsNullOrEmpty(message)
            ? "паспорт бандла не прочитан."
            : char.ToLowerInvariant(message[0]) + message[1..];
}
