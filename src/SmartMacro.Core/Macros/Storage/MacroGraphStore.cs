using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// Библиотека макросов на диске: по одному JSON-файлу на граф в папке <c>macros/</c> рядом с
/// исполняемым файлом, где ОСНОВА ИМЕНИ ФАЙЛА и есть имя макроса. Файл на макрос (вместо
/// прежнего единого <c>macros.json</c>) — чтобы править руками, смотреть диффом и делиться
/// отдельным макросом было естественными действиями.
///
/// За что отвечает:
///   * загрузить всё при создании, пропуская (а не падая на) нечитаемые файлы;
///   * CRUD через <see cref="SaveAsync"/> / <see cref="DeleteAsync"/> с проверкой имени по
///     правилам NTFS;
///   * горячую перезагрузку через <see cref="FileSystemWatcher"/> с гашением дребезга, где
///     собственные записи подавляются сравнением подписи папки по временам последней записи.
///
/// ИНВАРИАНТ: КОНСТРУКТОР НЕ ПИШЕТ НИ ОДНОГО ФАЙЛА. Он заводит саму папку, если её ещё нет
/// (иначе некуда класть первый макрос и не на что натравливать наблюдателя), читает её — и на
/// этом всё: содержимое библиотеки сразу после создания хранилища ровно такое, каким его
/// оставил пользователь.
///
/// Так было НЕ ВСЕГДА, и потому это записано инвариантом, а не подразумевается. До отмены
/// обратной совместимости конструктор ещё и мигрировал унаследованный <c>macros.json</c>,
/// переименовывал его вместе с <c>hotkeys.json</c> в <c>*.migrated</c>, сеял шесть примеров
/// <c>pw-*</c> и ставил маркер <c>.examples-seeded</c>. То есть «создать объект» означало
/// «изменить состояние на диске»: тест не мог построить хранилище, не получив в придачу чужих
/// файлов, а пользователь не мог понять, откуда в его папке макросы, которых он не писал.
/// Примеры теперь раздаются файлами (<c>examples/</c> рядом с демоном) и копируются руками, а
/// мигрировать больше нечего. Побочные эффекты сюда не возвращать: если что-то нужно записать
/// на старте, это отдельный метод, видимый на месте вызова.
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

    private readonly string _directory;
    private readonly ILogger<MacroGraphStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FileSystemWatcher? _watcher;

    private CancellationTokenSource? _pendingReload;
    private int _disposed;

    private ImmutableList<MacroGraph> _macros = [];

    // Снимок «путь → время последней записи» на момент последней загрузки или записи, которую
    // выполнили МЫ. Перезагрузка, дождавшаяся конца дребезга и совпавшая с этой подписью, —
    // это либо эхо нашей же записи, либо дубль события, и она выбрасывается.
    private ImmutableDictionary<string, DateTime> _signature = ImmutableDictionary<string, DateTime>.Empty;

    /// <summary>Боевой конструктор: <c>macros/</c> рядом с исполняемым файлом.</summary>
    public MacroGraphStore(ILogger<MacroGraphStore> logger)
        : this(AppContext.BaseDirectory, logger)
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
            _watcher = new FileSystemWatcher(_directory, "*.json")
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

    /// <inheritdoc />
    public MacroGraph? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var macro in _macros)
        {
            if (string.Equals(macro.Name, name, StringComparison.Ordinal))
            {
                return macro;
            }
        }

        return null;
    }

    /// <summary>
    /// Пишет <paramref name="graph"/> в <c>macros/{Name}.json</c>, заменяя любой существующий
    /// файл с этим именем, и поднимает <see cref="MacrosChanged"/>.
    /// </summary>
    /// <param name="graph">Сохраняемый граф; его имя становится основой имени файла.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <exception cref="ArgumentException">Имя графа не годится в качестве имени файла.</exception>
    public async Task SaveAsync(MacroGraph graph, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (ValidateName(graph.Name) is { } nameError)
        {
            throw new ArgumentException(nameError, nameof(graph));
        }

        // Ошибки сохранению не мешают — редактор обязан уметь сохранить недоделанный граф, — но
        // они громкие, потому что исполнитель оборвёт прогон, который дойдёт до сломанного
        // места.
        foreach (var issue in MacroGraphValidator.Validate(graph))
        {
            if (issue.Severity == ValidationSeverity.Error)
            {
                LogValidationError(graph.Name, issue.NodeId ?? "(граф)", issue.Message);
            }
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(graph.Name);
            await File.WriteAllTextAsync(path, MacroGraphJson.Serialize(graph), cancellationToken)
                .ConfigureAwait(false);
            LogSaved(graph.Name, path);
            Reload(raiseEvent: false);
        }
        finally
        {
            _writeLock.Release();
        }

        MacrosChanged?.Invoke(_macros);
    }

    /// <summary>
    /// Удаляет <c>macros/{name}.json</c> и поднимает <see cref="MacrosChanged"/>.
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

    private string PathFor(string name) => Path.Combine(_directory, $"{name}.json");

    // Полное перечитывание папки. Не бросает никогда: файл, который не разобрался, попадает в
    // лог и пропускается, чтобы одна кривая правка руками не опустошила библиотеку.
    private void Reload(bool raiseEvent)
    {
        var macros = new List<MacroGraph>();
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
            try
            {
                var graph = MacroGraphJson.Deserialize(File.ReadAllText(path));
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

                macros.Add(graph);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                LogFileSkipped(ex, path);
                skipped++;
            }
        }

        macros.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        _macros = [.. macros];
        _signature = signature.ToImmutable();
        LogLoaded(macros.Count, skipped, _directory);

        if (raiseEvent)
        {
            MacrosChanged?.Invoke(_macros);
        }
    }

    private IEnumerable<string> EnumerateFilesSafe()
    {
        try
        {
            return Directory.EnumerateFiles(_directory, "*.json")
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
