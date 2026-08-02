using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels.Canvas;
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

    /// <summary>
    /// Same name in the canvas's lower-case voice ("далее", "нашёл"). The box rows are set
    /// in 9.5px and a capital there reads as a heading rather than as a port label.
    /// </summary>
    public string ShortLabel => Label.Length == 0
        ? Label
        : string.Concat(char.ToLowerInvariant(Label[0]).ToString(), Label.AsSpan(1));

    /// <summary>Selected node id; <c>""</c> = end of run.</summary>
    public string TargetId
    {
        get => _targetId;
        // A ComboBox pushes null when its SelectedItem leaves the ItemsSource (e.g. the
        // list is rebuilt after a node is deleted). Normalising to "" turns that into the
        // meaningful "no target" value instead of a null that would blow up later.
        set
        {
            if (SetField(ref _targetId, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsEnd));
                OnPropertyChanged(nameof(BoxLabel));
            }
        }
    }

    /// <summary>
    /// <c>true</c> when this outcome ends the run. NOT an error and NOT a node: the canvas
    /// says so in the port row itself rather than drawing an edge to a terminal box.
    /// </summary>
    public bool IsEnd => _targetId.Length == 0;

    /// <summary>Port-row caption on the collapsed box: "нашёл" or "таймаут → конец".</summary>
    public string BoxLabel => IsEnd ? $"{ShortLabel} → конец" : ShortLabel;

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

    /// <summary>
    /// The canvas's targets chip. It says what the SELECTOR is, never how many windows it
    /// currently matches — the panel has no way to ask (the mockup's «8 окон · кроме Склад»
    /// badge needs an IPC request that does not exist, plan §D4), and an invented number
    /// beside a real one is worse than no number.
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

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName is not (null or nameof(Summary)))
        {
            base.OnPropertyChanged(nameof(Summary));
        }
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
/// Base for the boxes of the node editor — one per node of the open graph.
///
/// The hierarchy is polymorphic on purpose: each concrete row owns exactly the parameters
/// its node type has, renders through an implicit <c>DataTemplate</c> matched on its own
/// type, and maps to/from the model in one place (<see cref="ToNode"/> /
/// <see cref="FromNode"/>). That pair is the contract the whole editor rests on and is
/// what the round-trip test pins down.
///
/// Since D3a the same object is also the canvas box: it carries its own position
/// (<see cref="X"/>/<see cref="Y"/>, persisted as the model's <c>NodeEditorInfo</c>), the
/// collapsed/expanded state of the in-place editor, and the one-line
/// <see cref="Summary"/> the box shows. Keeping that on the row rather than in a parallel
/// "canvas node" hierarchy means there is exactly one object per node and no syncing.
/// </summary>
public abstract class NodeRowViewModel : ObservableObject
{
    private string _nodeId;
    private bool _isSelected;
    private double _x;
    private double _y;
    private bool _hasPosition;
    private bool _isExpanded;
    private bool _isExecuting;

    protected NodeRowViewModel(string nodeId, TargetSelectorViewModel? target, params NodeEdgeViewModel[] edges)
    {
        _nodeId = nodeId;
        Target = target;
        Edges = edges;
        if (target is not null)
        {
            // The box's targets chip mirrors the selector, which is edited through its own
            // view-model — so its changes have to be forwarded or the chip goes stale.
            target.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(TargetSummary));
                OnPropertyChanged(nameof(ShowsTargetChip));
            };
        }
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

    /// <summary>
    /// Canvas placement, in the model's own shape. <c>null</c> means "never placed" — a
    /// graph authored before the canvas existed — and the editor lays those out on load
    /// rather than piling them at the origin.
    /// </summary>
    public NodeEditorInfo? Editor
    {
        get => _hasPosition ? new NodeEditorInfo(_x, _y) : null;
        set
        {
            if (value is null)
            {
                _hasPosition = false;
                _x = 0;
                _y = 0;
            }
            else
            {
                _hasPosition = true;
                _x = value.X;
                _y = value.Y;
            }
            OnPropertyChanged(nameof(X));
            OnPropertyChanged(nameof(Y));
            OnPropertyChanged(nameof(HasPosition));
        }
    }

    /// <summary>Canvas X of the box's top-left corner.</summary>
    public double X
    {
        get => _x;
        set
        {
            _hasPosition = true;
            SetField(ref _x, value);
        }
    }

    /// <summary>Canvas Y of the box's top-left corner.</summary>
    public double Y
    {
        get => _y;
        set
        {
            _hasPosition = true;
            SetField(ref _y, value);
        }
    }

    /// <summary><c>false</c> until the node has been placed (by hand, by load, or by auto-layout).</summary>
    public bool HasPosition => _hasPosition;

    /// <summary>Moves the box. One call so a drag raises two changes, not four.</summary>
    public void SetPosition(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// Height the ROUTER uses. Always the collapsed height, even while the box is expanded:
    /// an expanded node is a transient editing state that deliberately overlaps its
    /// neighbours, and re-routing every edge around it would make the graph jump.
    /// </summary>
    public double LayoutHeight => IsConditional
        ? CanvasMetrics.ConditionalNodeHeight
        : CanvasMetrics.ActionNodeHeight;

    /// <summary>Rendered height: the collapsed height, or auto (<c>NaN</c>) while expanded.</summary>
    public double BoxHeight => _isExpanded ? double.NaN : LayoutHeight;

    /// <summary>Rendered width — wider while expanded, to fit the parameter fields.</summary>
    public double BoxWidth => _isExpanded ? CanvasMetrics.ExpandedNodeWidth : CanvasMetrics.NodeWidth;

    /// <summary>Two outcomes rather than one — drives the box's height and its header glyph.</summary>
    public bool IsConditional => Edges.Count > 1;

    /// <summary>
    /// The box is an editor of itself (mockup 1e). Double click opens it, Esc closes it;
    /// the inspector on the right stays in sync because both edit the same object.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(BoxHeight));
                OnPropertyChanged(nameof(BoxWidth));
            }
        }
    }

    /// <summary>
    /// The executor is standing on this node. Wave D3b sets it from the run-event stream;
    /// nothing sets it today, so every box renders in its idle state.
    /// </summary>
    public bool IsExecuting
    {
        get => _isExecuting;
        set => SetField(ref _isExecuting, value);
    }

    /// <summary>
    /// Highlighted on the canvas and shown in the inspector. Set by clicking a box or a
    /// validation issue.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Russian type label shown in the box header.</summary>
    public abstract string TypeLabel { get; }

    /// <summary>
    /// The one mono line under the id: whatever identifies this node's job at a glance
    /// (the key, the point, the template, the delay). Recomputed on any property change —
    /// see <see cref="OnPropertyChanged"/>.
    /// </summary>
    public abstract string Summary { get; }

    /// <summary>
    /// <c>false</c> when the box should not print the summary line: either there is nothing
    /// to say, or the node renders a <see cref="Keycap"/> instead and printing both would
    /// show the same key twice.
    /// </summary>
    public bool HasSummary => Summary.Length > 0 && Keycap is null;

    /// <summary>The targets chip, or <c>null</c> for the node types that have no selector.</summary>
    public string? TargetSummary => Target?.Summary;

    /// <summary>
    /// Whether the box prints the targets chip at all. Only when the node routes by TAGS:
    /// a 210px header cannot carry both a type label and a chip, and "acts on the context
    /// window" is the default every second node has — it is the departure from it that is
    /// worth a word.
    /// </summary>
    public bool ShowsTargetChip => Target?.UseSelector == true;

    /// <summary>
    /// Non-null only for <see cref="KeyPressNodeRowViewModel"/>: the box draws a keycap
    /// instead of a line of text, because a key is a thing you press and reads as one.
    /// </summary>
    public virtual string? Keycap => null;

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

    /// <summary>
    /// Joins the parts of a box summary with the middle dot, skipping blanks. A node whose
    /// template has not been typed in yet must read <c>всё окно</c>, not <c>· всё окно</c>.
    /// </summary>
    protected static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Builds the model node from the current editor state.</summary>
    public abstract MacroNode ToNode();

    /// <summary>
    /// Every change re-raises <see cref="Summary"/>.
    ///
    /// The alternative is a hand-written raise in each of the ~25 parameter setters across
    /// ten row types, and the failure mode of forgetting one is a box that quietly shows
    /// stale text — the kind of bug that survives a full test suite. The pure-presentation
    /// properties are excluded so dragging a box does not churn its text.
    /// </summary>
    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        switch (propertyName)
        {
            case null:
            case nameof(Summary):
            case nameof(HasSummary):
            case nameof(TargetSummary):
            case nameof(ShowsTargetChip):
            case nameof(X):
            case nameof(Y):
            case nameof(HasPosition):
            case nameof(IsSelected):
            case nameof(IsExpanded):
            case nameof(IsExecuting):
            case nameof(BoxWidth):
            case nameof(BoxHeight):
                return;
            default:
                base.OnPropertyChanged(nameof(Summary));
                base.OnPropertyChanged(nameof(HasSummary));
                return;
        }
    }

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
