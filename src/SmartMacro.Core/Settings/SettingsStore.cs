using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Contracts.Settings;
using SmartMacro.Io;
using SmartMacro.Resources;

namespace SmartMacro.Settings;

/// <summary>
/// Настройки на диске: один файл <c>settings.json</c> в КОРНЕ УСТАНОВКИ — в поставке это папка
/// уровнем выше демона, рядом с панелью, и совпадает с его собственной только в дереве
/// разработки.
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
/// ⚠️ <b>«Нечитаемый файл не переписывается» — обещание хранилища, и одного его МАЛО.</b>
/// Хранилище и правда не пишет, но следующая же команда панели пишет: пользователь видел форму с
/// умолчаниями, считал, что настройки сбросились, жал «Применить» — и чинимый одной запятой файл
/// исчезал навсегда. Поэтому нечитаемость теперь не только пишется в журнал, но и ЕЗДИТ НАРУЖУ
/// (<see cref="Fault"/> → <c>SettingsSnapshotDto</c> → полоса вверху экрана настроек). Блокировать
/// «Применить» при этом НЕ надо: перезаписать сломанный файл — законное намерение, опасна была
/// тихая потеря, а не сама возможность.
///
/// <b>Запись АТОМАРНА</b> — <see cref="AtomicFile"/>, тот же механизм, что у бандла: временный
/// файл рядом, затем <c>ReplaceFile</c>. До этого здесь стоял <c>File.WriteAllTextAsync</c>, то
/// есть усечение и запись на месте: демон, убитый посреди записи, оставлял половину JSON — а
/// половина JSON нечитаема, и все настройки пользователя превращались в те самые умолчания.
///
/// ⚠️ Имя временного файла (<c>settings.json.tmp</c>) обязано промахиваться мимо фильтра
/// наблюдателя, а фильтр здесь — САМО ИМЯ ФАЙЛА, не маска. Промахивается: ни длинное
/// <c>settings.json.tmp</c>, ни короткое 8.3 <c>SETTIN~1.TMP</c> с <c>settings.json</c> не
/// совпадают.
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

    // Вердикт последней НЕУДАВШЕЙСЯ загрузки; null — снимок и есть содержимое файла.
    private volatile string? _fault;

    /// <summary>
    /// Боевой конструктор: <c>settings.json</c> в КОРНЕ УСТАНОВКИ. В поставке это папка уровнем
    /// выше демона (он сам лежит в <c>daemon\</c>), в дереве разработки — его собственная;
    /// см. <see cref="InstallationLayout"/>.
    /// </summary>
    public SettingsStore(ILogger<SettingsStore> logger)
        : this(InstallationLayout.RootFromDaemonDirectory(AppContext.BaseDirectory), logger)
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
        // правки», а не до падения. Фильтр по имени файла, а не по маске: в корне установки
        // лежат ещё макросы, шаблоны и журналы, и будить перезагрузку на каждый их байт незачем.
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

    /// <summary>
    /// Почему файл настроек НЕ прочитан, — или <c>null</c>, если <see cref="Current"/> и есть его
    /// содержимое.
    ///
    /// Существует затем, чтобы «файл не прочитан» доехало до человека, а не осталось строкой в
    /// журнале, которую никто не открывает. Пока этого поля не было, панель показывала умолчания
    /// (или последний удачный снимок) как содержимое файла, и «Применить» уничтожало файл, который
    /// чинился одной запятой; довод целиком — в шапке класса.
    ///
    /// Не флаг, а вердикт: «повреждён» и «занят другим процессом» требуют от пользователя разных
    /// действий, ровно как у <c>MacroBundleFault</c>.
    /// </summary>
    public string? Fault => _fault;

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
    //   * файл не читается → ОСТАВЛЯЕМ ПРЕЖНИЙ снимок, громко пишем в лог И ВЫСТАВЛЯЕМ Fault.
    //     Именно оставляем, а не откатываем к умолчаниям и уж точно не переписываем: пользователь
    //     правил файл блокнотом и поставил лишнюю запятую, и «программа молча вернула всё к
    //     заводскому» здесь — потеря его работы. Он чинит запятую, наблюдатель перечитывает.
    //     Fault при этом — не украшение: без него панель показывала бы не содержимое файла как
    //     содержимое файла, и «Применить» доделало бы то, от чего мы здесь воздержались.
    private void Load()
    {
        if (!File.Exists(_path))
        {
            _current = AppSettings.Default;
            _fault = null;
            LogMissingFileSeeded(_path);
            TryWriteFile(AppSettings.Default);
            return;
        }

        try
        {
            var text = File.ReadAllText(_path);
            _current = AppSettingsJson.Deserialize(text, out var adoptedHooks);
            _signature = File.GetLastWriteTimeUtc(_path);
            _fault = null;
            LogLoaded(_current.Profiles.Count, _path);

            // Молчаливая миграция здесь недопустима: она меняет смысл файла, который пользователь
            // правил руками, — и как раз тем, что ФАЙЛ ПРИ ЭТОМ НЕ ТРОГАЕТ. Не сказав об этом,
            // мы оставили бы человека с настройками, которых он в файле не видит.
            if (adoptedHooks > 0)
            {
                LogLegacyHooksAdopted(_path, adoptedHooks);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _fault = string.Format(CultureInfo.CurrentCulture, Strings.Settings_File_Unreadable, ex.Message);
            LogUnreadable(ex, _path);
        }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteAllTextAsync(_path, AppSettingsJson.Serialize(settings), cancellationToken)
                .ConfigureAwait(false);
            _current = settings;
            _signature = File.GetLastWriteTimeUtc(_path);
            // Файл теперь наш и читается: держать прежний вердикт значило бы предупреждать о
            // потере, которая уже случилась по воле пользователя.
            _fault = null;
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
            AtomicFile.WriteAllText(_path, AppSettingsJson.Serialize(settings));
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
