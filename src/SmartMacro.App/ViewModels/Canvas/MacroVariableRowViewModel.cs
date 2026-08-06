using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Analysis;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// Одна карточка блока «переменные макроса» в инспекторе: <c>{tag} · строка · «Жрец»</c>, а под
/// ней «пишет recognize-class» и «читает set-icon — в пути к иконке».
///
/// Два источника, сходящиеся здесь и больше нигде:
///
///   · СТРУКТУРА приходит из <see cref="MacroVariableAnalysis"/> — это чистая функция от графа,
///     поэтому она есть ещё до того, как хоть что-нибудь запускалось, и обновляется по мере
///     правки графа;
///   · ЗНАЧЕНИЕ приходит из событий <c>VariableSet</c> выбранного обхода, поэтому оно есть
///     только с того момента, как этот обход действительно его присвоил.
///
/// То, что русский язык живёт на этой стороне, — то же правило, которому следует лог прогона:
/// анализ называет <see cref="VariableSlot"/>, а панель решает, что читается это как
/// «в пути к иконке».
/// </summary>
public sealed class MacroVariableRowViewModel : ObservableObject
{
    private string? _value;
    private bool _isHighlighted;

    internal MacroVariableRowViewModel(MacroVariableInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        Name = string.Create(CultureInfo.InvariantCulture, $"{{{info.Name}}}");
        KindText = info.Kind switch
        {
            VariableKind.Point => Strings.Editor_Variables_KindPoint,
            VariableKind.Text => Strings.Editor_Variables_KindText,
            _ => Strings.Editor_Variables_KindAny,
        };
        WrittenBy = info.SeededByTrigger && info.Writes.Count == 0
            ? Strings.Editor_Variables_SeededByTrigger
            : info.Writes.Count == 0
                ? Strings.Editor_Variables_Nobody
                : string.Join(", ", info.Writes.Select(w => w.NodeName));
        ReadBy = info.Reads.Count == 0
            ? Strings.Editor_Variables_Nobody
            : string.Join(", ", info.Reads.Select(r => r.NodeName));
        // Хвостовое «в пути к иконке» печатается, только если ВСЕ читатели читают переменную в
        // одном и том же поле. При разных слотах однострочная форма была прямо неверной —
        // «step-3, step-5 · в точке клика» утверждала, что нода тега читала точку клика, — так
        // что слоты уезжают в tooltip, а в строке остаются одни имена читателей.
        var slots = info.Reads.Select(r => r.Slot).Distinct().ToList();
        ReadWhere = slots.Count == 1 ? Describe(slots[0]) : null;
        ReadDetail = info.Reads.Count == 0
            ? null
            : string.Join("\n", info.Reads.Select(r => $"{r.NodeName} — {Describe(r.Slot)}"));
    }

    /// <summary>Анализ, стоящий за этой строкой, — из него выводится подсветка на canvas.</summary>
    public MacroVariableInfo Info { get; }

    /// <summary>С фигурными скобками, ровно как переменную пишут в ноде: <c>{tag}</c>.</summary>
    public string Name { get; }

    /// <summary>Голое имя — для сопоставления с полями ноды.</summary>
    public string RawName => Info.Name;

    /// <summary>«точка» / «строка» / «значение».</summary>
    public string KindText { get; }

    /// <summary>Подписи нод, которые её присваивают, либо «триггер (сид)» / «никто».</summary>
    public string WrittenBy { get; }

    /// <summary>Подписи нод, которые её потребляют, либо «никто».</summary>
    public string ReadBy { get; }

    /// <summary>
    /// В каком поле её читают, но ТОЛЬКО когда все читатели сходятся; иначе <c>null</c>, а
    /// разбивка по каждому читателю живёт в <see cref="ReadDetail"/>.
    /// </summary>
    public string? ReadWhere { get; }

    /// <summary>По строке «нода — поле» на каждого читателя, для tooltip. <c>null</c>, если её никто не читает.</summary>
    public string? ReadDetail { get; }

    /// <summary><c>true</c>, когда нода её читает, а не присваивает никто, — это прерванный прогон, который только и ждёт своего часа.</summary>
    public bool IsUndefined => !Info.IsDefined;

    /// <summary>
    /// Значение из выбранного обхода либо <c>null</c>, если этот обход его ещё не присвоил.
    /// Намеренно пусто, а не «—»: пустая ячейка читается как «ещё нет», а прочерк — как «пустая
    /// строка», и это разные баги.
    /// </summary>
    public string? Value
    {
        get => _value;
        internal set
        {
            if (SetField(ref _value, value))
            {
                OnPropertyChanged(nameof(HasValue));
            }
        }
    }

    /// <summary><c>true</c>, когда у выбранного обхода для неё есть значение.</summary>
    public bool HasValue => _value is not null;

    /// <summary>Указатель над этой карточкой — canvas подсвечивает того, кто пишет, и тех, кто читает.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        internal set => SetField(ref _isHighlighted, value);
    }

    /// <summary>Русское название для <see cref="VariableSlot"/> — то самое «в пути к иконке» с макета.</summary>
    public static string Describe(VariableSlot slot) => slot switch
    {
        VariableSlot.FoundPointVar => Strings.Editor_Variables_SlotFoundPoint,
        VariableSlot.ResultVar => Strings.Editor_Variables_SlotResult,
        VariableSlot.PointVar => Strings.Editor_Variables_SlotPoint,
        VariableSlot.Tag => Strings.Editor_Variables_SlotTag,
        VariableSlot.IconPath => Strings.Editor_Variables_SlotIcon,
        _ => string.Empty,
    };
}
