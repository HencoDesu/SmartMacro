using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Model;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>Один тег из списка require/exclude селектора, снимаемым чипом (макет 1g).</summary>
public sealed class SelectorTagChipViewModel
{
    private readonly Action<string> _remove;

    internal SelectorTagChipViewModel(string text, Action<string> remove)
    {
        Text = text;
        _remove = remove;
    }

    /// <summary>Сам тег.</summary>
    public string Text { get; }

    /// <summary>Выбрасывает этот тег из списка, которому он принадлежит.</summary>
    public void Remove() => _remove(Text);
}

/// <summary>Одно окно, которое бейдж называет в развёрнутом виде.</summary>
/// <param name="Label">«0x1402F8 Лучник» — хэндл плюс все теги, которые на нём висят.</param>
/// <param name="IsExcluded">Рисуется перечёркнутым: селектор пропускает его намеренно.</param>
public sealed record TargetWindowChip(string Label, bool IsExcluded);

/// <summary>
/// Редактор <see cref="TargetSelector"/> у ноды: два списка тегов через запятую плюс (с D4)
/// живой бейдж «8 окон · кроме Склад» над ними.
///
/// <see cref="UseSelector"/> — это то, чем различаются две вещи, которые в модели значат
/// «селектор null» и «селектор пустой» и которых одними полями тегов не выразить:
///   * выключен — <c>Target = null</c>: действовать на КОНТЕКСТНОЕ окно прогона;
///   * включён при обоих пустых полях — <c>Target = new TargetSelector()</c>: разойтись
///     веером по КАЖДОМУ зарегистрированному окну.
/// Без этого флага оба случая схлопываются в один, и граф не пережил бы round trip
/// «загрузить — сохранить».
///
/// <b>Бейдж считается здесь, по тому же правилу, каким пользуется движок.</b>
/// <see cref="TargetSelector.Matches"/> живёт в Shared именно для того, чтобы этот класс и
/// демонский <c>SelectorEvaluator</c> не могли по-разному ответить на вопрос «по каким окнам
/// это ударит»: бейдж, расходящийся с исполнителем, был бы хуже, чем полное его отсутствие,
/// ведь сказать, куда попадёт прогон, — вся его цель. Список окон приходит из
/// <see cref="Windows"/>, живого каталога редактора; когда каталог не подключён (модульные
/// тесты round trip, панель, которая ещё не засеялась) бейдж честно говорит «нет окон», а не
/// выдумывает число.
/// </summary>
public sealed class TargetSelectorViewModel : ObservableObject
{
    /// <summary>Сколько окон бейдж называет поимённо, прежде чем свернуть остаток в «+N».</summary>
    private const int MaxNamedWindows = 4;

    private bool _useSelector;
    private string _requireText = string.Empty;
    private string _excludeText = string.Empty;
    private string _newRequireTag = string.Empty;
    private string _newExcludeTag = string.Empty;
    private WindowCatalog? _windows;

    /// <summary><c>false</c> = действовать на контекстное окно (<c>Target = null</c>).</summary>
    public bool UseSelector
    {
        get => _useSelector;
        set => SetField(ref _useSelector, value);
    }

    /// <summary>Теги через запятую, которые окно обязано нести все до одного.</summary>
    [AllowNull]
    public string RequireText
    {
        get => _requireText;
        set
        {
            if (SetField(ref _requireText, value ?? string.Empty))
            {
                RebuildChips();
            }
        }
    }

    /// <summary>Теги через запятую, любой из которых снимает окно с дистанции.</summary>
    [AllowNull]
    public string ExcludeText
    {
        get => _excludeText;
        set
        {
            if (SetField(ref _excludeText, value ?? string.Empty))
            {
                RebuildChips();
            }
        }
    }

    /// <summary>
    /// Живой снимок окон у редактора, общий для всех селекторов открытого графа. Проставляет
    /// его <c>MacroEditorViewModel</c>, когда подцепляет строку ноды; в изоляции он
    /// <c>null</c>, и бейдж честно об этом говорит.
    /// </summary>
    public WindowCatalog? Windows
    {
        get => _windows;
        set
        {
            if (ReferenceEquals(_windows, value))
            {
                return;
            }

            if (_windows is not null)
            {
                _windows.Changed -= OnWindowsChanged;
            }

            _windows = value;
            if (_windows is not null)
            {
                _windows.Changed += OnWindowsChanged;
            }

            OnWindowsChanged();
        }
    }

    // ---- текст селектора (то, что хранит нода) -----------------------------------------

    /// <summary>
    /// Что говорит САМ СЕЛЕКТОР, без числа окон: «кроме Склад», «Лучник», «все окна». Живое
    /// число бейдж приписывает перед этим.
    /// </summary>
    public string Summary
    {
        get
        {
            if (!_useSelector)
            {
                return "контекст-окно";
            }

            var require = _requireText.Trim();
            var exclude = _excludeText.Trim();
            return (require.Length, exclude.Length) switch
            {
                (0, 0) => "все окна",
                (_, 0) => require,
                (0, _) => $"кроме {exclude}",
                _ => $"{require} · кроме {exclude}",
            };
        }
    }

    // ---- бейдж (макет 1g) --------------------------------------------------------------

    /// <summary>
    /// Свёрнутый бейдж: «8 окон · кроме Склад». Число идёт первым, потому что это и есть тот
    /// вопрос, ради ответа на который бейдж существует, — теги и так лежат в инспекторе ниже.
    /// </summary>
    public string BadgeText
    {
        get
        {
            if (!_useSelector)
            {
                return "1 окно · контекст";
            }

            if (TotalCount == 0)
            {
                return "нет окон";
            }

            var count = string.Create(CultureInfo.CurrentCulture, $"{MatchCount} {Plural(MatchCount)}");
            var require = _requireText.Trim();
            var exclude = _excludeText.Trim();
            return (require.Length, exclude.Length) switch
            {
                (0, 0) => count,
                (_, 0) => $"{count} · {require}",
                (0, _) => $"{count} · кроме {exclude}",
                _ => $"{count} · {require} · кроме {exclude}",
            };
        }
    }

    /// <summary>
    /// Тот же бейдж, с которого сняли теги: «7 окон», «контекст», «нет окон».
    ///
    /// Это то, что показывает коробка на canvas. Коробка ноды шириной 210px и уже несёт
    /// подпись типа и глиф семейства; полное «7 окон · кроме Лучник, Шаман» вытесняет подпись
    /// из существования, а подпись — это то, что говорит, ЧТО нода делает. Число и есть тот
    /// заголовок, ради которого бейдж заведён, остальное — в одном наведении (или в одном
    /// клике по коробке) отсюда.
    /// </summary>
    public string BadgeCountText
    {
        get
        {
            if (!_useSelector)
            {
                return "контекст";
            }

            if (TotalCount == 0)
            {
                return "нет окон";
            }

            return string.Create(CultureInfo.CurrentCulture, $"{MatchCount} {Plural(MatchCount)}");
        }
    }

    /// <summary>Сколько окон селектор задевает прямо сейчас. Пока <see cref="UseSelector"/> выключен — величина бессмысленная.</summary>
    public int MatchCount { get; private set; }

    /// <summary>Сколько окон демон отслеживает вообще.</summary>
    public int TotalCount => _windows?.Count ?? 0;

    /// <summary>
    /// Акцентный бейдж: селектор маршрутизирует по тегам и во что-то попадает. И это, и
    /// <see cref="BadgeIsDanger"/> ложны для случая контекстного окна — там нейтральная
    /// пилюля.
    /// </summary>
    public bool BadgeIsAccent => _useSelector && TotalCount > 0 && MatchCount > 0;

    /// <summary>
    /// Ноль совпадений, нарисованный ошибкой: макет прямо говорит, что «0 окон» не должно
    /// читаться нейтральным числом.
    ///
    /// Для этого нужно, чтобы окна вообще были. При закрытой игре НИ ОДИН селектор ни во что не
    /// попадает, и красить каждую ноду каждого макроса в красный оттого, что ничего не
    /// запущено, — значит выучить пользователя не замечать цвет, который должен означать «этот
    /// селектор неверен». Такому случаю достаётся своё тихое «нет окон».
    /// </summary>
    public bool BadgeIsDanger => _useSelector && TotalCount > 0 && MatchCount == 0;

    /// <summary>Четыре полоски доли. <c>true</c> = закрашена; ненулевая доля всегда закрашивает хотя бы одну.</summary>
    public IReadOnlyList<bool> Bars { get; private set; } = [false, false, false, false];

    /// <summary>Полоски рисуются только тогда, когда есть что показывать, — см. <see cref="ShowsHollowDot"/>.</summary>
    public bool ShowsBars => _useSelector && TotalCount > 0 && MatchCount > 0;

    /// <summary>Отметка «ноль совпадений»: залитая тревожная точка там, где были бы полоски доли.</summary>
    public bool ShowsDangerDot => BadgeIsDanger;

    /// <summary>
    /// Тихая отметка: контурное колечко. Сюда попадают и маршрутизация по контекстному окну, и
    /// «демон не отслеживает ничего» — ни то ни другое не ошибка, и ни у того ни у другого нет
    /// доли, которую можно нарисовать.
    /// </summary>
    public bool ShowsHollowDot => !ShowsBars && !BadgeIsDanger;

    /// <summary>«8 из 11» в подвале развёрнутого popup.</summary>
    public string HitText => TotalCount == 0
        ? "нет окон под управлением"
        : string.Create(CultureInfo.CurrentCulture, $"{MatchCount} из {TotalCount}");

    /// <summary>До <see cref="MaxNamedWindows"/> окон, по которым ударит прогон, — хэндлом и тегами.</summary>
    public ObservableCollection<TargetWindowChip> HitWindows { get; } = [];

    /// <summary>Помеченные окна, которые селектор пропускает; в popup они перечёркнуты.</summary>
    public ObservableCollection<TargetWindowChip> MissedWindows { get; } = [];

    /// <summary>«+4», когда совпало больше окон, чем popup называет поимённо. Иначе пусто.</summary>
    public string MoreText { get; private set; } = string.Empty;

    /// <summary><c>true</c>, когда <see cref="MoreText"/> есть что показать.</summary>
    public bool HasMore => MoreText.Length > 0;

    /// <summary>«2 без тегов» — окна без тегов, до которых селектору не дотянуться. Пусто, когда таких нет.</summary>
    public string UntaggedText { get; private set; } = string.Empty;

    /// <summary><c>true</c>, когда <see cref="UntaggedText"/> есть что показать.</summary>
    public bool HasUntagged => UntaggedText.Length > 0;

    // ---- чипы тегов (редактор внутри popup) ---------------------------------------------

    /// <summary>Обязательные теги снимаемыми чипами. Зеркалит <see cref="RequireText"/>.</summary>
    public ObservableCollection<SelectorTagChipViewModel> RequireChips { get; } = [];

    /// <summary>Исключающие теги снимаемыми чипами. Зеркалит <see cref="ExcludeText"/>.</summary>
    public ObservableCollection<SelectorTagChipViewModel> ExcludeChips { get; } = [];

    /// <summary>Текст поля «+ тег» в popup для списка обязательных.</summary>
    [AllowNull]
    public string NewRequireTag
    {
        get => _newRequireTag;
        set => SetField(ref _newRequireTag, value ?? string.Empty);
    }

    /// <summary>Текст поля «+ тег» в popup для списка исключений.</summary>
    [AllowNull]
    public string NewExcludeTag
    {
        get => _newExcludeTag;
        set => SetField(ref _newExcludeTag, value ?? string.Empty);
    }

    /// <summary>Фиксирует <see cref="NewRequireTag"/> (Enter в поле). Дубликаты и пустые строки игнорируются.</summary>
    public void CommitRequireTag()
    {
        if (AddTag(ref _requireText, _newRequireTag))
        {
            NewRequireTag = string.Empty;
            OnPropertyChanged(nameof(RequireText));
            RebuildChips();
        }
    }

    /// <summary>Фиксирует <see cref="NewExcludeTag"/> (Enter в поле).</summary>
    public void CommitExcludeTag()
    {
        if (AddTag(ref _excludeText, _newExcludeTag))
        {
            NewExcludeTag = string.Empty;
            OnPropertyChanged(nameof(ExcludeText));
            RebuildChips();
        }
    }

    // ---- round trip через модель -----------------------------------------------------------

    /// <summary>Собирает селектор модели либо <c>null</c>, когда целью служит контекстное окно.</summary>
    public TargetSelector? ToSelector() => _useSelector
        ? new TargetSelector
        {
            RequireTags = SplitTags(_requireText),
            ExcludeTags = SplitTags(_excludeText),
        }
        : null;

    /// <summary>Загружает селектор модели (<c>null</c> = контекстное окно).</summary>
    public static TargetSelectorViewModel FromSelector(TargetSelector? selector)
    {
        var vm = new TargetSelectorViewModel
        {
            UseSelector = selector is not null,
            RequireText = JoinTags(selector?.RequireTags),
            ExcludeText = JoinTags(selector?.ExcludeTags),
        };
        vm.RebuildChips();
        return vm;
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        // Сводку и бейдж кормит каждое поле этого объекта, поэтому вместо того чтобы поднимать
        // уведомления руками в каждом сеттере (и однажды забыть), всё, что само не является
        // производным свойством, переподнимает весь производный набор.
        if (propertyName is null || IsDerived(propertyName))
        {
            return;
        }

        base.OnPropertyChanged(nameof(Summary));
        Recompute();
    }

    private static bool IsDerived(string propertyName) => propertyName is
        nameof(Summary) or nameof(BadgeText) or nameof(BadgeCountText)
        or nameof(MatchCount) or nameof(TotalCount)
        or nameof(BadgeIsAccent) or nameof(BadgeIsDanger) or nameof(Bars) or nameof(ShowsBars)
        or nameof(ShowsDangerDot) or nameof(ShowsHollowDot)
        or nameof(HitText) or nameof(MoreText) or nameof(HasMore)
        or nameof(UntaggedText) or nameof(HasUntagged)
        or nameof(NewRequireTag) or nameof(NewExcludeTag);

    private void OnWindowsChanged() => Recompute();

    /// <summary>
    /// Пересчитывает селектор по каталогу. Дёшево по построению — десяток окон и горстка
    /// тегов, — поэтому запускается на каждое нажатие клавиши в полях тегов, а не через
    /// задержку: именно от этого число кажется приклеенным к тому, что набирают.
    /// </summary>
    private void Recompute()
    {
        var windows = _windows?.Windows ?? [];
        var selector = _useSelector ? ToSelector() : null;

        var hit = new List<WindowDto>();
        var missed = new List<WindowDto>();
        var untaggedMisses = 0;

        foreach (var window in windows)
        {
            if (selector is not null && selector.Matches(window.Tags))
            {
                hit.Add(window);
            }
            else if (window.Tags.Count == 0)
            {
                untaggedMisses++;
            }
            else
            {
                missed.Add(window);
            }
        }

        MatchCount = hit.Count;
        Bars = BuildBars(hit.Count, windows.Count);

        Replace(HitWindows, hit.Take(MaxNamedWindows).Select(w => new TargetWindowChip(Describe(w), false)));
        Replace(MissedWindows, missed.Take(MaxNamedWindows).Select(w => new TargetWindowChip(Describe(w), true)));

        var overflow = hit.Count - MaxNamedWindows;
        MoreText = overflow > 0
            ? string.Create(CultureInfo.InvariantCulture, $"+{overflow}")
            : string.Empty;
        UntaggedText = untaggedMisses > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{untaggedMisses} без тегов")
            : string.Empty;

        base.OnPropertyChanged(nameof(BadgeText));
        base.OnPropertyChanged(nameof(BadgeCountText));
        base.OnPropertyChanged(nameof(MatchCount));
        base.OnPropertyChanged(nameof(TotalCount));
        base.OnPropertyChanged(nameof(BadgeIsAccent));
        base.OnPropertyChanged(nameof(BadgeIsDanger));
        base.OnPropertyChanged(nameof(Bars));
        base.OnPropertyChanged(nameof(ShowsBars));
        base.OnPropertyChanged(nameof(ShowsDangerDot));
        base.OnPropertyChanged(nameof(ShowsHollowDot));
        base.OnPropertyChanged(nameof(HitText));
        base.OnPropertyChanged(nameof(MoreText));
        base.OnPropertyChanged(nameof(HasMore));
        base.OnPropertyChanged(nameof(UntaggedText));
        base.OnPropertyChanged(nameof(HasUntagged));
    }

    // Четыре полоски, точно по макету: 8 из 11 закрашивают три, 1 из 11 — одну. От одного лишь
    // округления единственное совпадение показывало бы пустую полосу, а она читается как
    // «ничего», — поэтому любая ненулевая доля стоит хотя бы одной полоски.
    private static IReadOnlyList<bool> BuildBars(int matched, int total)
    {
        var filled = total <= 0 || matched <= 0
            ? 0
            : Math.Clamp((int)Math.Round(matched * 4.0 / total, MidpointRounding.AwayFromZero), 1, 4);
        return [filled > 0, filled > 1, filled > 2, filled > 3];
    }

    private static string Describe(WindowDto window) => window.Tags.Count == 0
        ? string.Create(CultureInfo.InvariantCulture, $"0x{window.Hwnd:X}")
        : string.Create(CultureInfo.InvariantCulture, $"0x{window.Hwnd:X} {string.Join(' ', window.Tags)}");

    private void RebuildChips()
    {
        Rebuild(RequireChips, SplitTags(_requireText), RemoveRequireTag);
        Rebuild(ExcludeChips, SplitTags(_excludeText), RemoveExcludeTag);
    }

    private void RemoveRequireTag(string tag)
    {
        RequireText = string.Join(", ",
            SplitTags(_requireText).Where(t => !string.Equals(t, tag, StringComparison.Ordinal)));
    }

    private void RemoveExcludeTag(string tag)
    {
        ExcludeText = string.Join(", ",
            SplitTags(_excludeText).Where(t => !string.Equals(t, tag, StringComparison.Ordinal)));
    }

    private static bool AddTag(ref string list, string candidate)
    {
        var tag = candidate.Trim();
        if (tag.Length == 0)
        {
            return false;
        }

        var tags = SplitTags(list);
        if (tags.Contains(tag, StringComparer.Ordinal))
        {
            return false;
        }

        tags.Add(tag);
        list = string.Join(", ", tags);
        return true;
    }

    private static void Rebuild(
        ObservableCollection<SelectorTagChipViewModel> target,
        IReadOnlyList<string> tags,
        Action<string> remove)
    {
        target.Clear();
        foreach (var tag in tags)
        {
            target.Add(new SelectorTagChipViewModel(tag, remove));
        }
    }

    private static void Replace(ObservableCollection<TargetWindowChip> target, IEnumerable<TargetWindowChip> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    // Тег с запятой внутри здесь выразить нельзя. На практике это имена классов и прочие
    // короткие ярлыки, так что размен покупает однострочный редактор для обычного случая.
    private static List<string> SplitTags(string text) =>
    [
        .. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ];

    private static string JoinTags(IReadOnlyList<string>? tags) =>
        tags is null || tags.Count == 0 ? string.Empty : string.Join(", ", tags);

    // окно / окна / окон. Расписано руками, а не взято из библиотеки склонений, потому что
    // слово одно, а UI всё равно только русский.
    private static string Plural(int count)
    {
        var mod100 = count % 100;
        if (mod100 is >= 11 and <= 14)
        {
            return "окон";
        }

        return (count % 10) switch
        {
            1 => "окно",
            2 or 3 or 4 => "окна",
            _ => "окон",
        };
    }
}
