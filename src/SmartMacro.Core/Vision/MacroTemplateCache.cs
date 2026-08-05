using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;

namespace SmartMacro.Vision;

/// <summary>
/// Шаблоны макросов в памяти — то единственное, что стоит между тиком сопоставления и zip-архивом
/// на диске.
///
/// <b>Зачем вообще кэш.</b> С общим деревом «сходить за шаблоном» означало прочитать PNG; с
/// бандлом — открыть zip, найти запись, распаковать. Делать это на каждом тике зрения нельзя тем
/// более, чем раньше. Прежний <c>TemplateSetProvider</c> держал кэш на ВСЁ ВРЕМЯ ЖИЗНИ ПРОЦЕССА и
/// честно признавал цену: пока демон не перезапущен, он матчит теми байтами, что попали в кэш
/// первыми. Здесь этой цены нет, и вот из чего это устроено.
///
/// <b>Ключ — ИМЯ МАКРОСА.</b> Не путь и не бандл: имя файла и есть идентичность макроса (§13.1),
/// прогон знает своё имя, и по имени же ключуется всё остальное в хранилище. Одно имя — одна
/// запись кэша, в ней ВСЕ шаблоны этого бандла.
///
/// <b>Наполняется лениво на уровне МАКРОСА и разом на уровне бандла.</b> Первое обращение к
/// любому шаблону макроса открывает архив ОДИН раз и вычитывает из него все шаблоны сразу
/// (<see cref="MacroBundleReader.ReadAllTemplates"/>); дальше макрос не трогает диск вовсе.
/// Промежуточные варианты хуже оба: «лениво по одному имени» платит открытием архива за каждое
/// новое имя (у распознавания класса это одиннадцать открытий), «заранее для всей библиотеки»
/// читает бандлы макросов, которые в этом сеансе никто не запустит.
///
/// <b>Сбрасывается по <see cref="MacroGraphStore.MacrosChanged"/>, целиком.</b> То самое событие,
/// которым хранилище объявляет, что библиотека на диске изменилась. С волны F3 источник у этого
/// изменения ровно один — наблюдатель за папкой, потому что демон больше не пишет вовсе; писать
/// шаблон в бандл стало делом панели. Отсюда важное свойство: <b>граф и его шаблоны устаревают и
/// обновляются ВМЕСТЕ</b>, потому что приезжают из одного файла и по одному событию, — запустить
/// новый граф со старыми шаблонами нельзя. Сброс валовый, а не по одному макросу: правка любого
/// файла стоит одного лишнего чтения бандла на первый прогон каждого макроса, и это дешевле, чем
/// вторая копия правила «что именно поменялось».
///
/// <b>Идущий прогон сбросом не рвётся.</b> Источник, выданный на <c>RunAsync</c>, держит ссылку на
/// снимок; сброс подменяет запись в словаре, а снимок остаётся жить у своего прогона — та же
/// схема неизменяемого снимка, что у самого хранилища.
/// </summary>
public sealed partial class MacroTemplateCache : IDisposable
{
    private readonly MacroGraphStore _macros;
    private readonly ILogger<MacroTemplateCache> _logger;

    private ConcurrentDictionary<string, Lazy<MacroTemplateSnapshot>> _byMacro = NewMap();

    public MacroTemplateCache(MacroGraphStore macros, ILogger<MacroTemplateCache> logger)
    {
        _macros = macros;
        _logger = logger;
        _macros.MacrosChanged += OnMacrosChanged;
    }

    /// <summary>
    /// Источник шаблонов для одного прогона. Зовётся один раз, в <c>Orchestrator.RunAsync</c>;
    /// ничего не читает, пока у него не спросят первый шаблон.
    /// </summary>
    public IMacroTemplateSource For(string macroName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        return new Source(this, macroName);
    }

    /// <summary>Сколько макросов сейчас в кэше. Диагностика и тесты.</summary>
    public int CachedMacros => _byMacro.Count;

    public void Dispose() => _macros.MacrosChanged -= OnMacrosChanged;

    private void OnMacrosChanged()
    {
        // Подмена словаря целиком, а не Clear(): выданные источники продолжают держать свои
        // снимки, и прогон, идущий прямо сейчас, ничего не заметит.
        var dropped = Interlocked.Exchange(ref _byMacro, NewMap()).Count;
        if (dropped > 0)
        {
            LogDropped(dropped);
        }
    }

    private MacroTemplateSnapshot Snapshot(string macroName)
    {
        // ExecutionAndPublication (умолчание Lazy<T>) здесь нагружено смыслом: веер на десять окон
        // спрашивает шаблоны из десяти обходов одновременно, и открыть архив должен ровно один.
        var lazy = _byMacro.GetOrAdd(
            macroName,
            name => new Lazy<MacroTemplateSnapshot>(() => Load(name)));
        return lazy.Value;
    }

    private MacroTemplateSnapshot Load(string macroName)
    {
        if (_macros.TryGetEntry(macroName) is not { } entry)
        {
            LogMacroUnknown(macroName);
            return MacroTemplateSnapshot.Empty;
        }

        var snapshot = MacroTemplateSnapshot.FromBundle(MacroBundleReader.ReadAllTemplates(entry.Path));
        LogLoaded(macroName, snapshot.Count, entry.Path);
        return snapshot;
    }

    private static ConcurrentDictionary<string, Lazy<MacroTemplateSnapshot>> NewMap() =>
        new(StringComparer.Ordinal);

    // Тонкий держатель имени: сам по себе ничего не хранит, чтобы кэш оставался единственным
    // владельцем байтов, — но снимок берёт ОДИН РАЗ и запоминает, иначе сброс кэша посреди
    // прогона подменил бы шаблоны под работающим макросом.
    private sealed class Source(MacroTemplateCache cache, string macroName) : IMacroTemplateSource
    {
        private MacroTemplateSnapshot? _snapshot;

        public byte[]? TryGetTemplate(string templateName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
            return Snapshot().Singles.GetValueOrDefault(templateName);
        }

        public IReadOnlyDictionary<string, byte[]> GetSet(string setName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(setName);
            return Snapshot().Sets.GetValueOrDefault(setName) ?? MacroTemplateSnapshot.NoTemplates;
        }

        private MacroTemplateSnapshot Snapshot() => _snapshot ??= cache.Snapshot(macroName);
    }

    [LoggerMessage(LogLevel.Debug, "Шаблоны макроса '{Macro}' прочитаны в кэш: {Count} из {Path}")]
    partial void LogLoaded(string macro, int count, string path);

    [LoggerMessage(LogLevel.Warning,
        "Шаблоны запрошены у макроса '{Macro}', которого нет в библиотеке — ноды зрения не совпадут ни с чем")]
    partial void LogMacroUnknown(string macro);

    [LoggerMessage(LogLevel.Debug, "Библиотека изменилась — кэш шаблонов сброшен ({Count} макросов)")]
    partial void LogDropped(int count);
}

/// <summary>
/// Шаблоны одного бандла, разложенные по тем двум пространствам имён, которыми их называют ноды:
/// одиночные по имени и наборы по имени папки. Неизменяем — в этом весь смысл: выданный прогону,
/// он живёт ровно столько, сколько прогон, что бы ни случилось с кэшем.
/// </summary>
public sealed class MacroTemplateSnapshot
{
    /// <summary>Общий пустой словарь для несуществующих наборов — чтобы не выделять его на каждый промах.</summary>
    internal static readonly IReadOnlyDictionary<string, byte[]> NoTemplates =
        new Dictionary<string, byte[]>(StringComparer.Ordinal);

    private MacroTemplateSnapshot(
        IReadOnlyDictionary<string, byte[]> singles,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> sets,
        int count)
    {
        Singles = singles;
        Sets = sets;
        Count = count;
    }

    /// <summary>Снимок бандла, в котором шаблонов нет.</summary>
    public static MacroTemplateSnapshot Empty { get; } = new(
        NoTemplates,
        new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal),
        0);

    /// <summary>Одиночные шаблоны: имя → байты.</summary>
    public IReadOnlyDictionary<string, byte[]> Singles { get; }

    /// <summary>Наборы: имя набора → «тег → байты».</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> Sets { get; }

    /// <summary>Сколько всего файлов в снимке.</summary>
    public int Count { get; }

    /// <summary>
    /// Раскладывает плоский список записей бандла по двум пространствам имён. Правило разбора
    /// пути — общее с описью и каталогом (<see cref="MacroBundleFormat.TryParseTemplatePath"/>):
    /// именно поэтому «шаблон, который видно в браузере» и «шаблон, который найдёт исполнитель» —
    /// это по построению одно и то же множество.
    /// </summary>
    public static MacroTemplateSnapshot FromBundle(IReadOnlyList<MacroBundleFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var singles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sets = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        var count = 0;

        foreach (var file in files)
        {
            if (!MacroBundleFormat.TryParseTemplatePath(file.Path, out var set, out var name))
            {
                continue;
            }

            count++;
            if (set is null)
            {
                singles[name] = file.Bytes;
                continue;
            }

            if (!sets.TryGetValue(set, out var members))
            {
                members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                sets[set] = members;
            }

            members[name] = file.Bytes;
        }

        return new MacroTemplateSnapshot(
            singles,
            sets.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, byte[]>)pair.Value,
                StringComparer.Ordinal),
            count);
    }
}
