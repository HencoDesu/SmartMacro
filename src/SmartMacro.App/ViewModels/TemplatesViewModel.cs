using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;

namespace SmartMacro.App.ViewModels;

/// <summary>Одна строка браузера: шаблон, лежащий в бандле открытого макроса.</summary>
public sealed class TemplateRowViewModel : ObservableObject
{
    private bool _isSelected;

    internal TemplateRowViewModel(TemplateDto template, IReadOnlyList<TemplateReference> usedBy)
    {
        Set = template.Set;
        Name = template.Name;
        Bytes = template.Bytes;
        IsDecodable = template is { Width: > 0, Height: > 0 };
        SizeText = IsDecodable
            ? string.Create(CultureInfo.InvariantCulture, $"{template.Width}×{template.Height}")
            : "не PNG";
        BytesText = TemplateFormat.Bytes(template.Bytes);
        UsedBy = usedBy;
    }

    /// <summary>Набор (подпапка) либо <c>null</c> — одиночный шаблон в корне <c>templates/</c> бандла.</summary>
    public string? Set { get; }

    /// <summary>Имя файла без расширения — та самая строка, которой шаблон называет нода.</summary>
    public string Name { get; }

    /// <summary>Размер файла в байтах. Сверяется с потолком превью.</summary>
    public long Bytes { get; }

    /// <summary>«112×34» либо «не PNG», когда заголовок не разобрался.</summary>
    public string SizeText { get; }

    /// <summary>Размер файла для человека: «35,9 КБ».</summary>
    public string BytesText { get; }

    /// <summary><c>false</c> у файла, чей заголовок PNG не читается: превью для него не запрашивают.</summary>
    public bool IsDecodable { get; }

    /// <summary>
    /// Ноды ОТКРЫТОГО макроса, которые называют этот шаблон. Считает
    /// <see cref="MacroTemplateAnalysis"/> — по графу, который у панели и так есть, без единого
    /// нового запроса. Ссылка на набор достаётся каждому файлу набора: <c>RecognizeTag</c>
    /// называет набор целиком, так что «Лучник.png никому не нужен» было бы враньём про шаблон,
    /// которым опознают лучника.
    /// </summary>
    public IReadOnlyList<TemplateReference> UsedBy { get; }

    /// <summary>«3 ноды» либо «не используется». Второе — не ошибка, просто факт.</summary>
    public string UsageText => UsedBy.Count == 0 ? "не используется" : Plural(UsedBy.Count);

    /// <summary><c>true</c>, когда на шаблон не ссылается ни одна нода, — строка рисуется приглушённо.</summary>
    public bool IsUnused => UsedBy.Count == 0;

    /// <summary>Подсветка выбранной строки; выделением владеет <see cref="TemplatesViewModel"/>.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    /// <summary>«classes / Лучник» либо просто «ServerSelectButton» — заголовок панели превью.</summary>
    public string FullName => Set is null ? Name : $"{Set} / {Name}";

    internal static string Plural(int nodes) => (nodes % 10, nodes % 100) switch
    {
        (1, not 11) => string.Create(CultureInfo.CurrentCulture, $"{nodes} нода"),
        (2 or 3 or 4, not (12 or 13 or 14)) => string.Create(CultureInfo.CurrentCulture, $"{nodes} ноды"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{nodes} нод"),
    };
}

/// <summary>Раздел списка: одиночные шаблоны либо один набор.</summary>
public sealed class TemplateGroupViewModel
{
    internal TemplateGroupViewModel(string? set, IReadOnlyList<TemplateRowViewModel> rows)
    {
        Set = set;
        Rows = rows;
        // «одиночные» — не имя папки, а роль: это файлы в корне, и называет их не
        // RecognizeTag целиком, а Find/Wait поимённо.
        Title = set ?? "одиночные";
        CountText = rows.Count.ToString(CultureInfo.InvariantCulture);
        IsSet = set is not null;
    }

    /// <summary>Имя набора либо <c>null</c> у раздела одиночных шаблонов.</summary>
    public string? Set { get; }

    /// <summary>Подпись раздела.</summary>
    public string Title { get; }

    /// <summary>Сколько в нём файлов.</summary>
    public string CountText { get; }

    /// <summary><c>true</c> у настоящего набора — тогда рядом уместна подсказка про RecognizeTag.</summary>
    public bool IsSet { get; }

    /// <summary>Строки раздела, по имени.</summary>
    public IReadOnlyList<TemplateRowViewModel> Rows { get; }
}

/// <summary>
/// Браузер шаблонов ОТКРЫТОГО МАКРОСА — то, что лежит в <c>templates/</c> внутри его бандла
/// <c>.hsm</c>: какие файлы там есть, как они выглядят и какие ноды их называют.
///
/// <b>Раньше это был самостоятельный режим рейки «Шаблоны», и он исчез (волна F2).</b> Рейка —
/// про сущности, а шаблон перестал быть сущностью: общего дерева <c>templates/</c> нет, файл
/// принадлежит ровно одному макросу и живёт внутри него. Поэтому браузер сложился внутрь
/// редактора, в инспектор макроса — туда же, где триггеры и переменные, то есть к остальным
/// свойствам макроса как целого.
///
/// <b>Раздел «НЕТ ФАЙЛА» отсюда тоже пропал, и это повышение класса ошибки, а не потеря.</b>
/// «Нода называет шаблон, которого нет» теперь ловит ВАЛИДАТОР — при сохранении и при загрузке
/// библиотеки, — потому что набор шаблонов бандла известен статически. Раньше об этом можно было
/// узнать, только зайдя в отдельный режим и посмотрев в специальный раздел.
///
/// <b>Картинки — по одной, за выделением.</b> Список это метаданные, они мелкие; PNG — килобайты
/// каждый, а труба общая с потоком событий прогона. Поэтому байты запрашиваются на смену
/// выделения, кэшируются на время жизни панели (повторный клик по строке бесплатен), и файл
/// крупнее <see cref="TemplateLimits.MaxImageBytes"/> не запрашивается вовсе — его размер уже
/// известен из списка.
///
/// <b>Список перечитывается при каждой смене открытого макроса и по <c>MacrosChanged</c>.</b>
/// Второе несёт двойную нагрузку: этим же событием отзывается собственная правка (добавили или
/// удалили шаблон) и чужая (бандл подменили в проводнике).
/// </summary>
public sealed class TemplatesViewModel : ObservableObject, IDisposable
{
    private readonly IIpcClient _client;
    private readonly IUiDispatcher _dispatcher;

    // Кэш превью. Ключ — тройка «макрос + набор + имя», то есть полная идентичность шаблона:
    // одноимённые шаблоны двух макросов — разные картинки, и общий ключ показал бы чужую.
    private readonly Dictionary<(string Macro, string? Set, string Name), byte[]> _previews = [];

    private string? _macroName;
    private MacroGraph? _graph;
    private IReadOnlyList<TemplateDto> _files = [];

    private TemplateRowViewModel? _selected;
    private byte[]? _previewPng;
    private string? _previewProblem;
    private string? _importProblem;
    private string _importSet = string.Empty;
    private bool _isLoading;

    public TemplatesViewModel(IIpcClient client, IUiDispatcher? dispatcher = null)
    {
        _client = client;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;
    }

    /// <summary>Разделы списка: сперва одиночные шаблоны, затем наборы по алфавиту.</summary>
    public ObservableCollection<TemplateGroupViewModel> Groups { get; } = [];

    /// <summary>Все строки одним списком — счётчик заголовка и тесты смотрят сюда.</summary>
    public ObservableCollection<TemplateRowViewModel> Templates { get; } = [];

    /// <summary>Имя открытого макроса или <c>null</c>, когда в редакторе ничего не открыто.</summary>
    public string? MacroName => _macroName;

    /// <summary>
    /// Опись шаблонов открытого макроса — то, чем валидатор ловит «нода называет шаблон, которого
    /// в бандле нет».
    ///
    /// <c>null</c>, пока макроса нет: <c>null</c> и пустая опись — разные вещи, и валидатор их
    /// различает. У несохранённого черновика бандла не существует, и обвинять его ноды в ссылке
    /// на несуществующий файл значило бы соврать.
    /// </summary>
    public MacroTemplateInventory? Inventory => _macroName is null
        ? null
        : MacroTemplateInventory.FromPaths(_files.Select(file => MacroBundleFormat.TemplatePath(file.Set, file.Name)));

    /// <summary><c>true</c>, когда браузеру есть что показывать (макрос открыт).</summary>
    public bool HasMacro => _macroName is not null;

    /// <summary>Число в заголовке раздела инспектора.</summary>
    public string CountText => Templates.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>true</c>, когда в бандле открытого макроса шаблонов нет.</summary>
    public bool IsEmpty => Templates.Count == 0;

    /// <summary>
    /// Набор, в который поедет следующий импорт. Пусто = одиночный шаблон в корне.
    ///
    /// Поле рядом с кнопкой, а не диалог: набор — это одна строка, а спрашивать её отдельным окном
    /// значило бы городить модальность ради текстового поля. Имя самого шаблона не спрашивается
    /// вовсе — им становится основа имени выбранного файла, ровно как было в общем дереве.
    /// </summary>
    public string ImportSet
    {
        get => _importSet;
        set => SetField(ref _importSet, value);
    }

    /// <summary>Выбранная строка. Присвоение подгружает её превью.</summary>
    public TemplateRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            if (_selected is not null)
            {
                _selected.IsSelected = false;
            }

            _selected = value;

            if (_selected is not null)
            {
                _selected.IsSelected = true;
            }

            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedUsages));
            OnPropertyChanged(nameof(SelectedUsageHeader));
            _ = LoadPreviewAsync(value);
        }
    }

    /// <summary><c>true</c>, когда панель превью показывает шаблон, а не своё пустое состояние.</summary>
    public bool HasSelection => _selected is not null;

    /// <summary>Подписи нод, которые называют выбранный шаблон.</summary>
    public IReadOnlyList<string> SelectedUsages => _selected is null
        ? []
        : [.. _selected.UsedBy.Select(r => r.NodeName).Distinct(StringComparer.Ordinal)];

    /// <summary>Заголовок раздела ссылок в панели превью.</summary>
    public string SelectedUsageHeader => _selected is null
        ? string.Empty
        : SelectedUsages.Count == 0
            ? "НА НЕГО НИКТО НЕ ССЫЛАЕТСЯ"
            : string.Create(CultureInfo.CurrentCulture, $"ССЫЛАЮТСЯ · {SelectedUsages.Count}");

    /// <summary>
    /// Байты выбранного шаблона либо <c>null</c>, пока их нет. Именно байты, а не
    /// <c>Bitmap</c>: view-model остаётся свободной от типов Avalonia, а декодированием и
    /// освобождением картинки занимается вид.
    /// </summary>
    public byte[]? PreviewPng
    {
        get => _previewPng;
        private set
        {
            _previewPng = value;
            OnPropertyChanged(nameof(PreviewPng));
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    /// <summary><c>true</c>, когда есть что нарисовать.</summary>
    public bool HasPreview => _previewPng is not null;

    /// <summary>Почему превью нет: слишком велик, не PNG, демон отказал. <c>null</c>, когда всё в порядке.</summary>
    public string? PreviewProblem
    {
        get => _previewProblem;
        private set
        {
            if (SetField(ref _previewProblem, value))
            {
                OnPropertyChanged(nameof(HasPreviewProblem));
            }
        }
    }

    /// <summary><c>true</c>, когда <see cref="PreviewProblem"/> есть что сказать.</summary>
    public bool HasPreviewProblem => _previewProblem is not null;

    /// <summary>
    /// Почему не получилось положить или убрать шаблон. Отдельно от <see cref="PreviewProblem"/>:
    /// у отказа записи и у отсутствующей картинки разные причины и разное время жизни.
    /// </summary>
    public string? ImportProblem
    {
        get => _importProblem;
        private set
        {
            if (SetField(ref _importProblem, value))
            {
                OnPropertyChanged(nameof(HasImportProblem));
            }
        }
    }

    /// <summary><c>true</c>, когда <see cref="ImportProblem"/> есть что сказать.</summary>
    public bool HasImportProblem => _importProblem is not null;

    /// <summary>Идёт запрос — гасит кнопки.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsNotLoading));
            }
        }
    }

    /// <summary>Обратное <see cref="IsLoading"/> — для привязки <c>IsEnabled</c>.</summary>
    public bool IsNotLoading => !_isLoading;

    /// <summary>Поднимается после любой пересборки списка.</summary>
    public event Action? TemplatesChanged;

    /// <summary>
    /// Наводит браузер на макрос, открытый в редакторе. <c>null</c> — редактор закрыт; тогда
    /// список пуст и никаких запросов не уходит.
    ///
    /// Граф передаётся вместе с именем, потому что «какие ноды называют этот шаблон» считается по
    /// НЕМУ, а не по тому, что лежит на диске: набрал имя шаблона в ноде — и строка сразу
    /// перестала быть «не используется», ещё до сохранения.
    /// </summary>
    public void ShowMacro(string? macroName, MacroGraph? graph)
    {
        var macroChanged = !string.Equals(_macroName, macroName, StringComparison.Ordinal);
        _macroName = macroName;
        _graph = graph;

        if (macroName is null)
        {
            _files = [];
            _selected = null;
            PreviewPng = null;
            PreviewProblem = null;
            ImportProblem = null;
            Rebuild();
            OnPropertyChanged(nameof(MacroName));
            OnPropertyChanged(nameof(HasMacro));
            return;
        }

        OnPropertyChanged(nameof(MacroName));
        OnPropertyChanged(nameof(HasMacro));

        if (macroChanged)
        {
            // Другой макрос — другие файлы; пока не приехал список, показывать старый нельзя.
            _files = [];
            _selected = null;
            PreviewPng = null;
            PreviewProblem = null;
            ImportProblem = null;
            Rebuild();
            _ = RefreshAsync();
        }
        else
        {
            // Тот же макрос, новый граф (правят ноды) — файлы те же, пересчитать надо только
            // «кто на что ссылается».
            Rebuild();
        }
    }

    /// <summary>Перечитывает перечень шаблонов открытого макроса у демона.</summary>
    public async Task RefreshAsync()
    {
        if (_macroName is not { } macroName)
        {
            return;
        }

        // Через диспетчер даже здесь: RefreshAsync зовётся и из обработчика Connected, а тот
        // поднимается на потоке читателя IPC.
        _dispatcher.Post(() => IsLoading = true);
        try
        {
            var files = await _client
                .RequestAsync<TemplateDto[]>(IpcMessageTypes.GetTemplates, new GetTemplatesRequest(macroName))
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                // Пока летел ответ, редактор мог открыть другой макрос: список не его — выбросить.
                if (!string.Equals(_macroName, macroName, StringComparison.Ordinal))
                {
                    IsLoading = false;
                    return;
                }

                _files = files ?? [];
                Rebuild();
                IsLoading = false;
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // Обрыв между подключением и запросом. Поддерживающий цикл переподключится, снова
            // сработает Connected, и он это повторит.
            Log.Warning(ex, "Не удалось получить список шаблонов макроса '{Macro}'", macroName);
            _dispatcher.Post(() => IsLoading = false);
        }
    }

    /// <summary>
    /// Кладёт PNG в бандл открытого макроса. Имя шаблона = основа имени файла, набор —
    /// <see cref="ImportSet"/>.
    /// </summary>
    /// <param name="fileName">Имя выбранного файла (с расширением).</param>
    /// <param name="png">Его содержимое.</param>
    public async Task<bool> ImportAsync(string fileName, byte[] png)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(png);

        ImportProblem = null;
        if (_macroName is not { } macroName)
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            ImportProblem = $"«{fileName}» — не годится как имя шаблона.";
            return false;
        }

        // Потолок сверяем ДО отправки: демон откажет по тому же числу, а round trip ради отказа не
        // нужен — ровно как с превью.
        if (png.Length > TemplateLimits.MaxImageBytes)
        {
            ImportProblem =
                $"{TemplateFormat.Bytes(png.Length)} — больше потолка в {TemplateFormat.Bytes(TemplateLimits.MaxImageBytes)}.";
            return false;
        }

        var set = string.IsNullOrWhiteSpace(_importSet) ? null : _importSet.Trim();
        IsLoading = true;
        try
        {
            var files = await _client
                .RequestAsync<TemplateDto[]>(
                    IpcMessageTypes.AddMacroTemplate,
                    new AddMacroTemplateRequest(macroName, set, name, png))
                .ConfigureAwait(true);
            Apply(macroName, files);
            // Байты у нас на руках — класть их в кэш превью сразу дешевле, чем просить обратно.
            _previews[(macroName, set, name)] = png;
            return true;
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            ImportProblem = $"Не удалось добавить шаблон: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Убирает шаблон из бандла открытого макроса.</summary>
    public async Task DeleteAsync(TemplateRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        ImportProblem = null;
        if (_macroName is not { } macroName)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var files = await _client
                .RequestAsync<TemplateDto[]>(
                    IpcMessageTypes.DeleteMacroTemplate,
                    new DeleteMacroTemplateRequest(macroName, row.Set, row.Name))
                .ConfigureAwait(true);
            _previews.Remove((macroName, row.Set, row.Name));
            Apply(macroName, files);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            ImportProblem = $"Не удалось удалить шаблон: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.EventReceived -= OnEventReceived;
    }

    // ---- проводка к демону -----------------------------------------------------------------

    private void OnConnected() => _ = RefreshAsync();

    private void OnEventReceived(IpcEvent evt)
    {
        // Библиотека изменилась — это могла быть и наша собственная правка бандла (добавление или
        // удаление шаблона поднимает то же событие), и чужая правка файла в проводнике. Дешевле
        // перечитать список, чем заводить отдельный путь на каждый случай.
        if (string.Equals(evt.Type, IpcMessageTypes.MacrosChanged, StringComparison.Ordinal))
        {
            _ = RefreshAsync();
        }
    }

    private void Apply(string macroName, TemplateDto[]? files)
    {
        if (!string.Equals(_macroName, macroName, StringComparison.Ordinal))
        {
            return;
        }

        _files = files ?? [];
        Rebuild();
    }

    private async Task LoadPreviewAsync(TemplateRowViewModel? row)
    {
        PreviewProblem = null;
        if (row is null || _macroName is not { } macroName)
        {
            PreviewPng = null;
            return;
        }

        if (_previews.TryGetValue((macroName, row.Set, row.Name), out var cached))
        {
            PreviewPng = cached;
            return;
        }

        PreviewPng = null;

        if (!row.IsDecodable)
        {
            PreviewProblem = "Файл не разобрался как PNG — показывать нечего.";
            return;
        }

        if (row.Bytes > TemplateLimits.MaxImageBytes)
        {
            // Спрашивать бессмысленно: демон откажет по тому же порогу. Размер файла у нас уже
            // есть из списка, так что round trip ради отказа не делаем.
            PreviewProblem =
                $"{row.BytesText} — больше потолка превью в {TemplateFormat.Bytes(TemplateLimits.MaxImageBytes)}.";
            return;
        }

        TemplateImageDto? image;
        try
        {
            image = await _client
                .RequestAsync<TemplateImageDto>(
                    IpcMessageTypes.GetTemplateImage,
                    new GetTemplateImageRequest(macroName, row.Set, row.Name))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            PreviewProblem = $"Не удалось прочитать шаблон: {ex.Message}";
            return;
        }

        if (image is null)
        {
            PreviewProblem = "Демон вернул пустой ответ.";
            return;
        }

        _previews[(image.MacroName, image.Set, image.Name)] = image.Png;

        // Гонка выделения: пользователь щёлкает быстрее, чем отвечает демон, и ответ на
        // предпоследний выбор может прийти последним. Без этой сверки в превью осталась бы
        // картинка не той строки, что подсвечена, — а с переездом браузера внутрь редактора
        // устареть успевает и весь макрос.
        if (image.Describes(_macroName ?? string.Empty, _selected?.Set, _selected?.Name ?? string.Empty))
        {
            PreviewPng = image.Png;
        }
    }

    // ---- пересборка ---------------------------------------------------------------------------

    private void Rebuild()
    {
        // Ссылки на ОДИНОЧНЫЙ файл ключуются его именем; ссылки на НАБОР — именем папки, и
        // достаются они каждому файлу этого набора: RecognizeTag называет набор целиком.
        var usage = _graph is null ? [] : MacroTemplateAnalysis.Analyze(_graph);
        var singles = usage
            .Where(u => !u.IsSet)
            .ToDictionary(u => u.Name, u => u.References, StringComparer.Ordinal);
        var sets = usage
            .Where(u => u.IsSet)
            .ToDictionary(u => u.Name, u => u.References, StringComparer.Ordinal);

        var rows = _files
            .Select(file => new TemplateRowViewModel(
                file,
                file.Set is null
                    ? singles.GetValueOrDefault(file.Name, [])
                    : sets.GetValueOrDefault(file.Set, [])))
            .ToList();

        var previousKey = _selected is null ? default((string?, string)?) : (_selected.Set, _selected.Name);

        Templates.Clear();
        foreach (var row in rows)
        {
            Templates.Add(row);
        }

        Groups.Clear();
        foreach (var group in rows.GroupBy(row => row.Set))
        {
            Groups.Add(new TemplateGroupViewModel(group.Key, [.. group]));
        }

        // Выделение переживает обновление, пока файл на месте, — иначе добавление соседнего
        // шаблона выбрасывало бы пользователя из того, который он разглядывает.
        _selected = null;
        Selected = previousKey is { } key
            ? rows.FirstOrDefault(row =>
                string.Equals(row.Set, key.Item1, StringComparison.Ordinal)
                && string.Equals(row.Name, key.Item2, StringComparison.Ordinal))
            : null;

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        TemplatesChanged?.Invoke();
    }
}

/// <summary>Форматирование размеров — одно на строку списка и на сообщение о потолке.</summary>
internal static class TemplateFormat
{
    public static string Bytes(long bytes) => bytes < 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes} Б")
        : bytes < 1024 * 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} КБ")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024.0):0.#} МБ");
}
