using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;

namespace SmartMacro.Macros.Storage;

/// <summary>
/// Библиотека макросов на диске глазами ДЕМОНА: по одному БАНДЛУ <c>.hsm</c> на макрос в папке
/// <c>macros/</c> в корне установки, где основа имени файла и есть имя макроса.
///
/// <b>С волны F3 это хранилище ТОЛЬКО ЧИТАЕТ.</b> Единственный автор изменений — панель: редактор
/// живёт в ней, значит она и пишет, а процессы делят файловую систему, так что макрос перестал
/// ходить по трубе вовсе. Отсюда три вещи, которые здесь ИСЧЕЗЛИ и возвращать их не надо:
/// <list type="bullet">
///   <item><c>SaveAsync</c> / <c>DeleteAsync</c> и правка шаблонов — вместе с запросами
///     <c>SaveMacro</c>, <c>DeleteMacro</c>, <c>AddMacroTemplate</c>, <c>DeleteMacroTemplate</c>,
///     которых в протоколе больше нет;</item>
///   <item><b>подавление собственных записей</b> — сверка подписи папки по временам последней
///     записи. Демон не пишет, глушить нечего, и заметный кусок мудрёного кода ушёл целиком;</item>
///   <item>вторая дорога чтения шаблонов (<c>TemplateCatalog</c> / <c>ReadTemplate</c>) — браузер
///     теперь читает бандл сам, а кэш исполнителя ходит в файл своей дорогой.</item>
/// </list>
///
/// <b>Взамен появилась ВАЛИДАЦИЯ ПРИ ЗАГРУЗКЕ, и она обязательна.</b> Пока писал демон, работала
/// гарантия «оно в библиотеке ⇒ демон его принял». Теперь в силе только «кто-то положил туда файл»,
/// поэтому судит о графе тот, кто его исполняет: каждый прочитанный бандл проходит общий
/// <see cref="MacroGraphValidator"/> с ЕГО ЖЕ описью шаблонов, вердикт ложится в
/// <see cref="MacroLibraryEntry.Issues"/>, и <c>HotkeyListener</c> берёт себе привязки только из
/// <see cref="Armed"/>. Иначе «хоткей нажимается, ничего не происходит» вернулось бы с другой
/// стороны.
///
/// ИНВАРИАНТ: ХРАНИЛИЩЕ НЕ ПИШЕТ НА ДИСК ВООБЩЕ НИЧЕГО. Конструктор заводит саму папку, если её
/// ещё нет (иначе не на что натравливать наблюдателя), читает её — и на этом всё. Раньше инвариант
/// звучал как «конструктор не пишет»; F3 распространила его на весь класс.
///
/// <b><c>*.json.incompatible</c> нет, и это тоже решение, а не пропажа.</b> Отодвигание непарсимого
/// файла в сторону появилось, когда ноды перешли на <c>Guid</c>. У бандла эта беда снята форматом:
/// <see cref="MacroBundleReader"/> не бросает и различает «повреждён» и «сделан другой версией
/// формата», а версия пишется в файл с первого дня. Отодвинуть бандл БУДУЩЕЙ версии значило бы
/// соврать ровно тем способом, ради недопущения которого поле версии и заведено. Поэтому файл
/// остаётся на месте, а причина уходит в журнал целиком — и, с волны F3, ещё и в библиотеку панели
/// отдельной строкой с восклицательным знаком.
///
/// Реализует <see cref="IMacroGraphResolver"/>, так что <c>RunMacroNode</c> разрешает под-макросы
/// прямо из живой библиотеки.
///
/// Параллелизм: чтения идут через неизменяемый снимок и обходятся без блокировок; перезагрузку
/// выстраивает в очередь семафор.
/// </summary>
public sealed partial class MacroGraphStore : IMacroGraphResolver, IDisposable
{
    private const int ReloadDebounceMs = 300;

    private readonly string _directory;
    private readonly ILogger<MacroGraphStore> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly FileSystemWatcher? _watcher;

    private CancellationTokenSource? _pendingReload;
    private int _disposed;

    private ImmutableList<MacroLibraryEntry> _entries = [];
    private ImmutableList<MacroGraph> _macros = [];
    private ImmutableList<MacroGraph> _armed = [];

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
        _directory = MacroBundleFolder.In(baseDirectory);

        // Единственное обращение к диску на запись за всю жизнь объекта — и то это папка, а не её
        // содержимое. См. инвариант в комментарии класса.
        Directory.CreateDirectory(_directory);
        Reload();

        // Наблюдатель ставится по возможности: горячая перезагрузка — приятное дополнение, так
        // что сбои прав или платформы деградируют до «перезапустите, чтобы подхватить правки»,
        // а не до падения. С волны F3 это ЕДИНСТВЕННЫЙ способ демона узнать о новом макросе:
        // панель пишет файл и демону об этом не сообщает.
        try
        {
            _watcher = new FileSystemWatcher(_directory, MacroBundleFolder.Filter)
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

    /// <summary>
    /// Поднимается после того, как библиотека перечитана и изменилась.
    ///
    /// Нагрузки нет намеренно: все три подписчика в этом же процессе и берут из хранилища РАЗНОЕ —
    /// <c>HotkeyListener</c> вооружаемые графы, <c>MacroTemplateCache</c> ничего (он просто
    /// сбрасывается), <c>IpcServer</c> тоже ничего (он рассылает голое событие). Список графов
    /// параметром обслуживал бы только первого и врал бы ему с волны F3, когда «вся библиотека» и
    /// «то, что можно вооружить» перестали совпадать.
    /// </summary>
    public event Action? MacrosChanged;

    /// <summary>Абсолютный путь к папке с макросами.</summary>
    public string FolderPath => _directory;

    /// <summary>Текущий неизменяемый снимок библиотеки, упорядоченный по имени.</summary>
    public IReadOnlyList<MacroGraph> All => _macros;

    /// <summary>
    /// Тот же снимок, но целыми бандлами: граф плюс паспорт плюс опись шаблонов плюс вердикт
    /// валидатора. Нужен тем, кто спрашивает про ФАЙЛ, а не про граф.
    /// </summary>
    public IReadOnlyList<MacroLibraryEntry> Entries => _entries;

    /// <summary>
    /// Макросы, чьи триггеры позволено вооружать: граф прочитан И валидатор не нашёл в нём ошибок.
    ///
    /// Отдельный список, а не фильтр на месте вызова, потому что спрашивают его из двух мест
    /// (регистрация при старте и перерегистрация по изменению библиотеки), а «забыли отфильтровать
    /// в одном из них» выглядит как хоткей, который иногда работает.
    /// </summary>
    public IReadOnlyList<MacroGraph> Armed => _armed;

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
    /// Перечитывает папку прямо сейчас, не дожидаясь наблюдателя.
    ///
    /// Существует ради одной гонки, и её завела как раз F3. Панель пишет файл сама, а демон узнаёт
    /// о нём наблюдателем с гашением дребезга в 300 мс — значит, «Сохранить», а следом сразу
    /// «Запустить» способны попасть в промежуток, где макроса в снимке ещё нет, и кнопка ответила
    /// бы «макрос не найден» про файл, который только что записали. Поэтому <c>RunMacro</c>,
    /// промахнувшись мимо снимка, заглядывает на диск ещё раз. Только на промахе: обычный путь
    /// по-прежнему не ходит в файловую систему вовсе.
    /// </summary>
    /// <returns><c>true</c>, если состав библиотеки изменился (и <see cref="MacrosChanged"/> поднято).</returns>
    public bool Refresh()
    {
        _reloadLock.Wait();
        bool changed;
        try
        {
            var before = Signature();
            Reload();
            changed = !before.SequenceEqual(Signature());
        }
        finally
        {
            _reloadLock.Release();
        }

        // Только на изменившемся снимке: перерегистрация хоткеев и сброс кэша шаблонов на каждый
        // промах мимо библиотеки были бы платой за то, что макроса действительно нет.
        if (changed)
        {
            MacrosChanged?.Invoke();
        }

        return changed;
    }

    // «Что мы сейчас держим» одной строкой на макрос: имя плюс дата правки из его паспорта.
    // Даты хватает, и это не экономия — писатель проставляет её на КАЖДУЮ запись бандла, включая
    // правку одного шаблона, так что «шаблон подменили, граф прежний» отсюда видно. Читается она
    // из уже прочитанного паспорта, то есть не стоит ни одного лишнего обращения к диску.
    //
    // Это НЕ вернувшееся подавление собственных записей — та сверка отвечала на вопрос «мы ли это
    // писали» и ушла вместе с авторством демона. Здесь вопрос другой и куда более скромный: есть
    // ли смысл будить подписчиков. Путь наблюдателя эту сверку не делает вовсе и поднимает событие
    // безусловно — ему важно сказать «я перечитал», даже если ничего не изменилось.
    private List<(string Name, DateTimeOffset Modified)> Signature() =>
        [.. _entries.Select(entry => (entry.Name, entry.Metadata.Modified))];

    // Полное перечитывание папки. Не бросает никогда: бандл, который не прочитался, попадает в
    // лог и пропускается, чтобы одна кривая правка руками не опустошила библиотеку. Файл при этом
    // НЕ трогаем — см. про *.incompatible в комментарии класса.
    private void Reload()
    {
        var entries = new List<MacroLibraryEntry>();
        var skipped = 0;

        foreach (var bundle in MacroBundleFolder.Read(_directory))
        {
            if (bundle is not { Graph: not null, Metadata: not null })
            {
                LogBundleSkipped(bundle.Path, bundle.Fault.ToString(), bundle.FaultMessage ?? "(без подробностей)");
                skipped++;
                continue;
            }

            if (bundle.NameOverridden)
            {
                // Главенствует имя файла — переименовали файл, значит переименовали макрос.
                LogNameMismatch(bundle.Metadata.Name, bundle.Name);
            }

            // Валидация ЗДЕСЬ, а не на первом прогоне: гарантия «оно в библиотеке ⇒ демон его
            // принял» ушла вместе с SaveMacro, и её место заняло «демон прочитал и вынес
            // вердикт». Опись подаётся из ЭТОГО бандла — та же, что увидит панель.
            var issues = MacroGraphValidator.Validate(bundle.Graph, bundle.Templates).ToList();
            var entry = new MacroLibraryEntry(
                bundle.Name,
                bundle.Graph,
                bundle.Metadata,
                bundle.TemplatePaths,
                bundle.Path,
                issues);

            if (entry.HasErrors)
            {
                LogNotArmed(entry.Name, entry.FirstError ?? "(без подробностей)");
            }

            entries.Add(entry);
        }

        _entries = [.. entries];
        _macros = [.. entries.Select(entry => entry.Graph)];
        _armed = [.. entries.Where(entry => !entry.HasErrors).Select(entry => entry.Graph)];
        LogLoaded(entries.Count, skipped, _directory);
    }

    // FileSystemWatcher стреляет с пула потоков, а замена файла даёт по нескольку событий на одну
    // запись, поэтому весь всплеск внутри окна гашения дребезга схлопывается в одну перезагрузку.
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

        try
        {
            await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            Reload();
            LogReloadedExternally(_macros.Count);
        }
        catch (Exception ex)
        {
            LogReloadFailed(ex);
        }
        finally
        {
            _reloadLock.Release();
        }

        // Поднимается БЕЗУСЛОВНО и снаружи блокировки. Безусловно — потому что событие с волны F3
        // означает «демон перечитал библиотеку и сейчас перерегистрирует хоткеи», а это правда и
        // тогда, когда набор графов не изменился (поменялся один шаблон внутри бандла, скажем);
        // снаружи — чтобы подписчики могли вызывать нас обратно без взаимной блокировки.
        MacrosChanged?.Invoke();
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

        _reloadLock.Dispose();
    }
}
