using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// Библиотека макросов на диске: по одному БАНДЛУ <c>.hsm</c> на макрос в папке <c>macros/</c> в
/// корне установки, где ОСНОВА ИМЕНИ ФАЙЛА и есть имя макроса.
///
/// <b>До волны F2 здесь лежали голые <c>*.json</c> с графом, а шаблоны жили одним общим деревом
/// <c>templates/</c>.</b> Связь между графом и его шаблонами при этом не была скреплена ничем:
/// отданный другому человеку файл молча не работал, потому что <c>templates/classes/Лучник.png</c>
/// был только у автора. Теперь макрос — это zip без сжатия, внутри которого лежит и граф, и его
/// собственные шаблоны (§13.1 спеки, <see cref="MacroBundleFormat"/>). Цена названа честно: два
/// макроса с распознаванием класса несут по своей копии одиннадцати PNG, и поправленный шаблон
/// приходится разносить руками.
///
/// За что отвечает:
///   * загрузить всё при создании, пропуская (а не падая на) непрочитавшиеся бандлы;
///   * CRUD через <see cref="SaveAsync"/> / <see cref="DeleteAsync"/> с проверкой имени по
///     правилам NTFS, плюс правку шаблонов внутри бандла;
///   * горячую перезагрузку через <see cref="FileSystemWatcher"/> с гашением дребезга, где
///     собственные записи подавляются сравнением подписи папки по временам последней записи.
///
/// ИНВАРИАНТ: ХРАНИЛИЩЕ НЕ СОЧИНЯЕТ СОДЕРЖИМОГО. Конструктор заводит саму папку, если её ещё нет
/// (иначе некуда класть первый макрос и не на что натравливать наблюдателя), читает её — и на
/// этом всё: библиотека сразу после создания хранилища ровно такая, какой её оставил
/// пользователь.
///
/// Так было НЕ ВСЕГДА, и потому это записано инвариантом, а не подразумевается. До отмены
/// обратной совместимости конструктор ещё и мигрировал унаследованный <c>macros.json</c>,
/// переименовывал его вместе с <c>hotkeys.json</c> в <c>*.migrated</c>, сеял шесть примеров
/// <c>pw-*</c> и ставил маркер <c>.examples-seeded</c>. То есть «создать объект» означало
/// «изменить состояние на диске»: тест не мог построить хранилище, не получив в придачу чужих
/// файлов, а пользователь не мог понять, откуда в его папке макросы, которых он не писал.
/// Побочные эффекты сюда не возвращать: если что-то нужно записать на старте, это отдельный
/// метод, видимый на месте вызова.
///
/// <b><c>*.json.incompatible</c> больше нет, и это тоже решение, а не пропажа.</b> Отодвигание
/// непарсимого файла в сторону появилось, когда ноды перешли на <c>Guid</c>: старые файлы
/// перестали разбираться ВСЕ РАЗОМ, и пользователь открыл бы панель с пустой библиотекой без
/// единого следа. У бандла эта беда снята форматом: <see cref="MacroBundleReader"/> не бросает и
/// различает «повреждён» и «сделан другой версией формата», а версия пишется в файл с первого
/// дня. Отодвинуть бандл БУДУЩЕЙ версии значило бы соврать ровно тем способом, ради недопущения
/// которого поле версии и заведено. Поэтому файл остаётся на месте, а причина уходит в журнал
/// целиком — и оба конца остаются у пользователя в руках.
///
/// Реализует <see cref="IMacroGraphResolver"/>, так что <c>RunMacroNode</c> разрешает
/// под-макросы прямо из живой библиотеки.
///
/// Параллелизм: записи выстраивает в очередь семафор; чтения идут через неизменяемый снимок
/// <see cref="All"/> и обходятся без блокировок.
/// </summary>
public sealed partial class MacroGraphStore : IMacroGraphResolver, IDisposable
{
    /// <summary>Имя папки с макросами относительно каталога приложения.</summary>
    public const string FolderName = "macros";

    private const int ReloadDebounceMs = 300;

    // Наблюдатель смотрит только на бандлы. Временный файл атомарной записи назван так, чтобы под
    // этот фильтр не попадать ни длинным именем («foo.hsm.tmp»), ни коротким 8.3
    // («FOOHSM~1.TMP»): 8.3 берёт первые три символа ПОСЛЕДНЕГО расширения — см.
    // MacroBundleWriter.
    private static readonly string BundleFilter = "*" + MacroBundleFormat.Extension;

    private readonly string _directory;
    private readonly ILogger<MacroGraphStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FileSystemWatcher? _watcher;

    private CancellationTokenSource? _pendingReload;
    private int _disposed;

    private ImmutableList<MacroLibraryEntry> _entries = [];
    private ImmutableList<MacroGraph> _macros = [];

    // Снимок «путь → время последней записи» на момент последней загрузки или записи, которую
    // выполнили МЫ. Перезагрузка, дождавшаяся конца дребезга и совпавшая с этой подписью, —
    // это либо эхо нашей же записи, либо дубль события, и она выбрасывается.
    private ImmutableDictionary<string, DateTime> _signature = ImmutableDictionary<string, DateTime>.Empty;

    /// <summary>
    /// Боевой конструктор: <c>macros/</c> в КОРНЕ УСТАНОВКИ. В поставке это папка уровнем выше
    /// демона (он сам лежит в <c>daemon\</c>), в дереве разработки — его собственная;
    /// см. <see cref="InstallationLayout"/>.
    /// </summary>
    public MacroGraphStore(ILogger<MacroGraphStore> logger)
        : this(InstallationLayout.RootFromDaemonDirectory(AppContext.BaseDirectory), logger)
    {
    }

    /// <param name="baseDirectory">Папка, в которой лежит (или появится) <c>macros/</c>.</param>
    /// <param name="logger">Приёмник диагностики.</param>
    public MacroGraphStore(string baseDirectory, ILogger<MacroGraphStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _logger = logger;
        _directory = Path.Combine(baseDirectory, FolderName);

        // Единственное обращение к диску на запись за весь конструктор — и то это папка, а не
        // её содержимое. См. инвариант в комментарии класса.
        Directory.CreateDirectory(_directory);
        Reload(raiseEvent: false);

        // Наблюдатель ставится по возможности: горячая перезагрузка — приятное дополнение, так
        // что сбои прав или платформы деградируют до «перезапустите, чтобы подхватить внешние
        // правки», а не до падения.
        try
        {
            _watcher = new FileSystemWatcher(_directory, BundleFilter)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
        catch (Exception ex)
        {
            LogWatcherStartFailed(ex, _directory);
        }
    }

    /// <summary>Поднимается после изменения библиотеки — сохранения, удаления или внешней правки.</summary>
    public event Action<IReadOnlyList<MacroGraph>>? MacrosChanged;

    /// <summary>Абсолютный путь к папке с макросами.</summary>
    public string FolderPath => _directory;

    /// <summary>Текущий неизменяемый снимок библиотеки, упорядоченный по имени.</summary>
    public IReadOnlyList<MacroGraph> All => _macros;

    /// <summary>
    /// Тот же снимок, но целыми бандлами: граф плюс паспорт плюс опись шаблонов. Нужен тем, кто
    /// спрашивает про ФАЙЛ, а не про граф, — валидации при сохранении, диагностике и браузеру
    /// шаблонов.
    /// </summary>
    public IReadOnlyList<MacroLibraryEntry> Entries => _entries;

    /// <inheritdoc />
    public MacroGraph? TryGet(string name) => TryGetEntry(name)?.Graph;

    /// <summary>Запись библиотеки по имени или <c>null</c>.</summary>
    public MacroLibraryEntry? TryGetEntry(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Пишет <paramref name="graph"/> в <c>macros/{Name}.hsm</c> и поднимает
    /// <see cref="MacrosChanged"/>.
    ///
    /// <b>Пишется бандл целиком, а меняется в нём только граф.</b> Шаблоны, под-макросы и паспорт
    /// (в том числе <see cref="MacroBundleMetadata.Id"/> и дата создания) читаются из
    /// существующего файла и кладутся обратно; меняется лишь дата правки. Иначе сохранение графа
    /// стирало бы шаблоны — то есть ровно то, ради чего бандл и заведён.
    /// </summary>
    /// <param name="graph">Сохраняемый граф; его имя становится основой имени файла.</param>
    /// <param name="renamedFrom">
    /// Прежнее имя, если это переименование.
    ///
    /// Без него переименование теряло бы шаблоны: редактор переименовывает записью под новым
    /// именем и удалением старого файла (именно в таком порядке — сбой между шагами обязан
    /// оставить две копии, а не ноль), а под новым именем бандла ещё нет, и наследовать вложения
    /// не от чего. Паспорт при переименовании тоже переезжает целиком: <c>Id</c> не меняется
    /// никогда, а переименование — это не дублирование.
    /// </param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <exception cref="ArgumentException">Имя графа не годится в качестве имени файла.</exception>
    public async Task SaveAsync(MacroGraph graph, string? renamedFrom = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (ValidateName(graph.Name) is { } nameError)
        {
            throw new ArgumentException(nameError, nameof(graph));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(graph.Name);
            var previous = ReadContentOrNull(path)
                           ?? (renamedFrom is null ? null : ReadContentOrNull(PathFor(renamedFrom)));

            var content = new MacroBundleContent
            {
                Metadata = previous?.Metadata.Touch() ?? MacroBundleMetadata.CreateNew(graph.Name),
                Graph = graph,
                Templates = previous?.Templates ?? [],
                Submacros = previous?.Submacros ?? [],
            };

            // Ошибки сохранению не мешают — редактор обязан уметь сохранить недоделанный граф, —
            // но они громкие, потому что исполнитель оборвёт прогон, который дойдёт до сломанного
            // места. Проверяем ПОСЛЕ того, как собрали содержимое: опись шаблонов берётся из того
            // бандла, который сейчас ляжет на диск, а не из того, что лежал раньше.
            var inventory = MacroTemplateInventory.FromPaths(content.Templates.Select(file => file.Path));
            foreach (var issue in MacroGraphValidator.Validate(graph, inventory))
            {
                if (issue.Severity == ValidationSeverity.Error)
                {
                    LogValidationError(graph.Name, issue.NodeName ?? "(граф)", issue.Message);
                }
            }

            MacroBundleWriter.Write(path, content);
            LogSaved(graph.Name, path, content.Templates.Count);
            Reload(raiseEvent: false);
        }
        finally
        {
            _writeLock.Release();
        }

        MacrosChanged?.Invoke(_macros);
    }

    /// <summary>
    /// Удаляет <c>macros/{name}.hsm</c> и поднимает <see cref="MacrosChanged"/>.
    /// </summary>
    /// <param name="name">Имя удаляемого макроса.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><c>false</c>, если такого файла нет (события не будет).</returns>
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (ValidateName(name) is not null)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            LogDeleted(name, path);
            Reload(raiseEvent: false);
        }
        finally
        {
            _writeLock.Release();
        }

        MacrosChanged?.Invoke(_macros);
        return true;
    }

    // ------------------------------------------------------------------ шаблоны бандла

    /// <summary>
    /// Кладёт шаблон в бандл макроса, заменяя одноимённый.
    ///
    /// Бандл при этом переписывается целиком — иначе не выйдет атомарной замены, — так что цена
    /// добавления одного PNG равна цене сохранения макроса. Для файлов в десятки килобайт это
    /// ничто, а взамен «добавили шаблон» ничем не отличается от «сохранили граф»: тот же
    /// временный файл, то же переименование, то же событие.
    /// </summary>
    /// <param name="macroName">Макрос, которому принадлежит шаблон.</param>
    /// <param name="set">Набор (подпапка) либо <c>null</c> — одиночный шаблон.</param>
    /// <param name="templateName">Имя шаблона = основа имени файла; для набора это тег.</param>
    /// <param name="png">Содержимое файла.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><c>false</c>, если такого макроса нет или его бандл не читается.</returns>
    public Task<bool> AddTemplateAsync(string macroName, string? set, string templateName, byte[] png,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        ArgumentNullException.ThrowIfNull(png);

        var relativePath = MacroBundleFormat.TemplatePath(set, templateName);
        if (!MacroBundleFormat.TryParseTemplatePath(relativePath, out _, out _))
        {
            return Task.FromResult(false);
        }

        return EditBundleAsync(macroName, content =>
        {
            var templates = content.Templates
                .Where(file => !SamePath(file.Path, relativePath))
                .Append(new MacroBundleFile(relativePath, png))
                .ToList();
            LogTemplateAdded(macroName, relativePath, png.Length);
            return content with { Templates = templates };
        }, cancellationToken);
    }

    /// <summary>Убирает шаблон из бандла макроса. <c>false</c> — такого шаблона там нет.</summary>
    public Task<bool> DeleteTemplateAsync(string macroName, string? set, string templateName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);

        var relativePath = MacroBundleFormat.TemplatePath(set, templateName);
        return EditBundleAsync(macroName, content =>
        {
            var templates = content.Templates.Where(file => !SamePath(file.Path, relativePath)).ToList();
            if (templates.Count == content.Templates.Count)
            {
                return null;
            }

            LogTemplateDeleted(macroName, relativePath);
            return content with { Templates = templates };
        }, cancellationToken);
    }

    /// <summary>
    /// Перечень шаблонов макроса для браузера в редакторе: пути, размеры, вес — без байтов.
    ///
    /// <b>Это ВТОРАЯ дорога чтения, и она намеренно ходит на диск каждый раз</b>, минуя кэш
    /// исполнителя (<c>MacroTemplateCache</c>). Довод тот же, что был у общего дерева: браузер
    /// существует ровно затем, чтобы показать, что в бандле лежит СЕЙЧАС, — а список, отвечающий
    /// снимком из кэша, был бы для этого бесполезен.
    /// </summary>
    public IReadOnlyList<MacroBundleTemplateInfo> TemplateCatalog(string macroName) =>
        TryGetEntry(macroName) is { } entry ? MacroBundleReader.ReadTemplateCatalog(entry.Path) : [];

    /// <summary>Байты одного шаблона для превью — с диска, минуя кэш исполнителя.</summary>
    /// <returns><c>null</c>, если макроса, шаблона или файла нет.</returns>
    public byte[]? ReadTemplate(string macroName, string? set, string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName) || TryGetEntry(macroName) is not { } entry)
        {
            return null;
        }

        var relativePath = MacroBundleFormat.TemplatePath(set, templateName);
        // Сегменты пути в имени — это попытка вычитать что-то за пределами templates/, а не
        // шаблон, которого не хватает; разбор их не пропустит.
        return MacroBundleFormat.TryParseTemplatePath(relativePath, out _, out _)
            ? MacroBundleReader.ReadTemplate(entry.Path, relativePath)
            : null;
    }

    /// <summary>
    /// Проверяет имя макроса по правилам имён файлов NTFS (имя И ЕСТЬ основа имени файла).
    /// </summary>
    /// <param name="name">Проверяемое имя.</param>
    /// <returns>Описание ошибки или <c>null</c>, если имя годится.</returns>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Имя макроса не может быть пустым.";
        }

        if (name.Length > 100)
        {
            return "Имя макроса должно быть не длиннее 100 символов.";
        }

        var invalid = name.IndexOfAny(Path.GetInvalidFileNameChars());
        if (invalid >= 0)
        {
            return $"Имя макроса не может содержать «{name[invalid]}».";
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "Имя макроса не может заканчиваться точкой или пробелом.";
        }

        if (IsReservedDeviceName(name))
        {
            return $"«{name}» — зарезервированное имя устройства Windows.";
        }

        return null;
    }

    private static bool IsReservedDeviceName(string name)
    {
        // CON, PRN, AUX, NUL, COM0-9, LPT0-9 — в Windows негодны как основы имён файлов.
        var stem = name.Split('.')[0];
        if (stem is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && char.IsAsciiDigit(stem[3]);
    }

    // NTFS регистр не различает, так что «Лучник.png» и «лучник.png» — это не два шаблона.
    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private string PathFor(string name) => Path.Combine(_directory, name + MacroBundleFormat.Extension);

    private static MacroBundleContent? ReadContentOrNull(string path) =>
        File.Exists(path) ? MacroBundleReader.ReadContent(path) : null;

    // Общий ход всякой правки бандла: прочитать всё → поменять одно → записать всё. Писатель умеет
    // только «файл целиком», и это не ограничение, а условие атомарности: заменить переименованием
    // можно только готовый файл.
    private async Task<bool> EditBundleAsync(string macroName, Func<MacroBundleContent, MacroBundleContent?> edit,
        CancellationToken cancellationToken)
    {
        if (ValidateName(macroName) is not null)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(macroName);
            if (ReadContentOrNull(path) is not { } content)
            {
                LogBundleNotRewritable(macroName);
                return false;
            }

            if (edit(content) is not { } updated)
            {
                return false;
            }

            MacroBundleWriter.Write(path, updated with { Metadata = updated.Metadata.Touch() });
            Reload(raiseEvent: false);
        }
        finally
        {
            _writeLock.Release();
        }

        MacrosChanged?.Invoke(_macros);
        return true;
    }

    // Полное перечитывание папки. Не бросает никогда: бандл, который не прочитался, попадает в
    // лог и пропускается, чтобы одна кривая правка руками не опустошила библиотеку.
    private void Reload(bool raiseEvent)
    {
        var entries = new List<MacroLibraryEntry>();
        var signature = ImmutableDictionary.CreateBuilder<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var path in EnumerateFilesSafe())
        {
            try
            {
                signature[path] = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                // Файл пишут прямо сейчас либо его удалили между перечислением и опросом
                // атрибутов; следующее событие перечитает.
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            var read = MacroBundleReader.Read(path);
            if (read is not { IsOk: true, Graph: not null, Metadata.Metadata: not null })
            {
                // Читатель не бросает и различает «повреждён», «занят», «сделан другой версией
                // формата» — поэтому здесь достаточно перенести его вердикт в журнал целиком.
                // Файл при этом НЕ трогаем; см. про *.incompatible в комментарии класса.
                var fault = read.Metadata.IsOk ? read.GraphFault : read.Metadata.Fault;
                var message = (read.Metadata.IsOk ? read.GraphMessage : read.Metadata.Message) ?? "(без подробностей)";
                LogBundleSkipped(path, fault.ToString(), message);
                skipped++;
                continue;
            }

            var graph = read.Graph;
            if (!string.Equals(graph.Name, stem, StringComparison.Ordinal))
            {
                // Главенствует имя файла — переименовали файл, значит переименовали макрос.
                LogNameMismatch(graph.Name, stem);
                graph = new MacroGraph
                {
                    Name = stem,
                    Triggers = graph.Triggers,
                    StartNodeId = graph.StartNodeId,
                    Nodes = graph.Nodes,
                };
            }

            entries.Add(new MacroLibraryEntry(stem, graph, read.Metadata.Metadata, read.TemplatePaths, path));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        _entries = [.. entries];
        _macros = [.. entries.Select(entry => entry.Graph)];
        _signature = signature.ToImmutable();
        LogLoaded(entries.Count, skipped, _directory);

        if (raiseEvent)
        {
            MacrosChanged?.Invoke(_macros);
        }
    }

    private IEnumerable<string> EnumerateFilesSafe()
    {
        try
        {
            return Directory.EnumerateFiles(_directory, BundleFilter)
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEnumerationFailed(ex, _directory);
            return [];
        }
    }

    // FileSystemWatcher стреляет с пула потоков, а редакторы выдают по нескольку событий на одно
    // сохранение, поэтому весь всплеск внутри окна гашения дребезга схлопывается в одну проверку
    // на перезагрузку.
    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pendingReload, cts);
        previous?.Cancel();
        previous?.Dispose();
        _ = ReloadAfterDelayAsync(cts.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReloadDebounceMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // вытеснено более свежим событием
        }

        var changed = false;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!HasFolderChanged())
            {
                // Эхо нашей же записи или дубль события — делать нечего.
                return;
            }

            Reload(raiseEvent: false);
            changed = true;
            LogReloadedExternally(_macros.Count);
        }
        catch (Exception ex)
        {
            LogReloadFailed(ex);
        }
        finally
        {
            _writeLock.Release();
        }

        // Поднимается снаружи блокировки, чтобы подписчики могли вызывать нас обратно без
        // взаимной блокировки.
        if (changed)
        {
            MacrosChanged?.Invoke(_macros);
        }
    }

    private bool HasFolderChanged()
    {
        var current = _signature;
        var seen = 0;
        foreach (var path in EnumerateFilesSafe())
        {
            seen++;
            if (!current.TryGetValue(path, out var known))
            {
                return true;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(path) != known)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
        }

        return seen != current.Count;
    }

    // Идемпотентно — и обязано таким быть: хранилище зарегистрировано дважды (само по себе и как
    // IMacroGraphResolver через фабрику), поэтому область DI держит ОДИН И ТОТ ЖЕ экземпляр в
    // своём списке освобождаемых дважды и при выключении вызывает этот метод тоже дважды.
    // Раньше второй вызов добирался до уже освобождённого _pendingReload и выбрасывал
    // ObjectDisposedException прямо из сноса хоста — а наружу это выглядело как «демон
    // SmartMacro завершился неожиданно» и ненулевой код возврата, причём только в тех сеансах,
    // где наблюдатель действительно срабатывал (у нетронутой папки macros/ _pendingReload
    // остаётся null, и исключения не видно). Забирая поле через Interlocked, мы заодно
    // закрываем гонку с обратным вызовом наблюдателя, проскочившим, пока мы сносились.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
        }

        var pending = Interlocked.Exchange(ref _pendingReload, null);
        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Параллельный OnFileChanged вытеснил и освободил его между нашим чтением и
            // вызовом Cancel. Значит, отменять уже нечего.
        }

        pending?.Dispose();

        _writeLock.Dispose();
    }
}
