using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Macros;
using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels;

/// <summary>Одна строка браузера: шаблон, лежащий в бандле открытого макроса.</summary>
public sealed class TemplateRowViewModel : ObservableObject
{
    private bool _isSelected;

    internal TemplateRowViewModel(string? set, string name, MacroBundleTemplateInfo file,
        IReadOnlyList<TemplateReference> usedBy)
    {
        Set = set;
        Name = name;
        Bytes = file.Bytes;
        IsDecodable = file.Size is { Width: > 0, Height: > 0 };
        SizeText = IsDecodable
            ? string.Create(CultureInfo.InvariantCulture, $"{file.Size.Width}×{file.Size.Height}")
            : Strings.Editor_Templates_NotPng;
        BytesText = TemplateFormat.Bytes(file.Bytes);
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
    /// <see cref="MacroTemplateAnalysis"/> — по графу, который у панели и так есть. Ссылка на
    /// набор достаётся каждому файлу набора: <c>MatchTemplateSet</c> называет набор целиком, так что
    /// «Лучник.png никому не нужен» было бы враньём про шаблон, которым опознают лучника.
    /// </summary>
    public IReadOnlyList<TemplateReference> UsedBy { get; }

    /// <summary>«3 ноды» либо «не используется». Второе — не ошибка, просто факт.</summary>
    public string UsageText => UsedBy.Count == 0 ? Strings.Editor_Templates_Unused : Plural(UsedBy.Count);

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

    // Третья копия разбора по %10 из тех, что жили порознь; теперь форму выбирает общий
    // PluralForms, а сами формы — явные ключи ресурсов.
    internal static string Plural(int nodes) => PluralForms.Format(
        nodes,
        Strings.Editor_Templates_UsedBy_One,
        Strings.Editor_Templates_UsedBy_Few,
        Strings.Editor_Templates_UsedBy_Many);
}

/// <summary>Раздел списка: одиночные шаблоны либо один набор.</summary>
public sealed class TemplateGroupViewModel
{
    internal TemplateGroupViewModel(string? set, IReadOnlyList<TemplateRowViewModel> rows)
    {
        Set = set;
        Rows = rows;
        // «одиночные» — не имя папки, а роль: это файлы в корне, и называет их не
        // MatchTemplateSet целиком, а Find/Wait поимённо.
        Title = set ?? Strings.Editor_Templates_GroupSingles;
        CountText = rows.Count.ToString(CultureInfo.InvariantCulture);
        IsSet = set is not null;
    }

    /// <summary>Имя набора либо <c>null</c> у раздела одиночных шаблонов.</summary>
    public string? Set { get; }

    /// <summary>Подпись раздела.</summary>
    public string Title { get; }

    /// <summary>Сколько в нём файлов.</summary>
    public string CountText { get; }

    /// <summary><c>true</c> у настоящего набора — тогда рядом уместна подсказка про MatchTemplateSet.</summary>
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
/// редактора, в инспектор макроса — туда же, где триггеры и переменные.
///
/// <b>Волна F3 убрала отсюда демона целиком.</b> Список шаблонов, байты превью, импорт и удаление
/// шли четырьмя запросами (<c>GetTemplates</c>, <c>GetTemplateImage</c>, <c>AddMacroTemplate</c>,
/// <c>DeleteMacroTemplate</c>), из которых последние два F2 завела вынужденно: дерева, куда
/// пользователь ронял PNG проводником, не стало, а писать zip панель тогда ещё не могла. Теперь
/// может — и все четыре запроса исчезли из протокола вместе с потолком, который существовал ради
/// трубы (см. <see cref="MaxPreviewBytes"/>).
///
/// <b>Раздел «НЕТ ФАЙЛА» отсюда пропал ещё в F2, и это повышение класса ошибки.</b> «Нода называет
/// шаблон, которого нет» ловит ВАЛИДАТОР — при сохранении и при загрузке библиотеки демоном, —
/// потому что набор шаблонов бандла известен статически.
///
/// <b>Картинки — по одной, за выделением.</b> Довод изменился, но остался: раньше PNG делили трубу
/// с потоком событий прогона, теперь каждое превью — это открытие zip. Байты читаются на смену
/// выделения и кэшируются на время жизни панели, так что повторный клик по строке бесплатен.
/// </summary>
public sealed class TemplatesViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Потолок на КАРТИНКУ ПРЕВЬЮ.
    ///
    /// <b>До F3 это был предел протокола</b>, и обоснование было про трубу: многомегабайтная
    /// строка base64 встала бы перед пачкой событий работающего макроса, а очередь соединения не
    /// бесконечна. Трубы больше нет, и вместе с ней ушёл потолок на ИМПОРТ: у панели нет никаких
    /// оснований отказываться положить в бандл файл, который формат прекрасно вмещает.
    ///
    /// Здесь потолок остался по другой, местной причине: панель превью — это картинка размером с
    /// ладонь, а декодирование произвольного блоба в <c>Bitmap</c> держит неуправляемую память
    /// Skia. Файл больше мегабайта в этой рамке всё равно не разглядеть, а его размер строка уже
    /// показывает. Поэтому вместо превью выводится причина.
    ///
    /// ⚠️ <b>На диалог выбора области (issue #29) он НЕ распространяется, и это не упущение.</b>
    /// Кадр 3840×2160 превышает его на порядок, а разглядывание кадра — единственное, ради чего
    /// то окно существует: применить потолок там значило бы отменить саму возможность. Причина же
    /// потолка (ладонь и чужая память) там не выполняется — окно во весь экран, картинка ровно
    /// одна, и закрытие её сразу освобождает. Вырезка, которая из того окна приезжает СЮДА, —
    /// обычный маленький шаблон и под потолком проходит.
    /// </summary>
    public const long MaxPreviewBytes = 1024 * 1024;

    private readonly MacroLibrary _library;
    private readonly IUiDispatcher _dispatcher;

    // Кэш превью. Ключ — тройка «макрос + набор + имя», то есть полная идентичность шаблона:
    // одноимённые шаблоны двух макросов — разные картинки, и общий ключ показал бы чужую.
    private readonly Dictionary<(string Macro, string? Set, string Name), byte[]> _previews = [];

    private string? _macroName;

    // ВСЕ графы бандла: сам макрос и каждый его под-макрос (волна F4). Список, а не один граф,
    // потому что папка templates/ у бандла одна на всех, и шаблон, названный только из функции,
    // обязан перестать быть «не используется» — иначе браузер утверждал бы про файл ровно
    // обратное тому, что сделает исполнитель.
    private IReadOnlyList<MacroGraph> _graphs = [];
    private IReadOnlyList<(string? Set, string Name, MacroBundleTemplateInfo File)> _files = [];

    private TemplateRowViewModel? _selected;
    private byte[]? _previewPng;
    private string? _previewProblem;
    private string? _importProblem;
    private string _importSet = string.Empty;

    public TemplatesViewModel(MacroLibrary library, IUiDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;
        _library.Changed += OnLibraryChanged;
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
        : MacroTemplateInventory.FromPaths(_files.Select(file => file.File.Path));

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
            LoadPreview(value);
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
            ? Strings.Editor_Templates_NoUsages
            : string.Format(CultureInfo.CurrentCulture, Strings.Editor_Templates_Usages,
                SelectedUsages.Count);

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

    /// <summary>Почему превью нет: слишком велик, не PNG, файл не читается. <c>null</c>, когда всё в порядке.</summary>
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

    /// <summary>Поднимается после любой пересборки списка.</summary>
    public event Action? TemplatesChanged;

    /// <summary>
    /// Наводит браузер на макрос, открытый в редакторе. <c>null</c> — редактор закрыт; тогда
    /// список пуст и на диск никто не ходит.
    ///
    /// Графы передаются вместе с именем, потому что «какие ноды называют этот шаблон» считается по
    /// НИМ, а не по тому, что лежит на диске: набрал имя шаблона в ноде — и строка сразу
    /// перестала быть «не используется», ещё до сохранения. Графов несколько, потому что папка
    /// шаблонов у бандла одна, а называть их могут и макрос, и любой его под-макрос (F4).
    /// </summary>
    public void ShowMacro(string? macroName, IReadOnlyList<MacroGraph>? graphs)
    {
        var macroChanged = !string.Equals(_macroName, macroName, StringComparison.Ordinal);
        _macroName = macroName;
        _graphs = graphs ?? [];

        OnPropertyChanged(nameof(MacroName));
        OnPropertyChanged(nameof(HasMacro));

        if (macroName is null)
        {
            _files = [];
            _selected = null;
            PreviewPng = null;
            PreviewProblem = null;
            ImportProblem = null;
            Rebuild();
            return;
        }

        if (macroChanged)
        {
            // Другой макрос — другие файлы.
            _selected = null;
            PreviewPng = null;
            PreviewProblem = null;
            ImportProblem = null;
            Refresh();
        }
        else
        {
            // Тот же макрос, новый граф (правят ноды) — файлы те же, пересчитать надо только
            // «кто на что ссылается».
            Rebuild();
        }
    }

    /// <summary>Перечитывает перечень шаблонов открытого макроса прямо из бандла.</summary>
    public void Refresh()
    {
        if (_macroName is not { } macroName)
        {
            return;
        }

        _files =
        [
            .. _library.TemplateCatalog(macroName)
                .Select(file => (Ok: MacroBundleFormat.TryParseTemplatePath(file.Path, out var set, out var name),
                    Set: set, Name: name, File: file))
                .Where(row => row.Ok)
                .Select(row => (row.Set, row.Name, row.File))
        ];
        Rebuild();
    }

    /// <summary>
    /// Кладёт PNG в бандл открытого макроса. Имя шаблона = основа имени файла, набор —
    /// <see cref="ImportSet"/>.
    /// </summary>
    /// <param name="fileName">Имя выбранного файла (с расширением).</param>
    /// <param name="png">Его содержимое.</param>
    public bool Import(string fileName, byte[] png)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(png);

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            ImportProblem = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Templates_BadName, fileName);
            return false;
        }

        return Add(string.IsNullOrWhiteSpace(_importSet) ? null : _importSet.Trim(), name, png);
    }

    /// <summary>
    /// Имена шаблонов, уже лежащие в этом наборе (или в корне, когда <paramref name="set"/> —
    /// <c>null</c>). Нужны диалогу выбора области, чтобы предупредить о замене.
    ///
    /// ⚠️ Сравнение набора — ORDINAL, как у исполнителя и у описи: <c>classes</c> и
    /// <c>Classes</c> — две разные подпапки бандла, и <c>RecognizeTag</c> найдёт ровно ту, что
    /// названа. Регистронезависимое сравнение обещало бы замену там, где её не будет.
    /// </summary>
    public IReadOnlyList<string> NamesIn(string? set) =>
    [
        .. _files
            .Where(file => string.Equals(file.Set, set, StringComparison.Ordinal))
            .Select(file => file.Name)
    ];

    /// <summary>
    /// Кладёт PNG в бандл открытого макроса под явными набором и именем.
    ///
    /// Отдельно от <see cref="Import"/>, потому что зовущих у этого двое и знают они разное:
    /// «+ файл…» берёт имя из имени файла и набор из поля рядом с кнопкой, а диалог выбора области
    /// спрашивает имя у человека и берёт набор у самой ноды.
    /// </summary>
    /// <param name="set">Набор (подпапка) либо <c>null</c> — одиночный шаблон в корне.</param>
    /// <param name="name">Основа имени файла — то, что несёт нода (для набора это тег).</param>
    /// <param name="png">Содержимое файла.</param>
    /// <returns><c>false</c>, если макрос не открыт или бандл не принял файл; причина — в <see cref="ImportProblem"/>.</returns>
    public bool Add(string? set, string name, byte[] png)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(png);

        ImportProblem = null;
        if (_macroName is not { } macroName)
        {
            return false;
        }

        try
        {
            if (!_library.AddTemplate(macroName, set, name, png))
            {
                ImportProblem = string.Format(CultureInfo.CurrentCulture,
                    Strings.Editor_Templates_AddFailedUnreadable, name);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ImportProblem = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Templates_AddFailed, ex.Message);
            return false;
        }

        // Байты у нас на руках — класть их в кэш превью сразу дешевле, чем читать обратно.
        _previews[(macroName, set, name)] = png;
        Refresh();
        return true;
    }

    /// <summary>Убирает шаблон из бандла открытого макроса.</summary>
    public void Delete(TemplateRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        ImportProblem = null;
        if (_macroName is not { } macroName)
        {
            return;
        }

        try
        {
            _library.DeleteTemplate(macroName, row.Set, row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ImportProblem = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Templates_DeleteFailed, ex.Message);
            return;
        }

        _previews.Remove((macroName, row.Set, row.Name));
        Refresh();
    }

    public void Dispose() => _library.Changed -= OnLibraryChanged;

    // ---- проводка к папке --------------------------------------------------------------------

    // Бандл изменился на диске — это могла быть и наша собственная правка, и чужая правка файла в
    // проводнике. Дешевле перечитать список, чем заводить отдельный путь на каждый случай.
    private void OnLibraryChanged() => _dispatcher.Post(Refresh);

    private void LoadPreview(TemplateRowViewModel? row)
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
            PreviewProblem = Strings.Editor_Templates_PreviewNotPng;
            return;
        }

        if (row.Bytes > MaxPreviewBytes)
        {
            // Размер файла уже есть в списке, так что читать его ради отказа не надо.
            PreviewProblem =
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Editor_Templates_PreviewTooBig,
                    row.BytesText,
                    TemplateFormat.Bytes(MaxPreviewBytes));
            return;
        }

        byte[]? png;
        try
        {
            png = _library.ReadTemplate(macroName, row.Set, row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Шаблон '{Template}' макроса '{Macro}' не прочитан", row.FullName, macroName);
            PreviewProblem = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Templates_PreviewFailed, ex.Message);
            return;
        }

        if (png is null)
        {
            // Список устарел: бандл сменился между перечислением и чтением.
            PreviewProblem = Strings.Editor_Templates_PreviewGone;
            return;
        }

        _previews[(macroName, row.Set, row.Name)] = png;
        PreviewPng = png;
    }

    // ---- пересборка ---------------------------------------------------------------------------

    private void Rebuild()
    {
        // Ссылки на ОДИНОЧНЫЙ файл ключуются его именем; ссылки на НАБОР — именем папки, и
        // достаются они каждому файлу этого набора: MatchTemplateSet называет набор целиком.
        // Ссылки со ВСЕХ графов бандла сливаются по имени: одно и то же имя, названное и
        // родителем, и функцией, — это два места, где шаблон используется, и показать надо оба.
        var usage = _graphs.SelectMany(MacroTemplateAnalysis.Analyze).ToList();
        var singles = usage
            .Where(u => !u.IsSet)
            .GroupBy(u => u.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TemplateReference>)[.. group.SelectMany(u => u.References)],
                StringComparer.Ordinal);
        var sets = usage
            .Where(u => u.IsSet)
            .GroupBy(u => u.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TemplateReference>)[.. group.SelectMany(u => u.References)],
                StringComparer.Ordinal);

        var rows = _files
            .Select(file => new TemplateRowViewModel(
                file.Set,
                file.Name,
                file.File,
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
        ? string.Format(CultureInfo.CurrentCulture, Strings.Editor_Templates_Bytes, bytes)
        : bytes < 1024 * 1024
            ? string.Format(
                CultureInfo.CurrentCulture,
                Strings.Editor_Templates_Kilobytes,
                (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture))
            : string.Format(
                CultureInfo.CurrentCulture,
                Strings.Editor_Templates_Megabytes,
                (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.CurrentCulture));
}
