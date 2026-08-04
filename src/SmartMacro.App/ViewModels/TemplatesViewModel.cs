using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Model;

namespace SmartMacro.App.ViewModels;

/// <summary>Одна строка браузера: файл шаблона, лежащий у демона.</summary>
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

    /// <summary>Набор (подпапка) либо <c>null</c> — одиночный шаблон в корне дерева.</summary>
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
    /// Все места в библиотеке, которые называют этот шаблон. Считает
    /// <see cref="MacroTemplateAnalysis"/> — по графам, которые у панели и так есть, без единого
    /// нового запроса.
    /// </summary>
    public IReadOnlyList<TemplateReference> UsedBy { get; }

    /// <summary>«3 макроса» либо «не используется». Второе — не ошибка, просто факт.</summary>
    public string UsageText
    {
        get
        {
            var macros = UsedBy.Select(r => r.MacroName).Distinct(StringComparer.Ordinal).Count();
            return macros == 0 ? "не используется" : Plural(macros);
        }
    }

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

    internal static string Plural(int macros) => (macros % 10, macros % 100) switch
    {
        (1, not 11) => string.Create(CultureInfo.CurrentCulture, $"{macros} макрос"),
        (2 or 3 or 4, not (12 or 13 or 14)) => string.Create(CultureInfo.CurrentCulture, $"{macros} макроса"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{macros} макросов"),
    };
}

/// <summary>Раздел списка: одиночные шаблоны либо один набор.</summary>
public sealed class TemplateGroupViewModel
{
    internal TemplateGroupViewModel(string? set, IReadOnlyList<TemplateRowViewModel> rows)
    {
        Set = set;
        Rows = rows;
        // «одиночные» — не имя папки, а роль: это файлы в корне дерева, и называет их не
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
/// Нода называет шаблон, которого в папке нет. До этого режима такое выяснялось только в момент
/// прогона — и то строчкой в логе демона.
/// </summary>
public sealed class MissingTemplateViewModel
{
    internal MissingTemplateViewModel(TemplateUsage usage)
    {
        Name = usage.Name;
        IsSet = usage.IsSet;
        Kind = usage.IsSet ? "набор" : "шаблон";
        UsageText = string.Join(
            ", ",
            usage.References
                .Select(r => $"{r.MacroName} / {r.NodeName}")
                .Distinct(StringComparer.Ordinal));
        // Ровно то, что произойдёт в прогоне: примитивы не бросают, они возвращают «не нашлось».
        Consequence = usage.IsSet
            ? "нет папки templates/ — RecognizeTag всегда пойдёт по «не совпало»"
            : "нет файла в templates/ — Find/Wait всегда пойдут по «не найдено»";
    }

    /// <summary>Имя, которое написано в ноде.</summary>
    public string Name { get; }

    /// <summary><c>true</c>, когда не хватает НАБОРА (подпапки), а не одиночного файла.</summary>
    public bool IsSet { get; }

    /// <summary>«шаблон» либо «набор» — что именно потеряно.</summary>
    public string Kind { get; }

    /// <summary>«pw-boot / find-server, pw-boot / wait-server».</summary>
    public string UsageText { get; }

    /// <summary>Чем это обернётся в прогоне.</summary>
    public string Consequence { get; }
}

/// <summary>
/// Режим «Шаблоны»: что лежит в дереве <c>Assets/templates</c> у демона, как оно выглядит и кому
/// оно нужно.
///
/// <b>Две половины из двух источников, и это главное решение здесь.</b> Файлы знает только демон
/// (<c>GetTemplates</c> — метаданные, <c>GetTemplateImage</c> — байты одного). А вот «кому нужен
/// этот шаблон» панель считает САМА: библиотека макросов у неё уже есть, имена шаблонов лежат в
/// нодах, и разбор живёт одной реализацией в <see cref="MacroTemplateAnalysis"/> (Shared) —
/// тот же приём, что у бейджа целей в D4, и с тем же правилом «второй копии правила не заводить».
/// Запроса «кто ссылается» в протоколе нет и не нужно.
///
/// <b>Картинки — по одной, за выделением.</b> Список это метаданные, они мелкие; PNG — килобайты
/// каждый, а труба общая с потоком событий прогона. Поэтому байты запрашиваются на смену
/// выделения, кэшируются на время жизни режима (повторный клик по строке бесплатен), и файл
/// крупнее <see cref="TemplateLimits.MaxImageBytes"/> не запрашивается вовсе — его размер уже
/// известен из списка.
///
/// <b>Список читается заново на каждый вход в режим</b> (и по кнопке «Обновить»): пользователь
/// кладёт PNG в папку именно затем, чтобы на него посмотреть, и снимок с момента старта демона
/// был бы для этого бесполезен. Пуша про изменение дерева в протоколе нет — <c>FileSystemWatcher</c>
/// заведён на <c>macros/</c>, а не на ассеты.
/// </summary>
public sealed class TemplatesViewModel : ObservableObject, IDisposable
{
    private readonly IIpcClient _client;
    private readonly IUiDispatcher _dispatcher;

    // Кэш превью на время жизни режима. Ключ — та же пара «набор + имя», что и идентичность
    // шаблона; счёт записей равен числу шаблонов, которые пользователь успел ткнуть.
    private readonly Dictionary<(string? Set, string Name), byte[]> _previews = [];

    private IReadOnlyList<TemplateDto> _files = [];
    private IReadOnlyList<MacroGraph> _macros = [];

    private TemplateRowViewModel? _selected;
    private byte[]? _previewPng;
    private string? _previewProblem;
    private bool _isLoading;

    public TemplatesViewModel(IIpcClient client, IUiDispatcher? dispatcher = null)
    {
        _client = client;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;

        if (_client.IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Разделы списка: сперва одиночные шаблоны, затем наборы по алфавиту.</summary>
    public ObservableCollection<TemplateGroupViewModel> Groups { get; } = [];

    /// <summary>Все строки одним списком — счётчик рейки и тесты смотрят сюда.</summary>
    public ObservableCollection<TemplateRowViewModel> Templates { get; } = [];

    /// <summary>Имена, которые называют ноды, но которых в дереве нет.</summary>
    public ObservableCollection<MissingTemplateViewModel> Missing { get; } = [];

    /// <summary><c>true</c>, когда разделу «нет файла» есть что показать.</summary>
    public bool HasMissing => Missing.Count > 0;

    /// <summary>Заголовок раздела «нет файла», капсом, — как «НЕ ОПОЗНАНО · 3» в «Окнах».</summary>
    public string MissingHeaderText =>
        string.Create(CultureInfo.CurrentCulture, $"НЕТ ФАЙЛА · {Missing.Count}");

    /// <summary>Строка шапки: «14 файлов · наборов: 1».</summary>
    public string SummaryText
    {
        get
        {
            if (Templates.Count == 0)
            {
                return "дерево шаблонов пусто";
            }

            var sets = Groups.Count(group => group.IsSet);
            var files = FilesWord(Templates.Count);
            return sets == 0
                ? files
                : string.Create(CultureInfo.CurrentCulture, $"{files} · наборов: {sets}");
        }
    }

    /// <summary><c>true</c>, пока демон не сообщает ни одного файла, — пустое состояние режима.</summary>
    public bool IsEmpty => Templates.Count == 0;

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

    /// <summary>«pw-boot / find-server» на каждую ссылку выбранного шаблона.</summary>
    public IReadOnlyList<string> SelectedUsages => _selected is null
        ? []
        : [.. _selected.UsedBy.Select(r => $"{r.MacroName} / {r.NodeName}")];

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

    /// <summary>Идёт запрос списка — гасит кнопку «Обновить».</summary>
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

    /// <summary>
    /// Поднимается после любой пересборки списка — по нему оболочка обновляет счётчик рейки, не
    /// спрашивая ни о чём демон.
    /// </summary>
    public event Action? TemplatesChanged;

    /// <summary>
    /// Перечитывает дерево у демона и заново считает «кто чем пользуется».
    ///
    /// Превью НЕ сбрасывает: файлы обычно те же самые, а гашение картинки при каждом входе в
    /// режим читалось бы как мигание. Устаревшие записи вымываются сами — ключ у них тот же, а
    /// строка после обновления новая.
    /// </summary>
    public async Task RefreshAsync()
    {
        // Через диспетчер даже здесь: RefreshAsync зовётся и из обработчика Connected, а тот
        // поднимается на потоке читателя IPC — уведомление об изменении свойства оттуда пошло бы
        // в привязку IsEnabled кнопки мимо потока UI.
        _dispatcher.Post(() => IsLoading = true);
        try
        {
            var files = await _client.RequestAsync<TemplateDto[]>(IpcMessageTypes.GetTemplates)
                .ConfigureAwait(false);
            var macros = await _client.RequestAsync<MacroGraph[]>(IpcMessageTypes.GetMacros)
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                _files = files ?? [];
                _macros = macros ?? [];
                Rebuild();
                IsLoading = false;
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // Обрыв между подключением и запросом. Поддерживающий цикл переподключится, снова
            // сработает Connected, и он это повторит.
            Log.Warning(ex, "Не удалось получить список шаблонов");
            _dispatcher.Post(() => IsLoading = false);
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
        // Библиотека изменилась — значит, изменился и ответ на «кому нужен этот шаблон», причём
        // без всякого движения в самом дереве файлов. Список файлов перечитывается заодно: он
        // маленький, а два запроса дешевле, чем отдельный путь пересчёта.
        if (string.Equals(evt.Type, IpcMessageTypes.MacrosChanged, StringComparison.Ordinal))
        {
            _ = RefreshAsync();
        }
    }

    private async Task LoadPreviewAsync(TemplateRowViewModel? row)
    {
        PreviewProblem = null;
        if (row is null)
        {
            PreviewPng = null;
            return;
        }

        if (_previews.TryGetValue((row.Set, row.Name), out var cached))
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
                    new GetTemplateImageRequest(row.Set, row.Name))
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

        _previews[(image.Set, image.Name)] = image.Png;

        // Гонка выделения: пользователь щёлкает быстрее, чем отвечает демон, и ответ на
        // предпоследний выбор может прийти последним. Без этой сверки в превью осталась бы
        // картинка не той строки, что подсвечена.
        if (image.Describes(_selected?.Set, _selected?.Name ?? string.Empty))
        {
            PreviewPng = image.Png;
        }
    }

    // ---- пересборка ---------------------------------------------------------------------------

    private void Rebuild()
    {
        var usage = MacroTemplateAnalysis.Analyze(_macros);

        // Ссылки на ОДИНОЧНЫЙ файл ключуются его именем; ссылки на НАБОР — именем папки, и
        // достаются они каждому файлу этого набора: RecognizeTag называет набор целиком, так что
        // «кому нужен Лучник.png» честно отвечается через «кому нужен classes».
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

        // Обратная сторона: имя названо, файла нет. Считается тем же разбором, только с другого
        // конца, — и это тот случай, который до сих пор всплывал лишь в момент прогона.
        var haveSets = _files.Where(f => f.Set is not null).Select(f => f.Set!).ToHashSet(StringComparer.Ordinal);
        var haveSingles = _files.Where(f => f.Set is null).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        Missing.Clear();
        foreach (var missing in usage.Where(u => u.IsSet ? !haveSets.Contains(u.Name) : !haveSingles.Contains(u.Name)))
        {
            Missing.Add(new MissingTemplateViewModel(missing));
        }

        // Выделение переживает обновление, пока файл на месте, — иначе кнопка «Обновить»
        // выбрасывала бы пользователя из того шаблона, который он разглядывает.
        _selected = null;
        Selected = previousKey is { } key
            ? rows.FirstOrDefault(row =>
                string.Equals(row.Set, key.Item1, StringComparison.Ordinal)
                && string.Equals(row.Name, key.Item2, StringComparison.Ordinal))
            : null;

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(MissingHeaderText));
        TemplatesChanged?.Invoke();
    }

    private static string FilesWord(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => string.Create(CultureInfo.CurrentCulture, $"{count} файл"),
        (2 or 3 or 4, not (12 or 13 or 14)) => string.Create(CultureInfo.CurrentCulture, $"{count} файла"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{count} файлов"),
    };
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
