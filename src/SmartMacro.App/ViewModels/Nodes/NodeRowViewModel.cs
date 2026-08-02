using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>The ten node types, in the order the "add node" menu offers them.</summary>
public enum MacroNodeKind
{
    KeyPress,
    Click,
    Delay,
    AddTag,
    RemoveTag,
    SetIcon,
    RunMacro,
    FindElement,
    WaitForElement,
    RecognizeTag,
}

/// <summary>One entry of the "add node" flyout: the kind plus its Russian menu label.</summary>
/// <param name="Kind">Node type to create.</param>
/// <param name="Label">Menu text.</param>
public sealed record MacroNodeKindOption(MacroNodeKind Kind, string Label);

/// <summary>
/// One outgoing edge of a node, rendered as a drop-down of node ids.
///
/// The empty string is a first-class value here and means "no target — the run ends on
/// this outcome", which is exactly what a <c>null</c> edge means in the model. Keeping it
/// as <c>""</c> rather than <c>null</c> lets a plain <c>ComboBox</c> of strings do the
/// editing (a null <c>SelectedItem</c> is indistinguishable from "nothing selected yet").
/// </summary>
public sealed class NodeEdgeViewModel : ObservableObject
{
    private string _targetId;
    private ObservableCollection<string> _choices = [];

    public NodeEdgeViewModel(string label, string? targetId)
    {
        Label = label;
        _targetId = targetId ?? string.Empty;
    }

    /// <summary>Outcome name shown next to the drop-down ("Далее", "Найдено", …).</summary>
    public string Label { get; }

    /// <summary>Selected node id; <c>""</c> = end of run.</summary>
    public string TargetId
    {
        get => _targetId;
        // A ComboBox pushes null when its SelectedItem leaves the ItemsSource (e.g. the
        // list is rebuilt after a node is deleted). Normalising to "" turns that into the
        // meaningful "no target" value instead of a null that would blow up later.
        set => SetField(ref _targetId, value ?? string.Empty);
    }

    /// <summary>Model form of <see cref="TargetId"/>.</summary>
    public string? TargetOrNull => string.IsNullOrEmpty(_targetId) ? null : _targetId;

    /// <summary>
    /// Live list of selectable ids, owned by the editor and shared by every edge, so
    /// adding/renaming/deleting a node updates all drop-downs at once.
    /// </summary>
    public ObservableCollection<string> Choices
    {
        get => _choices;
        set => SetField(ref _choices, value);
    }
}

/// <summary>
/// Editor for a node's <see cref="TargetSelector"/>: two comma-separated tag lists.
///
/// <see cref="UseSelector"/> is what distinguishes the two things a null-vs-empty selector
/// means in the model, which the tag boxes alone cannot express:
///   * off — <c>Target = null</c>: act on the run's CONTEXT window;
///   * on with both boxes empty — <c>Target = new TargetSelector()</c>: fan out to EVERY
///     registered window.
/// Without the flag those two collapse into each other and a graph would not survive a
/// load/save round-trip.
/// </summary>
public sealed class TargetSelectorViewModel : ObservableObject
{
    private bool _useSelector;
    private string _requireText = string.Empty;
    private string _excludeText = string.Empty;

    /// <summary><c>false</c> = act on the context window (<c>Target = null</c>).</summary>
    public bool UseSelector
    {
        get => _useSelector;
        set => SetField(ref _useSelector, value);
    }

    /// <summary>Comma-separated tags a window must all carry.</summary>
    public string RequireText
    {
        get => _requireText;
        set => SetField(ref _requireText, value);
    }

    /// <summary>Comma-separated tags that disqualify a window.</summary>
    public string ExcludeText
    {
        get => _excludeText;
        set => SetField(ref _excludeText, value);
    }

    /// <summary>Builds the model selector, or <c>null</c> when targeting the context window.</summary>
    public TargetSelector? ToSelector() => _useSelector
        ? new TargetSelector
        {
            RequireTags = SplitTags(_requireText),
            ExcludeTags = SplitTags(_excludeText),
        }
        : null;

    /// <summary>Loads a model selector (<c>null</c> = context window).</summary>
    public static TargetSelectorViewModel FromSelector(TargetSelector? selector) => new()
    {
        UseSelector = selector is not null,
        RequireText = JoinTags(selector?.RequireTags),
        ExcludeText = JoinTags(selector?.ExcludeTags),
    };

    // Tags containing a comma cannot be expressed here. They are class names and other
    // short labels in practice, so the trade-off buys a one-line editor for the common case.
    private static List<string> SplitTags(string text) =>
    [
        .. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ];

    private static string JoinTags(IReadOnlyList<string>? tags) =>
        tags is null || tags.Count == 0 ? string.Empty : string.Join(", ", tags);
}

/// <summary>
/// Editor for a client-space rectangle (X / Y / W / H as free text).
///
/// For the optional regions of Find/Wait nodes, a zero (or negative) width or height maps
/// back to <c>null</c> — the same "empty rect = search the whole client area" convention
/// the vision layer already uses, so the two representations mean the same thing and the
/// round-trip is lossless for every rect a user can usefully author.
/// </summary>
public sealed class RegionEditorViewModel : ObservableObject
{
    private string _xText = "0";
    private string _yText = "0";
    private string _widthText = "0";
    private string _heightText = "0";

    public string XText { get => _xText; set => SetField(ref _xText, value); }
    public string YText { get => _yText; set => SetField(ref _yText, value); }
    public string WidthText { get => _widthText; set => SetField(ref _widthText, value); }
    public string HeightText { get => _heightText; set => SetField(ref _heightText, value); }

    public static RegionEditorViewModel FromRect(ScreenRect? rect)
    {
        var value = rect ?? default;
        return new RegionEditorViewModel
        {
            XText = NodeInput.FormatInt(value.X),
            YText = NodeInput.FormatInt(value.Y),
            WidthText = NodeInput.FormatInt(value.Width),
            HeightText = NodeInput.FormatInt(value.Height),
        };
    }

    /// <summary>Always-a-value form, for nodes whose region is mandatory (RecognizeTag).</summary>
    public ScreenRect ToRect() => new(
        NodeInput.ParseInt(_xText) ?? 0,
        NodeInput.ParseInt(_yText) ?? 0,
        NodeInput.ParseInt(_widthText) ?? 0,
        NodeInput.ParseInt(_heightText) ?? 0);

    /// <summary>Optional form: a degenerate rect means "whole window" and is stored as <c>null</c>.</summary>
    public ScreenRect? ToOptionalRect()
    {
        var rect = ToRect();
        return rect.Width <= 0 || rect.Height <= 0 ? null : rect;
    }

    public IEnumerable<string> GetInputErrors(string nodeId)
    {
        foreach (var (label, text) in new[] { ("X", _xText), ("Y", _yText), ("W", _widthText), ("H", _heightText) })
        {
            if (NodeInput.ParseInt(text) is null)
            {
                yield return $"[{nodeId}] регион {label}: «{text}» — не целое число.";
            }
        }
    }
}

/// <summary>Shared text↔number parsing for the node editors. Invariant, comma tolerated as a decimal point.</summary>
internal static class NodeInput
{
    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Blank counts as 0; anything unparseable is <c>null</c> so the caller can report it.</summary>
    public static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Milliseconds rendered as seconds, matching the pre-graph editor: <c>"0.##"</c>
    /// invariant (1500 → "1.5", 2000 → "2"). Seconds are the unit PW itself uses for cast
    /// and cooldown times, so an author reading a skill tooltip can type what they see.
    /// </summary>
    public static string FormatSeconds(int ms) => (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Seconds text back to milliseconds. Accepts a comma as the decimal separator (a
    /// Russian keyboard's numpad produces one) by normalising it first — parsing "1,5"
    /// with invariant <see cref="NumberStyles.Any"/> would otherwise silently yield 15.
    /// </summary>
    public static int? ParseSecondsToMs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
        {
            return null;
        }
        return (int)Math.Round(seconds * 1000.0);
    }
}

/// <summary>
/// Base for the rows of the node editor — one row per node of the open graph.
///
/// The hierarchy is polymorphic on purpose: each concrete row owns exactly the parameters
/// its node type has, renders through an implicit <c>DataTemplate</c> matched on its own
/// type, and maps to/from the model in one place (<see cref="ToNode"/> /
/// <see cref="FromNode"/>). That pair is the contract the whole editor rests on and is
/// what the round-trip test pins down.
///
/// Edges are edited as drop-downs of node ids rather than by drawing links — this is the
/// rows editor; the canvas is W0.4. <see cref="Editor"/> (canvas coordinates) is carried
/// through untouched so opening a hand-arranged graph here does not flatten its layout.
/// </summary>
public abstract class NodeRowViewModel : ObservableObject
{
    private string _nodeId;
    private bool _isSelected;

    protected NodeRowViewModel(string nodeId, TargetSelectorViewModel? target, params NodeEdgeViewModel[] edges)
    {
        _nodeId = nodeId;
        Target = target;
        Edges = edges;
    }

    /// <summary>
    /// Raised after <see cref="NodeId"/> changes, carrying the PREVIOUS id. The editor
    /// listens so it can re-point every edge (and the start node) at the new id.
    /// </summary>
    public event Action<NodeRowViewModel, string>? IdChanged;

    /// <summary>Unique id within the graph. Edges reference nodes by this.</summary>
    public string NodeId
    {
        get => _nodeId;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, _nodeId, StringComparison.Ordinal))
            {
                // Reject a blank id outright: it would orphan every edge pointing here.
                OnPropertyChanged();
                return;
            }
            var previous = _nodeId;
            _nodeId = trimmed;
            OnPropertyChanged();
            IdChanged?.Invoke(this, previous);
        }
    }

    /// <summary>Canvas placement (W0.4). Round-tripped, never edited here.</summary>
    public NodeEditorInfo? Editor { get; set; }

    /// <summary>
    /// Highlighted in the list. Set by the editor when a validation issue naming this node
    /// is clicked — the cheap version of "scroll to the offending node".
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Russian type label shown in the row header.</summary>
    public abstract string TypeLabel { get; }

    /// <summary>Outgoing edges, in display order.</summary>
    public IReadOnlyList<NodeEdgeViewModel> Edges { get; }

    /// <summary>
    /// Target-selector editor, or <c>null</c> for nodes that have no <c>Target</c> in the
    /// model — the conditionals (context-window only by design) and <c>DelayNode</c>
    /// (a pause is global to the run).
    /// </summary>
    public TargetSelectorViewModel? Target { get; }

    /// <summary>Drives the visibility of the selector block.</summary>
    public bool HasTarget => Target is not null;

    /// <summary>Builds the model node from the current editor state.</summary>
    public abstract MacroNode ToNode();

    /// <summary>
    /// Field-level complaints ("X is not a number") in Russian, empty when the row is
    /// clean. Checked before the graph validator runs, because <see cref="ToNode"/> is
    /// lenient (unparseable numbers become 0) and would otherwise quietly persist a zero.
    /// </summary>
    public virtual IEnumerable<string> GetInputErrors() => [];

    /// <summary>Loads a model node into the matching row type.</summary>
    /// <exception cref="NotSupportedException">The node type has no editor (should be unreachable).</exception>
    public static NodeRowViewModel FromNode(MacroNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        NodeRowViewModel row = node switch
        {
            KeyPressNode n => new KeyPressNodeRowViewModel(n),
            ClickNode n => new ClickNodeRowViewModel(n),
            DelayNode n => new DelayNodeRowViewModel(n),
            AddTagNode n => new AddTagNodeRowViewModel(n),
            RemoveTagNode n => new RemoveTagNodeRowViewModel(n),
            SetIconNode n => new SetIconNodeRowViewModel(n),
            RunMacroNode n => new RunMacroNodeRowViewModel(n),
            FindElementNode n => new FindElementNodeRowViewModel(n),
            WaitForElementNode n => new WaitForElementNodeRowViewModel(n),
            RecognizeTagNode n => new RecognizeTagNodeRowViewModel(n),
            _ => throw new NotSupportedException($"No editor for node type {node.GetType().Name}."),
        };
        row.Editor = node.Editor;
        return row;
    }

    /// <summary>Creates an empty row of the requested kind with sensible defaults.</summary>
    public static NodeRowViewModel Create(MacroNodeKind kind, string nodeId) => kind switch
    {
        MacroNodeKind.KeyPress => new KeyPressNodeRowViewModel(new KeyPressNode { Id = nodeId, Key = VirtualKey.F1 }),
        MacroNodeKind.Click => new ClickNodeRowViewModel(new ClickNode { Id = nodeId, Point = default(ScreenPoint) }),
        MacroNodeKind.Delay => new DelayNodeRowViewModel(new DelayNode { Id = nodeId, Ms = 1000 }),
        MacroNodeKind.AddTag => new AddTagNodeRowViewModel(new AddTagNode { Id = nodeId, Tag = string.Empty }),
        MacroNodeKind.RemoveTag => new RemoveTagNodeRowViewModel(new RemoveTagNode { Id = nodeId, Tag = string.Empty }),
        MacroNodeKind.SetIcon => new SetIconNodeRowViewModel(new SetIconNode { Id = nodeId, IconPath = "Assets/ClassIcons/{tag}.png" }),
        MacroNodeKind.RunMacro => new RunMacroNodeRowViewModel(new RunMacroNode { Id = nodeId, MacroName = string.Empty }),
        MacroNodeKind.FindElement => new FindElementNodeRowViewModel(new FindElementNode { Id = nodeId, Template = string.Empty }),
        MacroNodeKind.WaitForElement => new WaitForElementNodeRowViewModel(new WaitForElementNode { Id = nodeId, Template = string.Empty, TimeoutMs = 10_000 }),
        MacroNodeKind.RecognizeTag => new RecognizeTagNodeRowViewModel(new RecognizeTagNode { Id = nodeId, TemplateSet = string.Empty, Region = default }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown node kind."),
    };

    /// <summary>Menu entries for the "add node" flyout, in catalogue order.</summary>
    public static IReadOnlyList<MacroNodeKindOption> Kinds { get; } =
    [
        new(MacroNodeKind.KeyPress, "Нажать клавишу"),
        new(MacroNodeKind.Click, "Клик"),
        new(MacroNodeKind.Delay, "Пауза"),
        new(MacroNodeKind.AddTag, "Добавить тег"),
        new(MacroNodeKind.RemoveTag, "Снять тег"),
        new(MacroNodeKind.SetIcon, "Сменить иконку"),
        new(MacroNodeKind.RunMacro, "Запустить макрос"),
        new(MacroNodeKind.FindElement, "Найти элемент"),
        new(MacroNodeKind.WaitForElement, "Ждать элемент"),
        new(MacroNodeKind.RecognizeTag, "Распознать тег"),
    ];
}
