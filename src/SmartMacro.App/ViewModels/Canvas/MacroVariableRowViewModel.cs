using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Analysis;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// One card of the inspector's «переменные макроса» block: <c>{tag} · строка · «Жрец»</c> over
/// «пишет recognize-class» and «читает set-icon — в пути к иконке».
///
/// Two sources, joined here and nowhere else:
///
///   · the STRUCTURE comes from <see cref="MacroVariableAnalysis"/> — a pure function of the
///     graph, so it is there before anything has ever run and it updates as the graph is
///     edited;
///   · the VALUE comes from the selected walk's <c>VariableSet</c> events, so it is present
///     only while (and after) that walk has actually assigned it.
///
/// Keeping the Russian on this side is the same rule the run log follows: the analysis names
/// a <see cref="VariableSlot"/>, the panel decides it reads «в пути к иконке».
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
            VariableKind.Point => "точка",
            VariableKind.Text => "строка",
            _ => "значение",
        };
        WrittenBy = info.SeededByTrigger && info.Writes.Count == 0
            ? "триггер (сид)"
            : info.Writes.Count == 0
                ? "никто"
                : string.Join(", ", info.Writes.Select(w => w.NodeId));
        ReadBy = info.Reads.Count == 0
            ? "никто"
            : string.Join(", ", info.Reads.Select(r => r.NodeId));
        // The trailing «в пути к иконке» is only printed when EVERY reader reads it in the
        // same field. With mixed slots the one-line form was flatly wrong — «step-3, step-5 ·
        // в точке клика» claimed the tag node read a click point — so the slots move into the
        // tooltip and the line just names the readers.
        var slots = info.Reads.Select(r => r.Slot).Distinct().ToList();
        ReadWhere = slots.Count == 1 ? Describe(slots[0]) : null;
        ReadDetail = info.Reads.Count == 0
            ? null
            : string.Join("\n", info.Reads.Select(r => $"{r.NodeId} — {Describe(r.Slot)}"));
    }

    /// <summary>The analysis behind this row — what the canvas highlight is derived from.</summary>
    public MacroVariableInfo Info { get; }

    /// <summary>Rendered with braces, the way it is written in a node: <c>{tag}</c>.</summary>
    public string Name { get; }

    /// <summary>Bare name, for matching against a node's fields.</summary>
    public string RawName => Info.Name;

    /// <summary>«точка» / «строка» / «значение».</summary>
    public string KindText { get; }

    /// <summary>Node ids that assign it, or «триггер (сид)» / «никто».</summary>
    public string WrittenBy { get; }

    /// <summary>Node ids that consume it, or «никто».</summary>
    public string ReadBy { get; }

    /// <summary>
    /// Which field it is read in, but ONLY when every reader agrees — otherwise <c>null</c>
    /// and the per-reader breakdown lives in <see cref="ReadDetail"/>.
    /// </summary>
    public string? ReadWhere { get; }

    /// <summary>One «нода — поле» line per reader, for the tooltip. <c>null</c> when nobody reads it.</summary>
    public string? ReadDetail { get; }

    /// <summary><c>true</c> when a node reads it and nothing ever assigns it — an aborted run waiting to happen.</summary>
    public bool IsUndefined => !Info.IsDefined;

    /// <summary>
    /// Value from the selected walk, or <c>null</c> when that walk has not assigned it yet.
    /// Deliberately blank rather than «—»: an empty cell reads as "not yet", a dash reads as
    /// "empty string", and those are different bugs.
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

    /// <summary><c>true</c> when the selected walk has a value for it.</summary>
    public bool HasValue => _value is not null;

    /// <summary>The pointer is over this card — the canvas is lighting its writer and its readers.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        internal set => SetField(ref _isHighlighted, value);
    }

    /// <summary>Russian for a <see cref="VariableSlot"/> — the mockup's «в пути к иконке».</summary>
    public static string Describe(VariableSlot slot) => slot switch
    {
        VariableSlot.FoundPointVar => "точка находки",
        VariableSlot.ResultVar => "результат распознавания",
        VariableSlot.PointVar => "в точке клика",
        VariableSlot.Tag => "в теге",
        VariableSlot.IconPath => "в пути к иконке",
        VariableSlot.MacroName => "в имени макроса",
        _ => string.Empty,
    };
}
