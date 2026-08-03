using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SmartMacro.Vision;

/// <summary>
/// Одно дерево <c>Assets/templates/</c> и всё, что о нём знает демон: разрешение имён, которые
/// несут ноды макроса, в байты PNG — и перечисление того же дерева для браузера шаблонов в
/// панели.
///
/// Раскладка ровно одна, и она же есть правило именования:
/// <code>
///   Assets/templates/{имя}.png        одиночный шаблон  → FindElement / WaitForElement
///   Assets/templates/{набор}/{тег}.png набор шаблонов    → RecognizeTag (имя файла = тег)
/// </code>
/// До этой волны одиночные шаблоны лежали в <c>Assets/GameUiElements</c>, а набор
/// <c>"classes"</c> был жёстко прошитым псевдонимом папки <c>Assets/GameClassNames</c>. Псевдоним
/// удалён: <c>"classes"</c> теперь ничем не отличается от любого другого набора и означает
/// подпапку <c>templates/classes/</c>. <b>Имена, которыми шаблоны называют ноды, при этом не
/// поменялись ни одно</b> — переехали только файлы, — поэтому ни миграции макросов, ни правки
/// пользовательских графов не потребовалось.
///
/// <b>Два пути чтения, намеренно разные.</b>
///   * <see cref="TryGetTemplate"/> / <see cref="GetSet"/> — путь ИСПОЛНИТЕЛЯ. Кэшируется на
///     время жизни процесса: шаблоны маленькие, во время прогона не меняются, а каждый тик
///     vision не должен ходить на диск.
///   * <see cref="Catalog"/> / <see cref="TryReadFile"/> — путь БРАУЗЕРА в панели. Читает диск
///     каждый раз и кэш не трогает, чтобы список и превью показывали то, что лежит в папке
///     ПРЯМО СЕЙЧАС: пользователь роняет туда новый PNG именно для того, чтобы на него
///     посмотреть, и список, отвечающий снимком с момента старта демона, был бы для этого
///     бесполезен. Обратная сторона названа честно: пока демон не перезапущен, движок будет
///     матчить теми байтами, что попали в кэш первыми.
///
/// Отсутствующая папка или нечитаемый файл пишутся в лог и дают пустой результат, а не
/// исключение, — чтобы один плохой PNG не утащил за собой всю библиотеку макросов.
/// </summary>
public sealed partial class TemplateSetProvider
{
    /// <summary>
    /// Набор, которым опознают класс персонажа, — тот, на который ссылаются примеры
    /// <c>pw-*</c>. Обычная подпапка <c>templates/classes/</c>, никакого особого разрешения за
    /// этим именем больше не стоит; константа осталась, чтобы поставляемые графы и тесты
    /// ссылались на одну строку, а не на три её копии.
    /// </summary>
    public const string ClassesSetName = "classes";

    /// <summary>Имя папки с шаблонами внутри <c>Assets/</c>.</summary>
    public const string TemplatesFolderName = "templates";

    private readonly string _root;
    private readonly ILogger<TemplateSetProvider> _logger;

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> _sets =
        new(StringComparer.Ordinal);

    private readonly Lazy<IReadOnlyDictionary<string, byte[]>> _singleTemplates;

    public TemplateSetProvider(ILogger<TemplateSetProvider> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "Assets"), logger)
    {
    }

    /// <param name="assetsRoot">Папка <c>Assets/</c>, внутри которой лежит дерево <c>templates/</c>.</param>
    /// <param name="logger">Приёмник диагностики.</param>
    public TemplateSetProvider(string assetsRoot, ILogger<TemplateSetProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
        _root = Path.Combine(assetsRoot, TemplatesFolderName);
        _logger = logger;
        _singleTemplates = new Lazy<IReadOnlyDictionary<string, byte[]>>(() => LoadFolder("одиночные шаблоны", _root));
    }

    /// <summary>Корень дерева шаблонов — <c>{assetsRoot}/templates</c>. Диагностика и тесты.</summary>
    public string TemplatesRoot => _root;

    // ---------------------------------------------------------------- путь исполнителя

    /// <summary>
    /// Именованный набор шаблонов в виде «тег → байты PNG» — подпапка
    /// <c>templates/{setName}/</c>. Неизвестные и пустые наборы дают пустой словарь (с одной
    /// записью в логе) — тогда <c>RecognizeTagNode</c> просто никогда ни с чем не совпадёт.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> GetSet(string setName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        return _sets.GetOrAdd(setName, name => LoadFolder(name, SetDirectory(name)));
    }

    /// <summary>
    /// Один шаблон по имени — файл <c>templates/{templateName}.png</c>. Имена сравниваются
    /// С УЧЁТОМ РЕГИСТРА: поиск идёт по перечислению папки, а не через <c>File.Exists</c>, чтобы
    /// шаблон разрешался здесь так же, как внутри набора, — иначе регистронезависимая файловая
    /// система Windows развела бы эти два пути.
    /// </summary>
    /// <returns>Байты PNG или <c>null</c>, если такого шаблона нет.</returns>
    public byte[]? TryGetTemplate(string templateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        if (_singleTemplates.Value.TryGetValue(templateName, out var bytes))
        {
            return bytes;
        }

        LogTemplateMissing(templateName, _root);
        return null;
    }

    // ------------------------------------------------------------------ путь браузера

    /// <summary>
    /// Всё дерево одним списком: сперва одиночные шаблоны из корня, затем содержимое каждой
    /// подпапки-набора. Внутри группы — по имени, порядок устойчив.
    ///
    /// Читает диск, а не кэш, — см. пояснение к классу. Дороговизны в этом нет: из файла берутся
    /// только длина и заголовок IHDR, сами пиксели не читаются.
    /// </summary>
    public IReadOnlyList<TemplateFileInfo> Catalog()
    {
        var found = new List<TemplateFileInfo>();
        if (!Directory.Exists(_root))
        {
            LogSetDirMissing("одиночные шаблоны", _root);
            return found;
        }

        Describe(found, set: null, _root);

        foreach (var directory in Directory.EnumerateDirectories(_root)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            Describe(found, Path.GetFileName(directory), directory);
        }

        return found;
    }

    /// <summary>
    /// Байты одного файла шаблона для превью — с диска, минуя кэш исполнителя.
    /// </summary>
    /// <param name="set">Набор (подпапка) либо <c>null</c> для одиночного шаблона в корне.</param>
    /// <param name="name">Основа имени файла — то самое имя, которым шаблон называет нода.</param>
    /// <returns>Байты PNG либо <c>null</c>, если такого файла нет (или он не читается).</returns>
    public byte[]? TryReadFile(string? set, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Имена приезжают из панели, то есть по проводу. Сегменты пути в них — это выход за
        // пределы Assets/, поэтому запрос с ними отклоняется целиком, а не «санируется»: имя с
        // разделителем не соответствует ни одному настоящему шаблону, так что терять нечего.
        if (HasPathSeparators(name) || (set is not null && HasPathSeparators(set)))
        {
            LogTemplatePathRejected(set, name);
            return null;
        }

        var directory = set is null ? _root : SetDirectory(set);
        var path = Path.Combine(directory, name + ".png");
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTemplateReadFailed(ex, name, path);
            return null;
        }
    }

    // ------------------------------------------------------------------- внутренности

    private string SetDirectory(string setName) => Path.Combine(_root, setName);

    private static bool HasPathSeparators(string value) =>
        value.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0
        || value.Contains("..", StringComparison.Ordinal)
        || Path.IsPathRooted(value);

    // Все *.png из одной папки, ключ — основа имени файла. Основа и есть идентичность: для
    // набора это тег, для одиночного поиска — имя шаблона.
    private IReadOnlyDictionary<string, byte[]> LoadFolder(string label, string directory)
    {
        var templates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            LogSetDirMissing(label, directory);
            return templates;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            try
            {
                templates[stem] = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                LogTemplateReadFailed(ex, stem, path);
            }
        }

        LogSetLoaded(label, templates.Count, directory);
        return templates;
    }

    private void Describe(List<TemplateFileInfo> into, string? set, string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.png")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            try
            {
                var file = new FileInfo(path);
                var (width, height) = ReadPngSize(path);
                into.Add(new TemplateFileInfo(
                    set,
                    Path.GetFileNameWithoutExtension(path),
                    width,
                    height,
                    file.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Один нечитаемый файл выпадает из списка со строкой в логе; браузер шаблонов не
                // та вещь, ради которой стоит валить запрос целиком.
                LogTemplateReadFailed(ex, Path.GetFileNameWithoutExtension(path), path);
            }
        }
    }

    /// <summary>
    /// Ширина и высота PNG из его заголовка IHDR: 8 байт сигнатуры, 8 байт длины и типа чанка,
    /// дальше два big-endian int32. Читаем 24 байта вместо того, чтобы декодировать картинку,
    /// потому что для списка нужны ровно эти два числа; файл, не похожий на PNG, отдаёт 0×0 и
    /// остаётся в списке (он ведь лежит в папке и его увидит исполнитель).
    /// </summary>
    private static (int Width, int Height) ReadPngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
               && header[..8].SequenceEqual(PngSignature)
            ? (BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
                BinaryPrimitives.ReadInt32BigEndian(header[20..24]))
            : (0, 0);
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    [LoggerMessage(LogLevel.Information, "Набор шаблонов '{SetName}' загружен: шаблонов {Count} из {Path}")]
    partial void LogSetLoaded(string setName, int count, string path);

    [LoggerMessage(LogLevel.Warning,
        "У набора шаблонов '{SetName}' нет папки в {Path} — ноды RecognizeTag с ним никогда не совпадут")]
    partial void LogSetDirMissing(string setName, string path);

    [LoggerMessage(LogLevel.Warning,
        "Шаблон '{TemplateName}' не найден в {Path} — ноды Find/Wait с ним никогда не совпадут")]
    partial void LogTemplateMissing(string templateName, string path);

    [LoggerMessage(LogLevel.Error, "Не удалось прочитать шаблон '{TemplateName}' из {Path}")]
    partial void LogTemplateReadFailed(Exception ex, string templateName, string path);

    [LoggerMessage(LogLevel.Warning,
        "Запрос шаблона отклонён: в имени есть путь (набор '{Set}', имя '{Name}')")]
    partial void LogTemplatePathRejected(string? set, string name);
}

/// <summary>
/// Один файл шаблона в том виде, в каком его видит браузер: где лежит, как называется и
/// насколько велик. Байтов здесь нет намеренно — см. <see cref="TemplateSetProvider.Catalog"/>.
/// </summary>
/// <param name="Set">Подпапка-набор либо <c>null</c> для одиночного шаблона в корне.</param>
/// <param name="Name">Основа имени файла — имя, которым шаблон называет нода (для набора это тег).</param>
/// <param name="Width">Ширина в пикселях; 0, если файл не разобрался как PNG.</param>
/// <param name="Height">Высота в пикселях; 0, если файл не разобрался как PNG.</param>
/// <param name="Bytes">Размер файла на диске.</param>
public sealed record TemplateFileInfo(string? Set, string Name, int Width, int Height, long Bytes);
