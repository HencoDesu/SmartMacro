using Serilog;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;

namespace SmartMacro.App.Macros;

/// <summary>
/// Библиотека макросов глазами ПАНЕЛИ: папка <c>macros/</c> в корне установки, по одному бандлу
/// <c>.hsm</c> на макрос.
///
/// <b>С волны F3 панель — единственный автор изменений в макросах, а демон только читает и
/// выполняет.</b> Редактор живёт здесь, значит и писать должен тот, у кого он есть; процессы делят
/// одну файловую систему, поэтому макрос перестал ходить по трубе вовсе. Отсюда: сохранение — это
/// запись файла, удаление — <c>File.Delete</c>, импорт — копирование <c>.hsm</c> в папку, экспорт —
/// отдать файл как есть. Запросов <c>SaveMacro</c>, <c>DeleteMacro</c>, <c>GetMacros</c>,
/// <c>GetTemplates</c>, <c>GetTemplateImage</c>, <c>AddMacroTemplate</c> и
/// <c>DeleteMacroTemplate</c> в протоколе больше нет.
///
/// <b>СВОЙ наблюдатель за папкой — ДА, и вот почему.</b> Довод против звучит как «два процесса
/// следят за одной папкой», но следят они за РАЗНЫМ и по-разному расплачиваются за промах:
/// <list type="bullet">
///   <item><b>Без своего наблюдателя панель узнавала бы о собственной записи от демона</b> —
///     через <c>MacrosChanged</c>, то есть после его гашения дребезга в 300 мс и round trip. Это
///     ровно тот круг, ради снятия которого F3 и затевалась.</item>
///   <item><b>Демону свой наблюдатель нужен в любом случае</b> — ему перерегистрировать хоткеи и
///     сбрасывать кэш шаблонов. Так что «второй наблюдатель» — это не второй, а первый: у панели
///     до F3 своего не было вовсе.</item>
///   <item><b>Настоящая опасность двух наблюдателей — два ПИСАТЕЛЯ</b>, а их F3 убирает по
///     построению. Сам <c>FileSystemWatcher</c> — это подписка на <c>ReadDirectoryChangesW</c> по
///     своему дескриптору; две подписки друг другу не мешают.</item>
///   <item><b>Библиотека — факт файловой системы, а не факт демона.</b> С упавшим или ещё не
///     поднятым демоном список макросов остаётся правдой, и панель обязана его показывать.</item>
/// </list>
///
/// <b>Подавления собственных записей здесь НЕТ, и это тоже решение.</b> В хранилище демона оно
/// было (сверка подписи папки по временам изменения) и ушло вместе с его авторством. Здесь оно не
/// нужно по другой причине: своя запись перечитывает папку сразу и синхронно, а эхо от
/// наблюдателя приходит спустя гашение дребезга и застаёт то же самое содержимое — то есть стоит
/// одного лишнего чтения десятка мелких файлов. У редактора же есть проверка посильнее любой
/// подписи: он сравнивает СОДЕРЖИМОЕ открытого графа с тем, что лежит на диске, и потому отличает
/// «вернулось моё» от «правил кто-то ещё» точно, а не по времени модификации.
///
/// Всё синхронно намеренно. Бандл — это десятки килобайт, запись идёт во временный файл рядом и
/// заменяется переименованием (<see cref="MacroBundleWriter"/>), а асинхронность здесь стоила бы
/// окна, в котором снимок разошёлся бы с диском.
/// </summary>
public sealed class MacroLibrary : IDisposable
{
    private const int ReloadDebounceMs = 300;

    private readonly FileSystemWatcher? _watcher;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _pendingReload;
    private IReadOnlyList<MacroBundleEntry> _entries = [];
    private int _disposed;

    /// <param name="root">
    /// Корень установки. В поставке это папка панели, в дереве разработки — папка демона; правило
    /// одно на оба процесса и живёт в <c>InstallationLayout</c>.
    /// </param>
    /// <param name="watch">
    /// Ставить ли наблюдатель за папкой. <c>false</c> нужен тестам view-model: наблюдатель
    /// перезагружает снимок с потока пула, а <c>Changed</c> тянет за собой всю пересборку списка —
    /// то есть посреди проверки в коллекции лезет второй поток. Сам наблюдатель проверяется
    /// отдельно, на настоящей папке и настоящих задержках: подделать его нельзя, не проверяя
    /// вместо него собственный мок.
    /// </param>
    public MacroLibrary(string root, bool watch = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        FolderPath = MacroBundleFolder.In(root);

        // Папку заводим, но не наполняем: посева примеров нет, свежая установка открывается с
        // пустой библиотекой — тот же инвариант, что у хранилища демона.
        try
        {
            Directory.CreateDirectory(FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Не удалось создать папку макросов {Path}", FolderPath);
        }

        Reload();

        if (!watch)
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(FolderPath, MacroBundleFolder.Filter)
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
            // Горячая перезагрузка — приятное дополнение: без наблюдателя панель просто не увидит
            // файл, положенный в папку проводником, пока её не перезапустят.
            Log.Warning(ex, "FileSystemWatcher не запустился на {Path}", FolderPath);
        }
    }

    /// <summary>Абсолютный путь к папке макросов — он же то, что стоит за кнопкой «открыть папку».</summary>
    public string FolderPath { get; }

    /// <summary>
    /// Текущий неизменяемый снимок папки, по имени. <b>Нечитаемые бандлы здесь ЕСТЬ</b> — со своим
    /// <see cref="MacroBundleEntry.Fault"/>: файл существует, и пользователь обязан его видеть.
    /// </summary>
    public IReadOnlyList<MacroBundleEntry> Entries => _entries;

    /// <summary>Поднимается после каждой перезагрузки снимка — своей записью или чужой правкой. Поток пула.</summary>
    public event Action? Changed;

    /// <summary>Запись библиотеки по имени или <c>null</c>.</summary>
    public MacroBundleEntry? TryGet(string name) =>
        string.IsNullOrEmpty(name)
            ? null
            : _entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

    /// <summary>Путь к бандлу макроса — то, что отдают на экспорт. <c>null</c>, если такого макроса нет.</summary>
    public string? PathOf(string name) => TryGet(name)?.Path;

    /// <summary>
    /// Пишет граф в <c>macros/{имя}.hsm</c> атомарно, переносит вложения и перечитывает снимок.
    /// </summary>
    /// <param name="graph">Сохраняемый граф; его имя становится основой имени файла.</param>
    /// <param name="renamedFrom">Прежнее имя, если это переименование, — иначе шаблоны не переедут.</param>
    /// <exception cref="ArgumentException">Имя графа не годится в качестве имени файла.</exception>
    public void Save(MacroGraph graph, string? renamedFrom = null)
    {
        MacroBundleFolder.Save(FolderPath, graph, renamedFrom);
        RaiseReload();
    }

    /// <summary>Удаляет бандл макроса. <c>false</c> — такого файла нет (это не ошибка).</summary>
    public bool Delete(string name)
    {
        if (!MacroBundleFolder.Delete(FolderPath, name))
        {
            return false;
        }

        RaiseReload();
        return true;
    }

    /// <summary>
    /// Кладёт чужой <c>.hsm</c> в библиотеку и отдаёт имя, под которым он лёг.
    /// </summary>
    /// <exception cref="ArgumentException">Файл не похож на бандл либо его имя не годится для NTFS.</exception>
    public string Import(string sourcePath)
    {
        var name = MacroBundleFolder.Import(FolderPath, sourcePath);
        RaiseReload();
        return name;
    }

    /// <summary>
    /// Перечень шаблонов макроса для браузера: пути, размеры, вес — без байтов.
    ///
    /// Читает с диска на каждый вызов, минуя снимок: браузер существует ровно затем, чтобы
    /// показать, что в бандле лежит СЕЙЧАС.
    /// </summary>
    public IReadOnlyList<MacroBundleTemplateInfo> TemplateCatalog(string name) =>
        TryGet(name) is { } entry ? MacroBundleReader.ReadTemplateCatalog(entry.Path) : [];

    /// <summary>Байты одного шаблона для превью. <c>null</c> — нет макроса, шаблона или файла.</summary>
    public byte[]? ReadTemplate(string name, string? set, string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName) || TryGet(name) is not { } entry)
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
    /// Кладёт PNG в бандл макроса, заменяя одноимённый. Бандл переписывается целиком — иначе не
    /// выйдет атомарной замены, — так что цена равна цене сохранения макроса.
    /// </summary>
    /// <returns><c>false</c>, если макроса нет, его бандл не читается или имя шаблона не годится.</returns>
    public bool AddTemplate(string name, string? set, string templateName, byte[] png)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        ArgumentNullException.ThrowIfNull(png);

        var relativePath = MacroBundleFormat.TemplatePath(set, templateName);
        if (!MacroBundleFormat.TryParseTemplatePath(relativePath, out _, out _))
        {
            return false;
        }

        var written = MacroBundleFolder.EditTemplates(FolderPath, name, templates =>
        [
            .. templates.Where(file => !SamePath(file.Path, relativePath)),
            new MacroBundleFile(relativePath, png),
        ]);

        if (written)
        {
            RaiseReload();
        }

        return written;
    }

    /// <summary>Убирает шаблон из бандла. <c>false</c> — такого шаблона там нет.</summary>
    public bool DeleteTemplate(string name, string? set, string templateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);

        var relativePath = MacroBundleFormat.TemplatePath(set, templateName);
        var written = MacroBundleFolder.EditTemplates(FolderPath, name, templates =>
        {
            var kept = templates.Where(file => !SamePath(file.Path, relativePath)).ToList();
            return kept.Count == templates.Count ? null : kept;
        });

        if (written)
        {
            RaiseReload();
        }

        return written;
    }

    /// <summary>Перечитывает папку прямо сейчас и поднимает <see cref="Changed"/>.</summary>
    public void Refresh() => RaiseReload();

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

        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pendingReload;
            _pendingReload = null;
        }

        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Обратный вызов наблюдателя вытеснил и освободил его, пока мы сносились.
        }

        pending?.Dispose();
    }

    // NTFS регистр не различает, так что «Лучник.png» и «лучник.png» — это не два шаблона.
    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void Reload() => _entries = MacroBundleFolder.Read(FolderPath);

    private void RaiseReload()
    {
        Reload();
        Changed?.Invoke();
    }

    // FileSystemWatcher стреляет с пула потоков, а замена файла даёт по нескольку событий на одну
    // запись, поэтому весь всплеск внутри окна гашения дребезга схлопывается в одно чтение.
    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _pendingReload;
            _pendingReload = cts;
        }

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

        try
        {
            RaiseReload();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось перечитать библиотеку макросов после изменения на диске");
        }
    }
}
