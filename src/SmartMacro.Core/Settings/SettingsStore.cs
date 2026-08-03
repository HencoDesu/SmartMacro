using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Settings;

namespace SmartMacro.Settings;

/// <summary>
/// Настройки на диске: один файл <c>settings.json</c> рядом с исполняемым файлом.
///
/// Устроено ровно как <c>MacroGraphStore</c>, и это не сходство, а требование: макросы уже
/// доказали, что схема «демон владеет файлом → панель правит по IPC → демон поднимает событие →
/// подписчики перечитывают» работает и переживает правку блокнотом. Настройки получают ту же —
/// загрузка при создании, неизменяемый снимок, запись под семафором, наблюдатель за файлом с
/// гашением дребезга и подавлением эха собственных записей по времени последней записи.
///
/// <b>ОДНО НАМЕРЕННОЕ ОТЛИЧИЕ: этот конструктор ПИШЕТ, если файла нет.</b> У
/// <c>MacroGraphStore</c> обратное записано инвариантом, и не по недосмотру: там побочный эффект
/// в конструкторе означал бы, что «создать объект» = «положить пользователю чужие макросы».
/// Здесь наоборот. Демон обязан работать без панели — панель по требованию, её может не быть
/// сутками, — а значит, первый запуск на новой машине не имеет права требовать «откройте панель
/// и нажмите Сохранить». Файл с разумными умолчаниями создаёт сам демон, и делает это ровно один
/// раз, только когда файла нет; существующий файл не переписывается никогда, даже если он
/// нечитаем (см. <see cref="Load"/>).
///
/// <b>Наблюдатель за файлом — не удобство, а следствие кнопки «Открыть папку».</b> Раскладка
/// портативная, папка одна, и пользователя туда прямо приглашают. Правка блокнотом обязана
/// подхватываться, а не теряться при следующем сохранении из панели.
///
/// Уровня журнала здесь нет и не будет: он остаётся в <c>appsettings.json</c>, чтобы падение на
/// чтении ЭТОГО файла можно было отладить тем уровнем, что был известен до. См.
/// <see cref="AppSettings"/>.
/// </summary>
public sealed partial class SettingsStore : ISettingsSource, IDisposable
{
    /// <summary>Имя файла настроек относительно каталога приложения.</summary>
    public const string FileName = "settings.json";

    private const int ReloadDebounceMs = 300;

    private readonly string _path;
    private readonly string _folder;
    private readonly ILogger<SettingsStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FileSystemWatcher? _watcher;

    private CancellationTokenSource? _pendingReload;
    private int _disposed;

    // Время последней записи файла на момент последней загрузки или НАШЕЙ записи. Событие
    // наблюдателя, дождавшееся конца дребезга и совпавшее с этой отметкой, — эхо нас самих.
    private DateTime _signature;

    private volatile AppSettings _current = AppSettings.Default;

    /// <summary>Боевой конструктор: <c>settings.json</c> рядом с исполняемым файлом.</summary>
    public SettingsStore(ILogger<SettingsStore> logger)
        : this(AppContext.BaseDirectory, logger)
    {
    }

    /// <param name="baseDirectory">Папка, в которой лежит (или появится) файл настроек.</param>
    /// <param name="logger">Приёмник диагностики.</param>
    public SettingsStore(string baseDirectory, ILogger<SettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _logger = logger;
        _folder = baseDirectory;
        _path = Path.Combine(baseDirectory, FileName);

        Load();

        // Наблюдатель ставится по возможности: горячая перезагрузка — приятное дополнение, так
        // что сбои прав или платформы деградируют до «перезапустите, чтобы подхватить внешние
        // правки», а не до падения. Фильтр по имени файла, а не по маске: в этой папке лежат ещё
        // appsettings.json и логи, и будить перезагрузку на каждый их байт незачем.
        try
        {
            _watcher = new FileSystemWatcher(baseDirectory, FileName)
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
            LogWatcherStartFailed(ex, baseDirectory);
        }
    }

    /// <inheritdoc />
    public event Action<AppSettings>? SettingsChanged;

    /// <inheritdoc />
    public AppSettings Current => _current;

    /// <summary>Полный путь к файлу настроек.</summary>
    public string FilePath => _path;

    /// <summary>Папка, в которой лежат файл настроек, макросы и журнал, — её открывает кнопка в панели.</summary>
    public string FolderPath => _folder;

    /// <summary>
    /// Проверяет и записывает настройки, затем поднимает <see cref="SettingsChanged"/>.
    /// </summary>
    /// <param name="settings">Новый снимок.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>
    /// Пустой список — записано. Непустой — НЕ записано, и он несёт причины: договорённость та
    /// же, что у <c>SaveMacro</c>.
    /// </returns>
    public async Task<IReadOnlyList<SettingsIssue>> SaveAsync(AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var issues = AppSettingsValidator.Validate(settings);
        if (issues.Count > 0)
        {
            LogSaveRejected(issues.Count, issues[0].Message);
            return issues;
        }

        await WriteAsync(settings, cancellationToken).ConfigureAwait(false);
        LogSaved(_path);
        SettingsChanged?.Invoke(_current);
        return [];
    }

    /// <summary>
    /// Возвращает файл к умолчаниям и поднимает <see cref="SettingsChanged"/>.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Записанный снимок.</returns>
    public async Task<AppSettings> ResetAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(AppSettings.Default, cancellationToken).ConfigureAwait(false);
        LogReset(_path);
        SettingsChanged?.Invoke(_current);
        return _current;
    }

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

        // Тот же приём, что в MacroGraphStore: забрать поле через Interlocked, чтобы второй
        // Dispose (объект зарегистрирован в DI дважды — сам по себе и как ISettingsSource) и
        // проскочивший обратный вызов наблюдателя не добрались до уже освобождённого источника.
        var pending = Interlocked.Exchange(ref _pendingReload, null);
        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Параллельный OnFileChanged вытеснил и освободил его между чтением и Cancel.
        }

        pending?.Dispose();
        _writeLock.Dispose();
    }

    // ---- диск -------------------------------------------------------------------------------

    // Чтение файла. Не бросает никогда — демон обязан подняться при любом содержимом папки.
    //
    // Три исхода:
    //   * файла нет      → умолчания + ЗАПИСЬ (единственное место, где мы создаём файл сами);
    //   * файл читается  → снимок из него;
    //   * файл не читается → ОСТАВЛЯЕМ ПРЕЖНИЙ снимок и громко пишем в лог. Именно оставляем, а
    //     не откатываем к умолчаниям и уж точно не переписываем: пользователь правил файл
    //     блокнотом и поставил лишнюю запятую, и «программа молча вернула всё к заводскому»
    //     здесь — потеря его работы. Он чинит запятую, наблюдатель перечитывает.
    private void Load()
    {
        if (!File.Exists(_path))
        {
            _current = AppSettings.Default;
            LogMissingFileSeeded(_path);
            TryWriteFile(AppSettings.Default);
            return;
        }

        try
        {
            var text = File.ReadAllText(_path);
            _current = AppSettingsJson.Deserialize(text);
            _signature = File.GetLastWriteTimeUtc(_path);
            LogLoaded(_current.Profiles.Count, _path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogUnreadable(ex, _path);
        }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(_path, AppSettingsJson.Serialize(settings), cancellationToken)
                .ConfigureAwait(false);
            _current = settings;
            _signature = File.GetLastWriteTimeUtc(_path);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Синхронная запись для конструктора: он не может ждать. По возможности — не подняться
    // из-за того, что папку не дали на запись, было бы хуже, чем работать на умолчаниях.
    private void TryWriteFile(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, AppSettingsJson.Serialize(settings));
            _signature = File.GetLastWriteTimeUtc(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSeedFailed(ex, _path);
        }
    }

    // ---- наблюдатель ------------------------------------------------------------------------

    // FileSystemWatcher стреляет с пула потоков, а редакторы выдают по нескольку событий на одно
    // сохранение, поэтому весь всплеск внутри окна гашения дребезга схлопывается в одну проверку.
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
            if (!HasFileChanged())
            {
                // Эхо нашей же записи или дубль события — делать нечего.
                return;
            }

            Load();
            changed = true;
            LogReloadedExternally(_path);
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
            SettingsChanged?.Invoke(_current);
        }
    }

    private bool HasFileChanged()
    {
        try
        {
            // Файл удалили руками — это тоже изменение: Load() пересоздаст его с умолчаниями.
            return !File.Exists(_path) || File.GetLastWriteTimeUtc(_path) != _signature;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
