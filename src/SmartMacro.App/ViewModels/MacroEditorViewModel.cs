using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels;

/// <summary>One macro in the editor's left-hand library list.</summary>
public sealed class MacroListItemViewModel : ObservableObject
{
    private bool _isRunning;
    private bool _isCurrent;
    private string? _hotkeyProblem;

    public MacroListItemViewModel(MacroGraph macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        Name = macro.Name;
        Summary = Describe(macro);
        TriggerBadge = Badge(macro);
    }

    /// <summary>Macro name = file stem = identity.</summary>
    public string Name { get; }

    /// <summary>Triggers and node count — the tooltip text.</summary>
    public string Summary { get; }

    /// <summary>
    /// The one-token chip beside the name: a chord (<c>F23</c>), <c>процесс</c>, or
    /// <c>null</c> when the macro has no trigger at all. Only the FIRST trigger is shown —
    /// the row is 28px and a macro with three triggers is rare enough to leave to the
    /// inspector.
    /// </summary>
    public string? TriggerBadge { get; }

    /// <summary><c>true</c> when there is a badge to render.</summary>
    public bool HasTriggerBadge => TriggerBadge is not null;

    /// <summary>Drives the Run/Stop button states.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsNotRunning));
            }
        }
    }

    public bool IsNotRunning => !_isRunning;

    /// <summary>
    /// This is the macro open in the editor. The library is a tree of groups rather than
    /// one flat <c>ListBox</c>, so selection cannot ride on <c>ListBoxItem</c>'s
    /// <c>:selected</c> and is carried here instead.
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetField(ref _isCurrent, value);
    }

    /// <summary>
    /// «Ctrl+F1 не зарегистрирован — занят другим приложением», or <c>null</c>.
    ///
    /// The library row is the only place a silently dead hotkey can be noticed WITHOUT
    /// opening the macro, which matters because the usual way this happens is at daemon
    /// startup, to a macro nobody is editing.
    /// </summary>
    public string? HotkeyProblem
    {
        get => _hotkeyProblem;
        internal set
        {
            if (SetField(ref _hotkeyProblem, value))
            {
                OnPropertyChanged(nameof(HasHotkeyProblem));
            }
        }
    }

    /// <summary><c>true</c> when the row should carry its warning marker.</summary>
    public bool HasHotkeyProblem => _hotkeyProblem is not null;

    private static string Describe(MacroGraph macro)
    {
        var triggers = macro.Triggers.Select(DescribeTrigger).ToList();
        var triggerText = triggers.Count > 0
            ? string.Join(", ", triggers)
            : "без триггеров";
        return string.Create(CultureInfo.CurrentCulture, $"{triggerText} · нод: {macro.Nodes.Count}");
    }

    private static string? Badge(MacroGraph macro) => macro.Triggers.Count switch
    {
        0 => null,
        _ => macro.Triggers[0] switch
        {
            HotkeyTrigger { IsMouse: true } hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.MouseButton.ToString()),
            HotkeyTrigger hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.Key.ToString()),
            ProcessAppearedTrigger => "процесс",
            var other => other.GetType().Name,
        },
    };

    private static string DescribeTrigger(MacroTrigger trigger) => trigger switch
    {
        HotkeyTrigger { IsMouse: true } hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.MouseButton.ToString()),
        HotkeyTrigger hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.Key.ToString()),
        ProcessAppearedTrigger process => $"процесс {process.ProcessName}",
        _ => trigger.GetType().Name,
    };

    private static string Chord(string modifiers, string key) =>
        string.Equals(modifiers, "None", StringComparison.Ordinal) ? key : $"{modifiers}+{key}";
}

/// <summary>One line of the validation panel.</summary>
public sealed class ValidationIssueViewModel
{
    public ValidationIssueViewModel(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        IsError = issue.Severity == ValidationSeverity.Error;
        NodeId = issue.NodeId;
        Display = issue.NodeId is null
            ? issue.Message
            : $"[{issue.NodeId}] {issue.Message}";
    }

    /// <summary>Free-form message (used for the input-level errors the validator never sees).</summary>
    public ValidationIssueViewModel(string message, bool isError)
    {
        IsError = isError;
        Display = message;
    }

    /// <summary>Errors block the save; warnings do not.</summary>
    public bool IsError { get; }

    /// <summary>Node the issue belongs to, when the validator named one.</summary>
    public string? NodeId { get; }

    /// <summary>Rendered text.</summary>
    public string Display { get; }
}

/// <summary>
/// The macro editor: library list on the left, the selected graph's triggers and nodes on
/// the right.
///
/// This is the rows editor of plan §0.5 — every node's PARAMETERS are editable, and its
/// outgoing edges are edited as drop-downs of node ids rather than by drawing links. That
/// covers branchy graphs as well as chains (unlike the pre-graph editor, which could only
/// express linear lists); the canvas in W0.4 adds direct manipulation on top of the same
/// model, it does not unlock anything that is unreachable here.
///
/// <b>Stage 3: the library is remote.</b> Where this VM used to hold a <c>MacroGraphStore</c>
/// it now holds a snapshot fetched over IPC, refreshed on <c>MacrosChanged</c> and on every
/// reconnect. Three consequences worth knowing before editing this class:
///
///   * <b>The daemon is the validator of record.</b> <c>SaveMacro</c> answers with the issue
///     list; an empty one means the graph was written. Warnings on a SUCCESSFUL save are not
///     returned (the protocol gives that field one meaning — rejection reasons), so they are
///     re-derived locally with the same <see cref="MacroGraphValidator"/>.
///   * <b>The save's own echo can arrive before its reply.</b> The daemon broadcasts
///     <c>MacrosChanged</c> from inside its save, on a different write path than the
///     response — so the "is this an external edit?" baselines are set BEFORE the request
///     goes out, and rolled back if it is refused.
///   * <b>A successful write is merged into the local library immediately</b> rather than
///     waiting for the push, so the list and the selection settle synchronously.
///
/// Everything Avalonia-shaped is kept out on purpose, so the whole class is exercisable
/// headlessly against a fake <see cref="IIpcClient"/> — which matters because the graph↔VM
/// mapping is where a silent data-loss bug would live.
/// </summary>
public sealed class MacroEditorViewModel : ObservableObject, IDisposable
{
    private const string DraftName = "новый-макрос";

    /// <summary>Folder the daemon keeps its macro files in, relative to its own directory.</summary>
    private const string MacroFolderName = "macros";

    private readonly IIpcClient _client;
    private readonly IMacroLauncher? _launcher;
    private readonly IHotkeySuspension? _hotkeys;
    private readonly IUiDispatcher _dispatcher;

    private IReadOnlyList<MacroGraph> _library = [];
    private IReadOnlyList<RunningMacroDto> _runningMacros = [];
    private IReadOnlyList<HotkeyFailureDto> _hotkeyFailures = [];

    private MacroListItemViewModel? _selectedMacro;
    private NodeRowViewModel? _selectedNode;
    private bool _suppressSelectionReload;

    // Name of the macro currently open, as it exists in the library. null = unsaved draft.
    private string? _loadedName;
    // Serialised form of the editor state as of the last load/save — the dirty baseline.
    private string _loadedJson = string.Empty;
    // Serialised form of what we believe the daemon has — the external-change baseline. Kept
    // separately from _loadedJson because loading normalises (a degenerate region becomes
    // null, say), and normalisation must not read as "the file changed under us".
    private string _diskJson = string.Empty;

    private string _macroName = string.Empty;
    private string _startNodeId = string.Empty;
    private bool _hasOpenMacro;
    private bool _changedOnDisk;
    private string? _errorMessage;
    private string? _statusMessage;

    private readonly MacroRunTracker _runs = new();

    private string _librarySearch = string.Empty;
    private double _zoom = 1;
    private double _panX = MinPan;
    private double _panY = MinPan;
    private string? _executingNodeId;
    private MacroRunViewModel? _selectedRun;
    private bool _wantsRunEvents;
    private int _droppedRunEvents;
    // Edge geometry is rebuilt from the nodes; while a batch of structural edits is in
    // flight (a load, a delete that repoints edges) the rebuild is deferred to the end so
    // the canvas is not routed against a half-updated graph.
    private int _edgeRebuildSuspended;

    /// <param name="client">Connection to the daemon — the library, the runs and the writes.</param>
    /// <param name="launcher">Manual "Run" seam; <c>null</c> disables the button.</param>
    /// <param name="hotkeys">Suspend/resume around the chord picker; <c>null</c> is a no-op.</param>
    /// <param name="dispatcher">UI-thread marshalling for daemon pushes.</param>
    /// <param name="macroFolderPath">
    /// Absolute path behind the "open folder" button. Supplied by the host because only IT
    /// knows where the daemon lives; defaults to <c>macros/</c> next to this executable,
    /// which is correct for the deployed side-by-side layout.
    /// </param>
    public MacroEditorViewModel(
        IIpcClient client,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null,
        IUiDispatcher? dispatcher = null,
        string? macroFolderPath = null)
    {
        _client = client;
        _launcher = launcher;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;
        FolderPath = macroFolderPath ?? Path.Combine(AppContext.BaseDirectory, MacroFolderName);

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;
        // One subscription for the whole trigger list rather than a hook in each of
        // AddTrigger / RemoveTrigger / LoadGraph / CloseEditor: those are four places that
        // would all have to remember, and forgetting one leaves a picker whose conflict
        // state never updates.
        Triggers.CollectionChanged += OnTriggersCollectionChanged;

        if (_client.IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    // ---- library (left pane) --------------------------------------------------------

    /// <summary>Macros in the library, ordered as the daemon returns them (by name).</summary>
    public ObservableCollection<MacroListItemViewModel> Macros { get; } = [];

    /// <summary>
    /// Selected library entry. Assigning it loads that graph into the editor; unsaved
    /// edits to the previously open graph are discarded (with a message — there is no
    /// modal confirmation in this pass).
    /// </summary>
    public MacroListItemViewModel? SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (!SetField(ref _selectedMacro, value) || _suppressSelectionReload)
            {
                return;
            }
            if (value is null)
            {
                return;
            }
            if (TryGet(value.Name) is { } graph)
            {
                var discarded = _hasOpenMacro && IsDirty() ? _loadedName ?? _macroName : null;
                LoadGraph(graph);
                ErrorMessage = discarded is null
                    ? null
                    : $"Несохранённые изменения в «{discarded}» отброшены.";
            }
        }
    }

    /// <summary>
    /// The library as the panel draws it: sections headed <c>pw · 6</c> / <c>прочее · 11</c>.
    /// Derived from <see cref="Macros"/> and <see cref="LibrarySearch"/>; the rule lives in
    /// <see cref="MacroLibraryGrouping"/>.
    /// </summary>
    public ObservableCollection<MacroLibraryGroupViewModel> MacroGroups { get; } = [];

    /// <summary>Library filter box. Case-insensitive substring over the macro name.</summary>
    public string LibrarySearch
    {
        get => _librarySearch;
        set
        {
            if (SetField(ref _librarySearch, value ?? string.Empty))
            {
                RebuildGroups();
            }
        }
    }

    /// <summary>Absolute path of the daemon's macro folder — the "open folder" affordance.</summary>
    public string FolderPath { get; }

    /// <summary>
    /// The editor's live window snapshot, behind every node's targets badge (D4). Seeded by
    /// <see cref="RefreshAsync"/> and kept current by the daemon's window pushes, exactly
    /// like the library is kept current by <c>MacrosChanged</c>.
    ///
    /// Public because the tests drive it and because the inspector's badge reads its count;
    /// nothing outside this class mutates it.
    /// </summary>
    public WindowCatalog Windows { get; } = new();

    // ---- open graph (right pane) ----------------------------------------------------

    /// <summary><c>true</c> when a graph (saved or draft) is open in the right pane.</summary>
    public bool HasOpenMacro
    {
        get => _hasOpenMacro;
        private set => SetField(ref _hasOpenMacro, value);
    }

    /// <summary>
    /// Editable name of the open graph. Saving under a different name renames the macro:
    /// the daemon keys on the file stem, so a rename is "write the new file, delete the
    /// old one" — which this VM does, because the protocol has no rename operation.
    /// </summary>
    public string MacroName
    {
        get => _macroName;
        set => SetField(ref _macroName, value ?? string.Empty);
    }

    /// <summary>Trigger rows of the open graph.</summary>
    public ObservableCollection<TriggerRowViewModel> Triggers { get; } = [];

    /// <summary>Node rows of the open graph, in persisted list order.</summary>
    public ObservableCollection<NodeRowViewModel> Nodes { get; } = [];

    /// <summary>
    /// Selectable edge targets: the empty string (= end of run) followed by every node id.
    /// One shared instance bound by every edge drop-down, so a rename or a new node shows
    /// up everywhere at once.
    /// </summary>
    public ObservableCollection<string> NodeIdChoices { get; } = [];

    /// <summary>Node ids for the start-node picker. Same list without the empty entry — a start node is required.</summary>
    public ObservableCollection<string> StartNodeChoices { get; } = [];

    /// <summary>Library names offered by <c>RunMacroNode</c> drop-downs.</summary>
    public ObservableCollection<string> MacroChoices { get; } = [];

    /// <summary>Where execution begins. Must name one of <see cref="Nodes"/>.</summary>
    public string StartNodeId
    {
        get => _startNodeId;
        set
        {
            // A ComboBox pushes null while its ItemsSource churns; ignoring that keeps a
            // valid start node from being wiped by an unrelated list rebuild.
            if (!string.IsNullOrEmpty(value))
            {
                SetField(ref _startNodeId, value);
            }
        }
    }

    /// <summary>Node kinds for the "add node" flyout.</summary>
    public IReadOnlyList<MacroNodeKindOption> NodeKinds => NodeRowViewModel.Kinds;

    /// <summary>Row highlighted by clicking a validation issue.</summary>
    public NodeRowViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            var previous = _selectedNode;
            if (!SetField(ref _selectedNode, value))
            {
                return;
            }
            if (previous is not null)
            {
                previous.IsSelected = false;
            }
            if (value is not null)
            {
                value.IsSelected = true;
            }
            OnPropertyChanged(nameof(HasSelectedNode));
            OnPropertyChanged(nameof(InspectorTitle));
        }
    }

    /// <summary>Drives the inspector's two states: a node, or the macro itself.</summary>
    public bool HasSelectedNode => _selectedNode is not null;

    /// <summary>Inspector heading — the selected node's type, or «Макрос».</summary>
    public string InspectorTitle => _selectedNode?.TypeLabel ?? "Макрос";

    // ---- canvas ---------------------------------------------------------------------

    /// <summary>
    /// Every drawn edge of the open graph, rebuilt whenever the graph's shape or a node's
    /// position changes. An outcome with no target contributes nothing — see
    /// <see cref="CanvasEdgeRouter"/>.
    /// </summary>
    public ObservableCollection<CanvasEdgeViewModel> CanvasEdges { get; } = [];

    /// <summary>Smallest zoom the canvas allows.</summary>
    public const double MinZoom = 0.35;

    /// <summary>Largest zoom the canvas allows.</summary>
    public const double MaxZoom = 2.0;

    // The surface is pinned this far inside the viewport at rest, so the top-left box is
    // not flush against the panel edge. Small on purpose: three columns of the wrapping
    // layout are 780px and the canvas pane is ~810 at the default window size, so a
    // generous margin is the difference between "the graph fits at 100%" and "the third
    // column is clipped until you pan".
    private const double MinPan = 12;

    /// <summary>Canvas scale. Clamped — a graph zoomed to nothing is a lost graph.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetField(ref _zoom, Math.Clamp(value, MinZoom, MaxZoom)))
            {
                OnPropertyChanged(nameof(ZoomText));
            }
        }
    }

    /// <summary>Zoom as the chip renders it: "100%".</summary>
    public string ZoomText => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(_zoom * 100)}%");

    /// <summary>Horizontal pan of the surface, in screen pixels.</summary>
    public double PanX
    {
        get => _panX;
        set => SetField(ref _panX, value);
    }

    /// <summary>Vertical pan of the surface, in screen pixels.</summary>
    public double PanY
    {
        get => _panY;
        set => SetField(ref _panY, value);
    }

    /// <summary>Back to 100% at the origin.</summary>
    public void ResetView()
    {
        Zoom = 1;
        PanX = MinPan;
        PanY = MinPan;
    }

    /// <summary>
    /// Node the executor is standing on, or <c>null</c>. Follows <see cref="SelectedRun"/>,
    /// which is the whole reason there is a run picker: a graph can be walked by ten windows
    /// at once and only one of them may light a box.
    ///
    /// Settable from outside because the canvas tests drive it directly; in the live panel
    /// only <see cref="SyncExecutingNode"/> writes it.
    /// </summary>
    public string? ExecutingNodeId
    {
        get => _executingNodeId;
        set
        {
            if (!SetField(ref _executingNodeId, value))
            {
                return;
            }
            foreach (var node in Nodes)
            {
                node.IsExecuting = value is not null
                    && string.Equals(node.NodeId, value, StringComparison.Ordinal);
            }
            // Edges read their liveness off their source node, and the layer repaints on a
            // collection change rather than on a property change of one edge — so the
            // highlight would otherwise lag a frame behind the box.
            RebuildEdges();
        }
    }

    // ---- run log --------------------------------------------------------------------

    /// <summary>
    /// The run-log strip under the canvas: the rows of <see cref="SelectedRun"/>.
    ///
    /// A projection, not a store — every walk keeps its own rows in
    /// <see cref="MacroRunViewModel.Log"/>, so switching the picker (or opening another
    /// macro and coming back) shows that walk's history rather than a log that was thrown
    /// away.
    /// </summary>
    public ObservableCollection<RunLogRowViewModel> RunLog { get; } = [];

    /// <summary><c>true</c> once there is anything to show in the strip.</summary>
    public bool HasRunLog => RunLog.Count > 0;

    /// <summary>
    /// What the strip says when it has no rows. Distinguishes "nothing has run" from
    /// "nothing is being recorded", because those call for different reactions from the user.
    /// </summary>
    public string RunLogEmptyText => _wantsRunEvents
        ? "прогонов ещё не было"
        : "лог пишется, пока открыт режим «Макросы»";

    /// <summary>
    /// Walks of the OPEN macro, oldest first — what the run chip pages through. Empty
    /// whenever the open graph has never been run while the panel was watching.
    /// </summary>
    public ObservableCollection<MacroRunViewModel> Runs { get; } = [];

    /// <summary><c>true</c> when there is a run chip to draw at all.</summary>
    public bool HasRuns => Runs.Count > 0;

    /// <summary>
    /// The walk the canvas and the log strip follow. Assigning it re-points both.
    /// </summary>
    public MacroRunViewModel? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (ReferenceEquals(_selectedRun, value))
            {
                return;
            }
            _selectedRun = value;
            OnPropertyChanged(nameof(SelectedRun));
            OnPropertyChanged(nameof(RunChipText));
            OnPropertyChanged(nameof(RunPositionText));
            OnPropertyChanged(nameof(SelectedRunIsLive));
            RebuildRunLog();
            SyncExecutingNode();
        }
    }

    /// <summary>Chip label: the context window as <c>0x140804</c>, or the macro name when there is none.</summary>
    public string RunChipText => _selectedRun?.Label ?? string.Empty;

    /// <summary>"2 / 10" while a fan-out is in flight; empty when there is only one walk.</summary>
    public string RunPositionText
    {
        get
        {
            if (_selectedRun is null || Runs.Count < 2)
            {
                return string.Empty;
            }
            return string.Create(CultureInfo.InvariantCulture, $"{Runs.IndexOf(_selectedRun) + 1} / {Runs.Count}");
        }
    }

    /// <summary>Drives the live dot beside the chip.</summary>
    public bool SelectedRunIsLive => _selectedRun?.IsLive == true;

    /// <summary>
    /// Why the strip may not be telling the whole truth: the panel joined mid-run, or the
    /// daemon had to discard events. <c>null</c> when the log is complete.
    ///
    /// This exists because the alternative — rendering a partial log exactly like a full one
    /// — turns "the click never happened" and "you weren't watching when it did" into the
    /// same picture.
    /// </summary>
    public string? RunLogNotice
    {
        get
        {
            var parts = new List<string>(2);
            if (_selectedRun is { FromStart: false })
            {
                parts.Add("начало прогона не записано");
            }
            if (_droppedRunEvents > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"пропущено событий: {_droppedRunEvents}"));
            }
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary><c>true</c> when <see cref="RunLogNotice"/> has something to say.</summary>
    public bool HasRunLogNotice => RunLogNotice is not null;

    /// <summary>Selects the previous walk of this macro (the chip's ◂).</summary>
    public void SelectPreviousRun() => StepRun(-1);

    /// <summary>Selects the next walk of this macro (the chip's ▸).</summary>
    public void SelectNextRun() => StepRun(+1);

    /// <summary>
    /// Drops every recorded walk (the strip's «очистить»). Live walks reappear as soon as
    /// they report their next node, because the daemon keeps sending — this clears the
    /// PANEL's history, it does not stop anything.
    /// </summary>
    public void ClearRunLog()
    {
        _runs.Clear();
        _droppedRunEvents = 0;
        SelectedRun = null;
        RebuildRuns();
        OnPropertyChanged(nameof(RunLogNotice));
        OnPropertyChanged(nameof(HasRunLogNotice));
    }

    /// <summary>Re-places every node on the grid (the «Авто-раскладка» button).</summary>
    public void AutoLayout()
    {
        if (!HasOpenMacro)
        {
            return;
        }
        MacroGraphLayout.Apply(Nodes, _startNodeId);
        RebuildEdges();
    }

    /// <summary>Moves one box. Called continuously while a node is dragged.</summary>
    public void MoveNode(NodeRowViewModel row, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.SetPosition(x, y);
        RebuildEdges();
    }

    /// <summary>
    /// Re-points an outcome, which is what dropping a dragged link does.
    /// <paramref name="targetId"/> of <c>null</c> or <c>""</c> means "end of run" — the
    /// legitimate unwired state, not a deletion of the outcome.
    /// </summary>
    public void RewireEdge(NodeEdgeViewModel edge, string? targetId)
    {
        ArgumentNullException.ThrowIfNull(edge);
        // A node cannot be reached from its own outcome without an infinite loop that the
        // canvas would draw as a knot; the validator has no rule against it, so the editor
        // simply refuses to create one by drag.
        var owner = Nodes.FirstOrDefault(node => node.Edges.Contains(edge));
        if (owner is not null && string.Equals(owner.NodeId, targetId, StringComparison.Ordinal))
        {
            return;
        }
        edge.TargetId = targetId ?? string.Empty;
    }

    /// <summary>Opens one box as an editor of itself (1e) and closes any other.</summary>
    public void ExpandNode(NodeRowViewModel? row)
    {
        foreach (var node in Nodes)
        {
            node.IsExpanded = ReferenceEquals(node, row);
        }
        if (row is not null)
        {
            SelectedNode = row;
        }
    }

    /// <summary>Collapses whatever box is expanded (Esc).</summary>
    public void CollapseNodes() => ExpandNode(null);

    /// <summary>Findings of the last save attempt (errors and warnings).</summary>
    public ObservableCollection<ValidationIssueViewModel> Issues { get; } = [];

    /// <summary><c>true</c> when the panel has anything to show.</summary>
    public bool HasIssues => Issues.Count > 0;

    /// <summary>
    /// The open graph was modified on disk while it had unsaved edits here. The editor
    /// refuses to clobber either side and shows a hint instead; a clean editor just
    /// reloads silently.
    /// </summary>
    public bool ChangedOnDisk
    {
        get => _changedOnDisk;
        private set => SetField(ref _changedOnDisk, value);
    }

    /// <summary>Blocking problem (failed save, bad name). Red.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    /// <summary>Non-blocking feedback ("сохранено"). Muted.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    // ---- library commands -----------------------------------------------------------

    /// <summary>Re-seeds the library and the run state from the daemon.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var macros = await _client.RequestAsync<MacroGraph[]>(IpcMessageTypes.GetMacros).ConfigureAwait(false);
            var runs = await _client.RequestAsync<RunningMacroDto[]>(IpcMessageTypes.GetRunningMacros).ConfigureAwait(false);
            // The targets badge needs the window list, and this VM keeps its own rather than
            // reaching into «Окна» — see WindowCatalog.
            var windows = await _client.RequestAsync<WindowDto[]>(IpcMessageTypes.GetWindows).ConfigureAwait(false);
            var failures = await _client.RequestAsync<HotkeyFailureDto[]>(IpcMessageTypes.GetHotkeyFailures).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                _runningMacros = runs ?? [];
                _hotkeyFailures = failures ?? [];
                Windows.Reset(windows ?? []);
                ApplyLibrary(macros ?? []);
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось получить библиотеку макросов");
        }
    }

    /// <summary>
    /// Opens a fresh draft: one Delay node, so the graph is immediately valid and
    /// saveable rather than starting out failing the "start node must exist" rule.
    /// Nothing is written until <see cref="SaveAsync"/>.
    /// </summary>
    public void NewMacro()
    {
        LoadGraph(new MacroGraph
        {
            Name = UniqueDraftName(),
            StartNodeId = "n1",
            Nodes = [new DelayNode { Id = "n1", Ms = 1000 }],
        });

        // A draft has no file yet: clear the disk identity so hot-reload leaves it alone
        // and Save creates rather than renames.
        _loadedName = null;
        _diskJson = string.Empty;
        _suppressSelectionReload = true;
        SelectedMacro = null;
        _suppressSelectionReload = false;
        StatusMessage = "Черновик — не сохранён.";
    }

    /// <summary>Deletes a macro from the library (and closes it if it was open).</summary>
    public async Task<bool> DeleteMacroAsync(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;

        try
        {
            // Deleting a macro that isn't there is a no-op by protocol, so the only failure
            // that reaches here is a transport or IO problem.
            await _client
                .RequestAsync(IpcMessageTypes.DeleteMacro, new DeleteMacroRequest(item.Name))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            ErrorMessage = $"Не удалось удалить «{item.Name}».";
            Log.Warning(ex, "DeleteMacro '{Macro}' не выполнен", item.Name);
            return false;
        }

        if (string.Equals(_loadedName, item.Name, StringComparison.Ordinal))
        {
            CloseEditor();
        }
        // Applied locally rather than waiting for the MacrosChanged push, so the list is
        // settled by the time this returns.
        SetLibrary([.. _library.Where(macro => !string.Equals(macro.Name, item.Name, StringComparison.Ordinal))]);
        StatusMessage = $"Макрос «{item.Name}» удалён.";
        return true;
    }

    /// <summary>Starts a macro with no context window — the manual equivalent of its hotkey.</summary>
    public void Run(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;
        if (_launcher is null)
        {
            ErrorMessage = "Запуск недоступен.";
            return;
        }
        _launcher.RunMacro(item.Name);
    }

    /// <summary>Cancels every tracked run of this macro (normally at most one).</summary>
    public async Task StopAsync(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;

        var runIds = _runningMacros
            .Where(run => string.Equals(run.MacroName, item.Name, StringComparison.Ordinal))
            .Select(run => run.RunId)
            .ToList();

        foreach (var runId in runIds)
        {
            try
            {
                await _client
                    .RequestAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(runId))
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
            {
                Log.Warning(ex, "Не удалось остановить запуск {RunId} макроса '{Macro}'", runId, item.Name);
            }
        }
    }

    // ---- editing commands -----------------------------------------------------------

    /// <summary>Appends a trigger row of the given kind.</summary>
    public TriggerRowViewModel AddTrigger(MacroTriggerKind kind)
    {
        var row = TriggerRowViewModel.Create(kind);
        Triggers.Add(row);
        return row;
    }

    /// <summary>Removes a trigger row.</summary>
    public void RemoveTrigger(TriggerRowViewModel row) => Triggers.Remove(row);

    /// <summary>Appends a node of the given kind with a freshly generated id.</summary>
    public NodeRowViewModel AddNode(MacroNodeKind kind)
    {
        var row = NodeRowViewModel.Create(kind, NextNodeId());
        // Placed before it joins the list, so the free-slot scan does not see itself.
        var (x, y) = MacroGraphLayout.NextFreeSlot(Nodes);
        row.SetPosition(x, y);
        AttachNode(row);
        Nodes.Add(row);

        // First node of an empty graph becomes the start node — otherwise the very first
        // thing the user sees after adding one is a validation error about a missing start.
        if (Nodes.Count == 1)
        {
            _startNodeId = row.NodeId;
            OnPropertyChanged(nameof(StartNodeId));
        }

        RebuildChoices();
        RebuildEdges();
        SelectedNode = row;
        return row;
    }

    /// <summary>
    /// Removes a node and repairs the graph around it: every edge that pointed at it
    /// becomes "end of run", and a start node that pointed at it moves to whatever node
    /// is left (nothing, if the graph is now empty).
    /// </summary>
    public void DeleteNode(NodeRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!Nodes.Remove(row))
        {
            return;
        }
        DetachNode(row);

        using (SuspendEdgeRebuild())
        {
            var removedId = row.NodeId;
            foreach (var edge in AllEdges())
            {
                if (string.Equals(edge.TargetId, removedId, StringComparison.Ordinal))
                {
                    edge.TargetId = string.Empty;
                }
            }
            if (string.Equals(_startNodeId, removedId, StringComparison.Ordinal))
            {
                _startNodeId = Nodes.Count > 0 ? Nodes[0].NodeId : string.Empty;
            }

            if (ReferenceEquals(SelectedNode, row))
            {
                SelectedNode = null;
            }
            RebuildChoices();
        }
        OnPropertyChanged(nameof(StartNodeId));
    }

    /// <summary>Highlights the node an issue refers to.</summary>
    public void SelectIssue(ValidationIssueViewModel issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.NodeId is null)
        {
            return;
        }
        SelectedNode = Nodes.FirstOrDefault(node => string.Equals(node.NodeId, issue.NodeId, StringComparison.Ordinal));
    }

    // ---- save -----------------------------------------------------------------------

    /// <summary>Builds the model graph from the current editor state. Lenient — never throws on bad input.</summary>
    public MacroGraph BuildGraph() => new()
    {
        Name = _macroName.Trim(),
        Triggers = [.. Triggers.Select(row => row.ToTrigger())],
        StartNodeId = _startNodeId,
        Nodes = [.. Nodes.Select(row => row.ToNode())],
    };

    /// <summary><c>true</c> when the editor state differs from what was last loaded or saved.</summary>
    public bool IsDirty() =>
        HasOpenMacro && !string.Equals(_loadedJson, SerializeCurrent(), StringComparison.Ordinal);

    /// <summary>
    /// Validates and persists the open graph.
    ///
    /// Two gates, in order: every row's own fields must parse (checked here — the daemon
    /// never sees a half-typed number, only the graph it produces), and then the daemon's
    /// <c>SaveMacro</c> must come back with an empty issue list. A non-empty one means
    /// nothing was written and carries the reasons, including the file-name check.
    /// </summary>
    /// <returns><c>false</c> when nothing was written; <see cref="Issues"/> explains why.</returns>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        ClearIssues();
        ErrorMessage = null;
        StatusMessage = null;

        if (!HasOpenMacro)
        {
            return false;
        }

        var inputErrors = Triggers.SelectMany(row => row.GetInputErrors())
            .Concat(Nodes.SelectMany(row => row.GetInputErrors()))
            .ToList();
        foreach (var error in inputErrors)
        {
            AddIssue(new ValidationIssueViewModel(error, isError: true));
        }
        if (inputErrors.Count > 0)
        {
            ErrorMessage = "Сохранение отменено: исправьте ошибки.";
            return false;
        }

        var graph = BuildGraph();
        var name = graph.Name;

        // Baselines move BEFORE the request: the daemon broadcasts MacrosChanged from inside
        // its save, on the event pump rather than the response path, so the echo can reach
        // us first — and the hot-reload handler has to recognise it as ours.
        var previousName = _loadedName;
        var previousLoadedJson = _loadedJson;
        var previousDiskJson = _diskJson;
        var json = MacroGraphJson.Serialize(graph);
        _loadedName = name;
        _loadedJson = json;
        _diskJson = json;

        ValidationIssueDto[]? rejected;
        try
        {
            rejected = await _client
                .RequestAsync<ValidationIssueDto[]>(
                    IpcMessageTypes.SaveMacro,
                    new SaveMacroRequest(graph),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            _loadedName = previousName;
            _loadedJson = previousLoadedJson;
            _diskJson = previousDiskJson;
            ErrorMessage = $"Не удалось сохранить: {ex.Message}";
            return false;
        }

        if (rejected is { Length: > 0 })
        {
            // Refused — nothing was written, so the editor stays exactly as dirty as it was.
            _loadedName = previousName;
            _loadedJson = previousLoadedJson;
            _diskJson = previousDiskJson;
            foreach (var issue in rejected)
            {
                AddIssue(new ValidationIssueViewModel(issue.ToIssue()));
            }
            ErrorMessage = "Сохранение отменено: исправьте ошибки.";
            return false;
        }

        if (previousName is not null && !string.Equals(previousName, name, StringComparison.Ordinal))
        {
            // Rename: the name IS the file stem, so the old file has to go. Order matters —
            // write first, delete second, so a failure in between leaves two copies rather
            // than none.
            try
            {
                await _client
                    .RequestAsync(IpcMessageTypes.DeleteMacro, new DeleteMacroRequest(previousName), cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
            {
                Log.Warning(ex, "Переименование: старый файл '{Macro}' не удалён", previousName);
            }
        }

        // A successful save returns an EMPTY list by protocol, warnings included — so they
        // are re-derived here with the same validator the daemon ran. Errors cannot appear:
        // the daemon would have refused the write.
        foreach (var issue in MacroGraphValidator.Validate(graph))
        {
            if (issue.Severity != ValidationSeverity.Error)
            {
                AddIssue(new ValidationIssueViewModel(issue));
            }
        }

        SetLibrary(MergeSaved(graph, previousName));
        ChangedOnDisk = false;
        SelectByName(name);
        StatusMessage = Issues.Count > 0
            ? $"Сохранено с предупреждениями ({Issues.Count})."
            : "Сохранено.";
        return true;
    }

    /// <summary>Discards local edits and re-reads the open macro from the library snapshot.</summary>
    public void ReloadFromDisk()
    {
        if (_loadedName is null || TryGet(_loadedName) is not { } graph)
        {
            return;
        }
        LoadGraph(graph);
        StatusMessage = "Перезагружено с диска.";
    }

    /// <summary>Loads a graph into the right pane.</summary>
    public void LoadGraph(MacroGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var row in Nodes)
        {
            DetachNode(row);
        }
        Nodes.Clear();
        Triggers.Clear();
        ClearIssues();

        _loadedName = graph.Name;
        MacroName = graph.Name;
        foreach (var trigger in graph.Triggers)
        {
            Triggers.Add(TriggerRowViewModel.FromTrigger(trigger));
        }
        using (SuspendEdgeRebuild())
        {
            foreach (var node in graph.Nodes)
            {
                var row = NodeRowViewModel.FromNode(node);
                AttachNode(row);
                Nodes.Add(row);
            }

            _startNodeId = graph.StartNodeId;
            HasOpenMacro = true;
            RebuildChoices();

            // Graphs written before the canvas existed carry no coordinates. Laying them
            // out HERE rather than on first paint means the baseline below is taken with
            // the positions already in it, so opening an old macro does not read as an
            // unsaved edit — but saving it for any other reason does persist the layout.
            MacroGraphLayout.EnsurePositions(Nodes, _startNodeId);
        }
        OnPropertyChanged(nameof(StartNodeId));
        ResetView();

        // Baseline for "dirty" is the editor's own round-trip, not the file: loading
        // normalises a few shapes, and that normalisation is not a user edit.
        _loadedJson = SerializeCurrent();
        _diskJson = MacroGraphJson.Serialize(graph);
        ChangedOnDisk = false;
        SelectedNode = null;
        ErrorMessage = null;
        StatusMessage = null;
        SyncCurrentFlags();

        // Last, because it can light a box: the picker is re-derived for THIS graph, and if
        // it is being walked right now the canvas picks the run up mid-flight.
        RebuildRuns();
    }

    // ---- hotkey suspension ----------------------------------------------------------

    /// <summary>
    /// Switches the daemon's global hotkeys off. Must be called before the hotkey picker can
    /// work at all — see <see cref="IHotkeySuspension"/>. WHEN it is called is the shell's
    /// decision (D2 scopes it to the «Макросы» mode being on screen); this VM only forwards.
    /// </summary>
    public Task SuspendHotkeysAsync() => _hotkeys?.SuspendAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Restores global hotkeys from the (possibly just-edited) library, then asks which of
    /// them Windows refused. The order matters: <c>ResumeHotkeys</c> only answers once every
    /// <c>RegisterHotKey</c> has been attempted, so the failure list is settled by then.
    /// </summary>
    public async Task ResumeHotkeysAsync()
    {
        if (_hotkeys is null)
        {
            return;
        }
        await _hotkeys.ResumeAsync().ConfigureAwait(false);
        await RefreshHotkeyFailuresAsync().ConfigureAwait(false);
    }

    // ---- run-event subscription -----------------------------------------------------

    /// <summary>
    /// Asks the daemon to start (or stop) streaming run events to this connection.
    ///
    /// Scoped by the shell to the «Макросы» mode being on screen, exactly like hotkey
    /// suspension: the stream is the only high-rate thing in the protocol, and the daemon
    /// produces nothing at all while nobody is subscribed. A panel sitting in «Окна» must
    /// not make the engine format a detail string for every node of every macro.
    ///
    /// The reply is the set of walks ALREADY in flight. They are adopted with
    /// <c>FromStart = false</c>, which is what puts «начало прогона не записано» on the
    /// strip instead of quietly showing a beheaded log.
    /// </summary>
    public async Task SetRunEventSubscriptionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _wantsRunEvents = enabled;
        // Marshalled: the reconnect path calls this from the client's thread-pool thread,
        // and a property raise from there reaches a binding off the UI thread.
        _dispatcher.Post(() => OnPropertyChanged(nameof(RunLogEmptyText)));

        try
        {
            var live = await _client
                .RequestAsync<RunWalkDto[]>(
                    IpcMessageTypes.SubscribeRunEvents,
                    new SubscribeRunEventsRequest(enabled),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() => AdoptLiveWalks(live ?? []));
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // A canvas without a live highlight is still a usable editor, so this is a
            // warning rather than a visible failure.
            Log.Warning(ex, "Не удалось {Action} поток событий прогона", enabled ? "включить" : "выключить");
        }
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.EventReceived -= OnEventReceived;
        Triggers.CollectionChanged -= OnTriggersCollectionChanged;
        foreach (var row in _watchedTriggers)
        {
            row.PropertyChanged -= OnTriggerRowChanged;
        }
        _watchedTriggers.Clear();
        foreach (var row in Nodes)
        {
            DetachNode(row);
        }
    }

    // ---- hotkey conflicts (mockup 1f, fourth state) -----------------------------------

    private readonly HashSet<HotkeyTriggerRowViewModel> _watchedTriggers = [];

    // Reconciled rather than driven off the event's Old/NewItems: Clear() raises a Reset
    // with neither, and LoadGraph clears before it refills.
    private void OnTriggersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var current = Triggers.OfType<HotkeyTriggerRowViewModel>().ToHashSet();
        foreach (var row in _watchedTriggers.Except(current).ToList())
        {
            row.PropertyChanged -= OnTriggerRowChanged;
            _watchedTriggers.Remove(row);
        }
        foreach (var row in current.Except(_watchedTriggers).ToList())
        {
            row.PropertyChanged += OnTriggerRowChanged;
            _watchedTriggers.Add(row);
        }
        RefreshHotkeyConflicts();
    }

    private void OnTriggerRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Conflict/HasConflict are what this method WRITES; reacting to them would be a
        // (terminating, but pointless) loop through the whole library on every assignment.
        if (e.PropertyName is nameof(HotkeyTriggerRowViewModel.Conflict)
            or nameof(HotkeyTriggerRowViewModel.HasConflict))
        {
            return;
        }
        RefreshHotkeyConflicts();
    }

    /// <summary>
    /// Recomputes both halves of "this hotkey will not work": the library clash the panel
    /// can see on its own, and the registration Windows refused.
    ///
    /// Order is deliberate. A chord claimed by another macro is reported as that, even when
    /// the daemon ALSO failed to register it — the two are the same event (the daemon
    /// registers the first claimant and Windows rejects the second), and «уже занят
    /// pw-immunity» names the thing the user can actually fix.
    /// </summary>
    private void RefreshHotkeyConflicts()
    {
        // Who else in the library owns a chord. The open macro is skipped: its triggers are
        // the ROWS, which may already differ from what is on disk.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var macro in _library)
        {
            if (_loadedName is not null && string.Equals(macro.Name, _loadedName, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var trigger in macro.Triggers.OfType<HotkeyTrigger>())
            {
                if (HotkeyTriggerRowViewModel.ChordKey(trigger) is { } key)
                {
                    owners.TryAdd(key, macro.Name);
                }
            }
        }

        // Registration failures that belong to the macro being edited. Matching on the macro
        // NAME as well as the chord matters: when two macros share a chord the daemon
        // registers one and rejects the other, and the winner must not be told its own key
        // is taken.
        var refusedHere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var failure in _hotkeyFailures)
        {
            if (_loadedName is not null && string.Equals(failure.MacroName, _loadedName, StringComparison.Ordinal))
            {
                refusedHere.Add($"K:{(int)failure.Modifiers}:{(int)failure.Key}");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Triggers.OfType<HotkeyTriggerRowViewModel>())
        {
            var key = row.ChordKey();
            if (key is null)
            {
                // Nothing bound yet — an empty picker clashes with nothing.
                row.Conflict = null;
            }
            else if (owners.TryGetValue(key, out var other))
            {
                row.Conflict = $"уже занят {other}";
            }
            else if (!seen.Add(key))
            {
                row.Conflict = "уже задан в этом макросе";
            }
            else
            {
                row.Conflict = refusedHere.Contains(key) ? "занят другим приложением" : null;
            }
        }

        RefreshLibraryHotkeyProblems();
    }

    // The library rows carry the same news for macros nobody has opened — the case where a
    // hotkey died at daemon startup and there is otherwise nothing on screen to say so.
    private void RefreshLibraryHotkeyProblems()
    {
        var byMacro = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var failure in _hotkeyFailures)
        {
            var chord = failure.Modifiers == HotkeyModifiers.None
                ? failure.Key.ToString()
                : $"{failure.Modifiers}+{failure.Key}";
            byMacro[failure.MacroName] = $"{chord} не зарегистрирован — сочетание занято другим приложением";
        }

        foreach (var item in Macros)
        {
            item.HotkeyProblem = byMacro.TryGetValue(item.Name, out var text) ? text : null;
        }
    }

    // ---- internals ------------------------------------------------------------------

    private MacroGraph? TryGet(string name) =>
        _library.FirstOrDefault(macro => string.Equals(macro.Name, name, StringComparison.Ordinal));

    private void OnConnected()
    {
        _ = RefreshAsync();

        // Subscriptions do NOT survive a reconnect — the daemon forgets a connection's flag
        // with the connection — and whatever ran during the gap is unrecoverable. So the
        // history goes, and the subscription is re-sent.
        _dispatcher.Post(ClearRunLog);
        if (_wantsRunEvents)
        {
            _ = SetRunEventSubscriptionAsync(true);
        }
    }

    private void OnEventReceived(IpcEvent evt)
    {
        switch (evt.Type)
        {
            case IpcMessageTypes.RunEvents:
            {
                // Read off the reader thread, applied on the UI thread: the payload is a
                // batch precisely so this happens a handful of times per run rather than
                // hundreds.
                var batch = IpcJson.Read<RunEventBatch>(evt.Payload);
                if (batch is not null)
                {
                    _dispatcher.Post(() => ApplyRunEvents(batch));
                }
                break;
            }

            case IpcMessageTypes.MacrosChanged:
                // Payloadless by protocol — the library can be large, so the daemon says
                // "something changed" and we go and get it.
                _ = ReloadLibraryAsync();
                break;

            case IpcMessageTypes.RunningMacrosChanged:
                var runs = IpcJson.Read<RunningMacroDto[]>(evt.Payload) ?? [];
                _dispatcher.Post(() =>
                {
                    _runningMacros = runs;
                    RefreshRunState();
                });
                break;

            // Both carry the window's FULL new state, so one upsert serves both.
            case IpcMessageTypes.WindowAppeared:
            case IpcMessageTypes.WindowTagsChanged:
                if (IpcJson.Read<WindowDto>(evt.Payload) is { } window)
                {
                    _dispatcher.Post(() => Windows.Upsert(window));
                }
                break;

            case IpcMessageTypes.WindowClosed:
                if (IpcJson.Read<WindowClosedEvent>(evt.Payload) is { } closed)
                {
                    _dispatcher.Post(() => Windows.Remove(closed.Hwnd));
                }
                break;

            default:
                break;
        }
    }

    // ---- run events -----------------------------------------------------------------

    /// <summary>
    /// Applies one batch. Everything here runs on the UI thread and touches only walks —
    /// the graph itself is never modified by a run.
    /// </summary>
    private void ApplyRunEvents(RunEventBatch batch)
    {
        if (batch.Dropped > 0)
        {
            _droppedRunEvents += batch.Dropped;
            OnPropertyChanged(nameof(RunLogNotice));
            OnPropertyChanged(nameof(HasRunLogNotice));
        }

        var listChanged = false;
        foreach (var evt in batch.Events)
        {
            var run = _runs.Apply(evt);
            if (run is null || !IsOpenMacro(run.MacroName))
            {
                // A walk of some OTHER graph — tracked (so opening that graph shows its log)
                // but nothing on screen changes.
                continue;
            }
            if (evt.Kind == RunEventKind.WalkStarted)
            {
                listChanged = true;
            }
            if (ReferenceEquals(run, _selectedRun))
            {
                SyncSelectedRunState(evt);
            }
        }

        if (listChanged)
        {
            RebuildRuns();
        }
    }

    // The selected walk moved: mirror its log into the strip and its position onto the canvas.
    private void SyncSelectedRunState(RunEventDto evt)
    {
        switch (evt.Kind)
        {
            case RunEventKind.NodeEntered:
                RebuildRunLog();
                SyncExecutingNode();
                break;
            case RunEventKind.NodeExited:
                // The row object is mutated in place, so the strip already shows it; only
                // the current-node highlight can be affected, and only by the walk ending.
                break;
            case RunEventKind.WalkFinished:
                SyncExecutingNode();
                OnPropertyChanged(nameof(SelectedRunIsLive));
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Takes over the walks that were already running when we subscribed. They are the
    /// daemon's answer to <c>SubscribeRunEvents</c> and carry no log at all, which is
    /// exactly what <c>FromStart = false</c> is there to admit.
    /// </summary>
    private void AdoptLiveWalks(IReadOnlyList<RunWalkDto> live)
    {
        if (live.Count == 0)
        {
            return;
        }
        foreach (var walk in live)
        {
            _runs.Add(walk);
        }
        RebuildRuns();
    }

    /// <summary>
    /// Re-derives the picker for the open macro and re-applies the selection rule. Called
    /// whenever the set of walks changes or a different graph is opened.
    /// </summary>
    private void RebuildRuns()
    {
        var forMacro = _runs.For(_loadedName);

        Runs.Clear();
        foreach (var run in forMacro)
        {
            Runs.Add(run);
        }
        OnPropertyChanged(nameof(HasRuns));

        // Keep the selection when it is still valid; otherwise follow whatever is alive —
        // and never steal the selection off a walk that is still running.
        if (_selectedRun is not null && Runs.Contains(_selectedRun) && _selectedRun.IsLive)
        {
            OnPropertyChanged(nameof(RunPositionText));
            return;
        }

        var newest = Runs.LastOrDefault(run => run.IsLive) ?? Runs.LastOrDefault();
        if (!ReferenceEquals(newest, _selectedRun))
        {
            SelectedRun = newest;
            return;
        }
        OnPropertyChanged(nameof(RunPositionText));
    }

    private void RebuildRunLog()
    {
        RunLog.Clear();
        if (_selectedRun is not null)
        {
            foreach (var row in _selectedRun.Log)
            {
                RunLog.Add(row);
            }
        }
        OnPropertyChanged(nameof(HasRunLog));
        OnPropertyChanged(nameof(RunLogNotice));
        OnPropertyChanged(nameof(HasRunLogNotice));
    }

    private void SyncExecutingNode() => ExecutingNodeId = _selectedRun?.CurrentNodeId;

    private void StepRun(int delta)
    {
        if (Runs.Count == 0 || _selectedRun is null)
        {
            return;
        }
        var index = Runs.IndexOf(_selectedRun);
        if (index < 0)
        {
            return;
        }
        // Wraps: with ten walks in a fan-out, paging off one end and having to turn around
        // is the wrong feel for a two-arrow chip.
        SelectedRun = Runs[((index + delta) % Runs.Count + Runs.Count) % Runs.Count];
    }

    private bool IsOpenMacro(string macroName) =>
        _loadedName is not null && string.Equals(_loadedName, macroName, StringComparison.Ordinal);

    private async Task ReloadLibraryAsync()
    {
        try
        {
            var macros = await _client.RequestAsync<MacroGraph[]>(IpcMessageTypes.GetMacros).ConfigureAwait(false);
            _dispatcher.Post(() => ApplyLibrary(macros ?? []));
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось перечитать библиотеку макросов");
        }
        await RefreshHotkeyFailuresAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads which chords the daemon failed to register.
    ///
    /// A pull, and these are its three moments: a reconnect, a library change, and the
    /// return of <see cref="ResumeHotkeysAsync"/>. The last one is the load-bearing one —
    /// while the «Макросы» mode is open the daemon holds every chord unregistered, so a
    /// hotkey bound in the editor is only ever tried when the user leaves the mode, and the
    /// verdict lands the moment <c>ResumeHotkeys</c> answers.
    /// </summary>
    private async Task RefreshHotkeyFailuresAsync()
    {
        try
        {
            var failures = await _client
                .RequestAsync<HotkeyFailureDto[]>(IpcMessageTypes.GetHotkeyFailures)
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                _hotkeyFailures = failures ?? [];
                RefreshHotkeyConflicts();
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось получить список незарегистрированных хоткеев");
        }
    }

    private void ApplyLibrary(IReadOnlyList<MacroGraph> macros)
    {
        SetLibrary(macros);

        if (!HasOpenMacro || _loadedName is null)
        {
            return; // nothing open, or an unsaved draft with no file to track
        }

        var onDisk = macros.FirstOrDefault(m => string.Equals(m.Name, _loadedName, StringComparison.Ordinal));
        if (onDisk is null)
        {
            ChangedOnDisk = true;
            StatusMessage = $"Файл «{_loadedName}» исчез с диска — сохранение создаст его заново.";
            return;
        }

        var json = MacroGraphJson.Serialize(onDisk);
        if (string.Equals(json, _diskJson, StringComparison.Ordinal))
        {
            return; // our own write echoing back, or an unrelated file changed
        }

        if (IsDirty())
        {
            // Never clobber unsaved work — flag it and let the user pick a side.
            _diskJson = json;
            ChangedOnDisk = true;
            return;
        }

        LoadGraph(onDisk);
        StatusMessage = "Макрос обновлён на диске — перечитан.";
    }

    private void SetLibrary(IReadOnlyList<MacroGraph> macros)
    {
        _library = macros;
        RebuildLibrary(macros);
        RefreshRunState();
        // A macro that just took (or gave up) a chord changes what every open picker is
        // clashing with, and RebuildLibrary made new row objects that need their markers.
        RefreshHotkeyConflicts();
    }

    // The written graph replaces (or joins) the snapshot immediately, and a rename drops the
    // old entry, so the list and the selection are correct before MacrosChanged arrives.
    private IReadOnlyList<MacroGraph> MergeSaved(MacroGraph graph, string? renamedFrom)
    {
        var next = _library
            .Where(macro => !string.Equals(macro.Name, graph.Name, StringComparison.Ordinal)
                            && (renamedFrom is null || !string.Equals(macro.Name, renamedFrom, StringComparison.Ordinal)))
            .Append(graph)
            .OrderBy(macro => macro.Name, StringComparer.Ordinal)
            .ToList();
        return next;
    }

    private void RebuildLibrary(IReadOnlyList<MacroGraph> macros)
    {
        // Prefer the OPEN macro over the currently highlighted row: after saving a new
        // macro the list is rebuilt from a posted event, and keying on the old selection
        // would drop the highlight off the very macro the user is editing.
        var previous = _loadedName ?? _selectedMacro?.Name;
        _suppressSelectionReload = true;
        try
        {
            Macros.Clear();
            foreach (var macro in macros)
            {
                Macros.Add(new MacroListItemViewModel(macro));
            }
            SelectedMacro = previous is null
                ? null
                : Macros.FirstOrDefault(item => string.Equals(item.Name, previous, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelectionReload = false;
        }

        SyncCurrentFlags();
        RebuildGroups();
        RebuildMacroChoices(macros);
    }

    private void RebuildMacroChoices(IReadOnlyList<MacroGraph> macros)
    {
        var names = macros.Select(m => m.Name).ToList();
        // Keep a reference to a macro that no longer exists selectable, so opening a graph
        // whose sub-macro was deleted doesn't silently blank the reference.
        foreach (var row in Nodes.OfType<RunMacroNodeRowViewModel>())
        {
            if (row.MacroName.Length > 0 && !names.Contains(row.MacroName, StringComparer.Ordinal))
            {
                names.Add(row.MacroName);
            }
        }
        Replace(MacroChoices, names);
    }

    private void RefreshRunState()
    {
        var running = _runningMacros
            .Select(run => run.MacroName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in Macros)
        {
            item.IsRunning = running.Contains(item.Name);
        }
    }

    private void SelectByName(string name)
    {
        _suppressSelectionReload = true;
        try
        {
            SelectedMacro = Macros.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelectionReload = false;
        }
    }

    private void CloseEditor()
    {
        foreach (var row in Nodes)
        {
            DetachNode(row);
        }
        Nodes.Clear();
        Triggers.Clear();
        CanvasEdges.Clear();
        ClearIssues();
        _loadedName = null;
        _loadedJson = string.Empty;
        _diskJson = string.Empty;
        MacroName = string.Empty;
        _startNodeId = string.Empty;
        OnPropertyChanged(nameof(StartNodeId));
        HasOpenMacro = false;
        ChangedOnDisk = false;
        SelectedNode = null;
        RebuildChoices();
        SyncCurrentFlags();
        // No graph open ⇒ no walk to follow. The walks themselves stay tracked, so
        // re-opening the macro brings its log back.
        RebuildRuns();
    }

    private void AttachNode(NodeRowViewModel row)
    {
        row.IdChanged += OnNodeIdChanged;
        foreach (var edge in row.Edges)
        {
            edge.Choices = NodeIdChoices;
            // Re-pointing an outcome moves a line on the canvas, whether it was done in
            // the inspector's drop-down or by dragging the port.
            edge.PropertyChanged += OnEdgeChanged;
        }
        if (row is RunMacroNodeRowViewModel runMacro)
        {
            runMacro.MacroChoices = MacroChoices;
        }
        // Same shared-instance pattern as the choice lists: one catalogue, every badge on
        // the canvas recomputes when a window appears or is tagged.
        if (row.Target is { } target)
        {
            target.Windows = Windows;
        }
    }

    private void DetachNode(NodeRowViewModel row)
    {
        row.IdChanged -= OnNodeIdChanged;
        foreach (var edge in row.Edges)
        {
            edge.PropertyChanged -= OnEdgeChanged;
        }
        if (row.Target is { } target)
        {
            // Drops the selector's subscription to the catalogue — a closed graph's rows
            // must not keep recomputing badges nobody is looking at.
            target.Windows = null;
        }
    }

    private void OnEdgeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeEdgeViewModel.TargetId))
        {
            RebuildEdges();
        }
    }

    /// <summary>
    /// Recomputes every edge path. Cheap (a macro is tens of nodes, not thousands) and
    /// called on every frame of a node drag, which is what keeps the lines glued to the box.
    /// </summary>
    private void RebuildEdges()
    {
        if (_edgeRebuildSuspended > 0)
        {
            return;
        }
        CanvasEdges.Clear();
        foreach (var edge in CanvasEdgeRouter.BuildAll(Nodes))
        {
            CanvasEdges.Add(edge);
        }
    }

    // Batches a structural edit so the canvas is routed once, at the end, against a
    // consistent graph.
    private IDisposable SuspendEdgeRebuild()
    {
        _edgeRebuildSuspended++;
        return new EdgeRebuildScope(this);
    }

    private sealed class EdgeRebuildScope(MacroEditorViewModel owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }
            _done = true;
            owner._edgeRebuildSuspended--;
            owner.RebuildEdges();
        }
    }

    // The library is a tree of groups, so "which row is highlighted" is a flag on the row
    // rather than a ListBox selection.
    private void SyncCurrentFlags()
    {
        var current = _loadedName ?? _selectedMacro?.Name;
        foreach (var item in Macros)
        {
            item.IsCurrent = current is not null
                && string.Equals(item.Name, current, StringComparison.Ordinal);
        }
    }

    private void RebuildGroups()
    {
        var visible = _librarySearch.Trim().Length == 0
            ? Macros.AsEnumerable()
            : Macros.Where(item => item.Name.Contains(_librarySearch.Trim(), StringComparison.CurrentCultureIgnoreCase));

        MacroGroups.Clear();
        foreach (var group in MacroLibraryGrouping.Build(visible))
        {
            MacroGroups.Add(group);
        }
    }

    // Renaming a node has to carry its inbound edges with it, or the rename would silently
    // sever every link into the node.
    private void OnNodeIdChanged(NodeRowViewModel row, string previousId)
    {
        foreach (var edge in AllEdges())
        {
            if (string.Equals(edge.TargetId, previousId, StringComparison.Ordinal))
            {
                edge.TargetId = row.NodeId;
            }
        }
        if (string.Equals(_startNodeId, previousId, StringComparison.Ordinal))
        {
            _startNodeId = row.NodeId;
        }
        RebuildChoices();
        RebuildEdges();
        OnPropertyChanged(nameof(StartNodeId));
    }

    private IEnumerable<NodeEdgeViewModel> AllEdges() => Nodes.SelectMany(node => node.Edges);

    // Rebuilding the shared choice lists makes every bound ComboBox re-evaluate its
    // selection, and a SelectedItem that momentarily leaves the ItemsSource comes back as
    // null. Snapshotting the intended values around the rebuild — rather than diffing the
    // lists — keeps that transient from silently rewriting the graph's edges.
    private void RebuildChoices()
    {
        var edges = AllEdges().ToList();
        var targets = edges.Select(edge => edge.TargetId).ToArray();
        var start = _startNodeId;

        var ids = Nodes.Select(node => node.NodeId).ToList();

        var edgeChoices = new List<string>(ids.Count + 2) { string.Empty };
        edgeChoices.AddRange(ids);
        // A hand-edited file can point an edge at a node that isn't there. Keep the value
        // selectable so the editor shows the truth and the validator can complain about it,
        // instead of quietly rewriting it to "end of run".
        foreach (var target in targets)
        {
            if (target.Length > 0 && !edgeChoices.Contains(target, StringComparer.Ordinal))
            {
                edgeChoices.Add(target);
            }
        }

        var startChoices = new List<string>(ids);
        if (start.Length > 0 && !startChoices.Contains(start, StringComparer.Ordinal))
        {
            startChoices.Add(start);
        }

        Replace(NodeIdChoices, edgeChoices);
        Replace(StartNodeChoices, startChoices);

        for (var i = 0; i < edges.Count; i++)
        {
            edges[i].TargetId = targets[i];
        }
        _startNodeId = start;
        OnPropertyChanged(nameof(StartNodeId));
    }

    private string NextNodeId()
    {
        var used = Nodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"n{i}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private string UniqueDraftName()
    {
        var used = Macros.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(DraftName))
        {
            return DraftName;
        }
        for (var i = 2; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{DraftName}-{i}");
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private string SerializeCurrent()
    {
        try
        {
            return MacroGraphJson.Serialize(BuildGraph());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Only reachable if a row produces something unserialisable; treat it as
            // "different from anything" so the state reads as dirty rather than clean.
            return Guid.NewGuid().ToString();
        }
    }

    private void AddIssue(ValidationIssueViewModel issue)
    {
        Issues.Add(issue);
        OnPropertyChanged(nameof(HasIssues));
    }

    private void ClearIssues()
    {
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
    }

    /// <summary>
    /// Reconciles a choice list IN PLACE.
    ///
    /// Not <c>Clear()</c> + re-add, which is what this used to be. A clear raises a Reset,
    /// and every <c>ComboBox</c> bound to the list answers a Reset by dropping its
    /// <c>SelectedItem</c> — so re-opening a macro that was already open (the «Перечитать»
    /// path, where the ids are IDENTICAL before and after) left the start-node picker
    /// blank. Rebuilding in place means the common case raises nothing at all.
    /// </summary>
    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(values[i]);
            }
            else if (!string.Equals(target[i], values[i], StringComparison.Ordinal))
            {
                target[i] = values[i];
            }
        }
        while (target.Count > values.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
