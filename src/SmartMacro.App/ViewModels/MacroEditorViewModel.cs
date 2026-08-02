using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Macros.Validation;

namespace SmartMacro.App.ViewModels;

/// <summary>One macro in the editor's left-hand library list.</summary>
public sealed class MacroListItemViewModel : ObservableObject
{
    private bool _isRunning;

    public MacroListItemViewModel(MacroGraph macro)
    {
        Name = macro.Name;
        Summary = Describe(macro);
    }

    /// <summary>Macro name = file stem = identity.</summary>
    public string Name { get; }

    /// <summary>Triggers and node count — enough to tell graphs apart at a glance.</summary>
    public string Summary { get; }

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

    private static string Describe(MacroGraph macro)
    {
        var triggers = macro.Triggers.Select(trigger => trigger switch
        {
            HotkeyTrigger { IsMouse: true } hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.MouseButton.ToString()),
            HotkeyTrigger hotkey => Chord(hotkey.Modifiers.ToString(), hotkey.Key.ToString()),
            ProcessAppearedTrigger process => $"процесс {process.ProcessName}",
            _ => trigger.GetType().Name,
        }).ToList();

        var triggerText = triggers.Count > 0
            ? string.Join(", ", triggers)
            : "без триггеров";
        return string.Create(CultureInfo.CurrentCulture, $"{triggerText} · нод: {macro.Nodes.Count}");
    }

    private static string Chord(string modifiers, string key) =>
        string.Equals(modifiers, "None", StringComparison.Ordinal) ? key : $"{modifiers}+{key}";
}

/// <summary>One line of the validation panel.</summary>
public sealed class ValidationIssueViewModel
{
    public ValidationIssueViewModel(ValidationIssue issue)
    {
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
/// Everything Avalonia-shaped is kept out on purpose: Core services come in through the
/// constructor, thread marshalling goes through <see cref="IUiDispatcher"/>, and the two
/// side effects the dialog needs from the host process — starting a macro and suspending
/// global hotkeys — are narrow interfaces. The whole class is therefore exercisable
/// headlessly, which matters because the graph↔VM mapping is where a silent data-loss bug
/// would live.
/// </summary>
public sealed class MacroEditorViewModel : ObservableObject, IDisposable
{
    private const string DraftName = "новый-макрос";

    private readonly MacroGraphStore _store;
    private readonly MacroRunRegistry _runs;
    private readonly IMacroLauncher? _launcher;
    private readonly IHotkeySuspension? _hotkeys;
    private readonly IUiDispatcher _dispatcher;

    private MacroListItemViewModel? _selectedMacro;
    private NodeRowViewModel? _selectedNode;
    private bool _suppressSelectionReload;

    // Name of the macro currently open, as it exists on disk. null = unsaved draft.
    private string? _loadedName;
    // Serialised form of the editor state as of the last load/save — the dirty baseline.
    private string _loadedJson = string.Empty;
    // Serialised form of what we believe is on disk — the external-change baseline. Kept
    // separately from _loadedJson because loading normalises (a degenerate region becomes
    // null, say), and normalisation must not read as "the file changed under us".
    private string _diskJson = string.Empty;

    private string _macroName = string.Empty;
    private string _startNodeId = string.Empty;
    private bool _hasOpenMacro;
    private bool _changedOnDisk;
    private string? _errorMessage;
    private string? _statusMessage;

    public MacroEditorViewModel(
        MacroGraphStore store,
        MacroRunRegistry runs,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null,
        IUiDispatcher? dispatcher = null)
    {
        _store = store;
        _runs = runs;
        _launcher = launcher;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;

        RebuildLibrary(_store.All);
        RefreshRunState();

        _store.MacrosChanged += OnMacrosChanged;
        _runs.RunsChanged += OnRunsChanged;
    }

    // ---- library (left pane) --------------------------------------------------------

    /// <summary>Macros in the library, ordered as the store returns them (by name).</summary>
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
            if (_store.TryGet(value.Name) is { } graph)
            {
                var discarded = _hasOpenMacro && IsDirty() ? _loadedName ?? _macroName : null;
                LoadGraph(graph);
                ErrorMessage = discarded is null
                    ? null
                    : $"Несохранённые изменения в «{discarded}» отброшены.";
            }
        }
    }

    /// <summary>Absolute path of the macro folder — the "open folder" affordance.</summary>
    public string FolderPath => _store.FolderPath;

    // ---- open graph (right pane) ----------------------------------------------------

    /// <summary><c>true</c> when a graph (saved or draft) is open in the right pane.</summary>
    public bool HasOpenMacro
    {
        get => _hasOpenMacro;
        private set => SetField(ref _hasOpenMacro, value);
    }

    /// <summary>
    /// Editable name of the open graph. Saving under a different name renames the macro:
    /// the store keys on the file stem, so a rename is "write the new file, delete the
    /// old one" — which this VM does, because the store has no rename operation.
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
        }
    }

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

        var deleted = await _store.DeleteAsync(item.Name).ConfigureAwait(true);
        if (!deleted)
        {
            ErrorMessage = $"Не удалось удалить «{item.Name}».";
            return false;
        }

        if (string.Equals(_loadedName, item.Name, StringComparison.Ordinal))
        {
            CloseEditor();
        }
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
    public void Stop(MacroListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;
        foreach (var run in _runs.Snapshot())
        {
            if (string.Equals(run.MacroName, item.Name, StringComparison.Ordinal))
            {
                _ = _runs.StopAsync(run.RunId);
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
        OnPropertyChanged(nameof(StartNodeId));
    }

    /// <summary>Moves a node one slot up. Display order only — edges are unaffected.</summary>
    public void MoveNodeUp(NodeRowViewModel row)
    {
        var index = Nodes.IndexOf(row);
        if (index > 0)
        {
            Nodes.Move(index, index - 1);
            RebuildChoices();
        }
    }

    /// <summary>Moves a node one slot down. Display order only — edges are unaffected.</summary>
    public void MoveNodeDown(NodeRowViewModel row)
    {
        var index = Nodes.IndexOf(row);
        if (index >= 0 && index < Nodes.Count - 1)
        {
            Nodes.Move(index, index + 1);
            RebuildChoices();
        }
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
    /// Three gates, in order: the name must be a usable file name; every row's own fields
    /// must parse; and <see cref="MacroGraphValidator"/> must report no ERRORs. Warnings
    /// (unreachable node, hot loop) are listed but let the save through — they describe
    /// graphs that run, just suspiciously.
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

        var name = _macroName.Trim();
        if (MacroGraphStore.ValidateName(name) is { } nameError)
        {
            AddIssue(new ValidationIssueViewModel($"Имя макроса: {nameError}", isError: true));
            ErrorMessage = "Сохранение отменено: исправьте ошибки.";
            return false;
        }

        var inputErrors = Triggers.SelectMany(row => row.GetInputErrors())
            .Concat(Nodes.SelectMany(row => row.GetInputErrors()))
            .ToList();
        foreach (var error in inputErrors)
        {
            AddIssue(new ValidationIssueViewModel(error, isError: true));
        }

        var graph = BuildGraph();
        var blocking = inputErrors.Count > 0;
        foreach (var issue in MacroGraphValidator.Validate(graph))
        {
            AddIssue(new ValidationIssueViewModel(issue));
            blocking |= issue.Severity == ValidationSeverity.Error;
        }

        if (blocking)
        {
            ErrorMessage = "Сохранение отменено: исправьте ошибки.";
            return false;
        }

        var previousName = _loadedName;
        // Set the baselines BEFORE writing: the store raises MacrosChanged synchronously
        // from inside SaveAsync, and the hot-reload handler must recognise the write as
        // ours rather than as an external edit.
        _loadedName = name;
        _loadedJson = MacroGraphJson.Serialize(graph);
        _diskJson = _loadedJson;

        try
        {
            await _store.SaveAsync(graph, cancellationToken).ConfigureAwait(true);
            if (previousName is not null && !string.Equals(previousName, name, StringComparison.Ordinal))
            {
                // Rename: the name IS the file stem, so the old file has to go. Order
                // matters — write first, delete second, so a crash in between leaves two
                // copies rather than none.
                await _store.DeleteAsync(previousName, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _loadedName = previousName;
            ErrorMessage = $"Не удалось сохранить: {ex.Message}";
            return false;
        }

        ChangedOnDisk = false;
        SelectByName(name);
        StatusMessage = Issues.Count > 0
            ? $"Сохранено с предупреждениями ({Issues.Count})."
            : "Сохранено.";
        return true;
    }

    /// <summary>Discards local edits and re-reads the open macro from the library.</summary>
    public void ReloadFromDisk()
    {
        if (_loadedName is null || _store.TryGet(_loadedName) is not { } graph)
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
        foreach (var node in graph.Nodes)
        {
            var row = NodeRowViewModel.FromNode(node);
            AttachNode(row);
            Nodes.Add(row);
        }

        _startNodeId = graph.StartNodeId;
        HasOpenMacro = true;
        RebuildChoices();
        OnPropertyChanged(nameof(StartNodeId));

        // Baseline for "dirty" is the editor's own round-trip, not the file: loading
        // normalises a few shapes, and that normalisation is not a user edit.
        _loadedJson = SerializeCurrent();
        _diskJson = MacroGraphJson.Serialize(graph);
        ChangedOnDisk = false;
        SelectedNode = null;
        ErrorMessage = null;
        StatusMessage = null;
    }

    // ---- hotkey suspension ----------------------------------------------------------

    /// <summary>
    /// Switches global hotkeys off for the lifetime of the dialog. Must be called before
    /// the hotkey picker can work at all — see <see cref="IHotkeySuspension"/>.
    /// </summary>
    public Task SuspendHotkeysAsync() => _hotkeys?.SuspendAsync() ?? Task.CompletedTask;

    /// <summary>Restores global hotkeys from the (possibly just-edited) library.</summary>
    public Task ResumeHotkeysAsync() => _hotkeys?.ResumeAsync() ?? Task.CompletedTask;

    public void Dispose()
    {
        _store.MacrosChanged -= OnMacrosChanged;
        _runs.RunsChanged -= OnRunsChanged;
        foreach (var row in Nodes)
        {
            DetachNode(row);
        }
    }

    // ---- internals ------------------------------------------------------------------

    private void OnMacrosChanged(IReadOnlyList<MacroGraph> macros) =>
        _dispatcher.Post(() => ApplyLibrary(macros));

    private void OnRunsChanged() => _dispatcher.Post(RefreshRunState);

    private void ApplyLibrary(IReadOnlyList<MacroGraph> macros)
    {
        RebuildLibrary(macros);
        RefreshRunState();

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
        var running = _runs.Snapshot()
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
    }

    private void AttachNode(NodeRowViewModel row)
    {
        row.IdChanged += OnNodeIdChanged;
        foreach (var edge in row.Edges)
        {
            edge.Choices = NodeIdChoices;
        }
        if (row is RunMacroNodeRowViewModel runMacro)
        {
            runMacro.MacroChoices = MacroChoices;
        }
    }

    private void DetachNode(NodeRowViewModel row) => row.IdChanged -= OnNodeIdChanged;

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

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
