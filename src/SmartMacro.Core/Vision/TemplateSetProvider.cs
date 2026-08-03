using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SmartMacro.Vision;

/// <summary>
/// Разрешает имена шаблонов, которые несут ноды макроса, в байты PNG.
///
/// Две формы под два семейства нод:
///   * ОДИН шаблон по имени (<c>FindElementNode</c>/<c>WaitForElementNode</c>) —
///     <see cref="TryGetTemplate"/>.
///   * НАБОР шаблонов с ключами-тегами (<c>RecognizeTagNode</c>) — <see cref="GetSet"/>, где
///     каждый ключ есть основа имени файла и, значит, тег, который проставляется при
///     совпадении.
///
/// Физическая раскладка (W0.2b): существующие папки с ассетами переиспользуются как есть, а не
/// перелопачиваются в раскладку <c>templates/{set}/{tag}.png</c>, набросанную в плане:
///   * набор <c>"classes"</c>         → <c>Assets/GameClassNames/*.png</c>
///   * любой другой набор <c>"{name}"</c> → <c>Assets/templates/{name}/*.png</c>
///   * одиночные шаблоны по основе имени → <c>Assets/GameUiElements/{stem}.png</c>
/// TODO(W0.4): схлопнуть GameUiElements и GameClassNames в одно дерево <c>templates/</c> и
/// выбросить псевдоним "classes" — он существует лишь затем, чтобы поставляемые ассеты PW
/// продолжали работать нетронутыми.
///
/// Всё читается с диска при первом обращении и кэшируется на время жизни процесса (шаблоны
/// маленькие и во время работы не меняются); отсутствующая папка или нечитаемый файл пишутся в
/// лог и дают пустой результат, а не исключение, — чтобы один плохой PNG не утащил за собой всю
/// библиотеку макросов.
/// </summary>
public sealed partial class TemplateSetProvider
{
    /// <summary>Имя набора, которое ложится на унаследованную папку <c>Assets/GameClassNames</c>.</summary>
    public const string ClassesSetName = "classes";

    private readonly string _assetsRoot;
    private readonly ILogger<TemplateSetProvider> _logger;

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> _sets =
        new(StringComparer.Ordinal);

    private readonly Lazy<IReadOnlyDictionary<string, byte[]>> _singleTemplates;

    public TemplateSetProvider(ILogger<TemplateSetProvider> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "Assets"), logger)
    {
    }

    /// <param name="assetsRoot">Папка, в которой лежат <c>GameUiElements</c> / <c>GameClassNames</c> / <c>templates</c>.</param>
    /// <param name="logger">Приёмник диагностики.</param>
    public TemplateSetProvider(string assetsRoot, ILogger<TemplateSetProvider> logger)
    {
        _assetsRoot = assetsRoot;
        _logger = logger;
        _singleTemplates = new Lazy<IReadOnlyDictionary<string, byte[]>>(() =>
            LoadFolder("single templates", Path.Combine(_assetsRoot, "GameUiElements")));
    }

    /// <summary>
    /// Именованный набор шаблонов в виде «тег → байты PNG». Неизвестные и пустые наборы дают
    /// пустой словарь (с одной записью в логе) — тогда <c>RecognizeTagNode</c> просто никогда
    /// ни с чем не совпадёт.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> GetSet(string setName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        return _sets.GetOrAdd(setName, LoadSet);
    }

    /// <summary>
    /// Один шаблон по имени. Имена — это основы имён файлов, и сравниваются они С УЧЁТОМ
    /// РЕГИСТРА: поиск идёт по перечислению папки, а не через <c>File.Exists</c>, чтобы шаблон
    /// разрешался здесь так же, как внутри набора, — иначе регистронезависимая файловая система
    /// Windows развела бы эти два пути.
    /// </summary>
    /// <returns>Байты PNG или <c>null</c>, если такого шаблона нет.</returns>
    public byte[]? TryGetTemplate(string templateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        if (_singleTemplates.Value.TryGetValue(templateName, out var bytes))
        {
            return bytes;
        }

        LogTemplateMissing(templateName, Path.Combine(_assetsRoot, "GameUiElements"));
        return null;
    }

    private IReadOnlyDictionary<string, byte[]> LoadSet(string setName)
    {
        var directory = string.Equals(setName, ClassesSetName, StringComparison.Ordinal)
            ? Path.Combine(_assetsRoot, "GameClassNames")
            : Path.Combine(_assetsRoot, "templates", setName);
        return LoadFolder(setName, directory);
    }

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
}
