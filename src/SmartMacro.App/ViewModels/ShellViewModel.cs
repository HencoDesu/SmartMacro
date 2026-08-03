using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Serilog;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;

namespace SmartMacro.App.ViewModels;

/// <summary>The five things the panel can be showing. Order is the sidebar's order.</summary>
public enum ShellMode
{
    Windows,
    Macros,
    Runs,
    Templates,
    Log,
}

/// <summary>
/// One row of the mode sidebar: a name and a counter.
///
/// The counter is <see cref="int"/>? on purpose. «Шаблоны» and «Лог» have no IPC behind them
/// yet, and a made-up number is worse than no number — so their counter is <c>null</c> and
/// the row simply renders without one.
/// </summary>
public sealed class ShellModeViewModel : ObservableObject
{
    private int? _count;
    private bool _showsActivityDot;
    private bool _isSelected;

    internal ShellModeViewModel(ShellMode mode, string title)
    {
        Mode = mode;
        Title = title;
    }

    /// <summary>Which mode this row selects.</summary>
    public ShellMode Mode { get; }

    /// <summary>Sidebar label (Russian, as everywhere in the UI).</summary>
    public string Title { get; }

    /// <summary>Rendered counter, or <c>null</c> when there is nothing honest to show.</summary>
    public string? CounterText => _count?.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>true</c> when the row has a counter to render at all.</summary>
    public bool HasCounter => _count is not null;

    /// <summary>The small accent dot beside the «Прогоны» counter while something is running.</summary>
    public bool ShowsActivityDot
    {
        get => _showsActivityDot;
        private set
        {
            if (SetField(ref _showsActivityDot, value))
            {
                OnPropertyChanged(nameof(CounterIsAccent));
            }
        }
    }

    /// <summary>Drives the 2px accent rule and the 12% wash (the <c>ListBoxItem</c> theme does both).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetField(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(CounterIsAccent));
            }
        }
    }

    /// <summary>
    /// Accent counter for the selected row and for anything live; faint for the rest.
    /// That is exactly the mockup's rule — «Прогоны» keeps its accent number while it has
    /// runs even when another mode is open.
    /// </summary>
    public bool CounterIsAccent => _isSelected || _showsActivityDot;

    internal void SetCount(int? count, bool activityDot = false)
    {
        if (_count != count)
        {
            _count = count;
            OnPropertyChanged(nameof(CounterText));
            OnPropertyChanged(nameof(HasCounter));
        }
        ShowsActivityDot = activityDot;
    }
}

/// <summary>One chip of the sidebar's tag summary: the tag and how many windows carry it.</summary>
public sealed class TagSummaryItemViewModel
{
    internal TagSummaryItemViewModel(string tag, int count)
    {
        Tag = tag;
        Count = count;
        CountText = count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The tag itself.</summary>
    public string Tag { get; }

    /// <summary>How many live windows carry it.</summary>
    public int Count { get; }

    /// <summary>Rendered count — the dim number inside the chip.</summary>
    public string CountText { get; }
}

/// <summary>
/// The 1b shell: one window, modes in a left rail, a run bar pinned at the bottom.
///
/// It owns nothing the daemon knows about. Both mode view-models — <see cref="Workspace"/>
/// (windows + runs) and <see cref="Editor"/> (the macro library) — keep their own IPC
/// subscriptions exactly as they had them when they were a window and a dialog; this class
/// only composes them, derives the sidebar's counters and tag summary from what they already
/// hold, and decides which one is on screen.
///
/// <b>Derived, not fetched.</b> Every number in the sidebar comes from a collection that is
/// already in memory. The tag summary in particular is recomputed from
/// <see cref="WorkspaceViewModel.WindowsChanged"/>, which fires on a <c>WindowTagsChanged</c>
/// push — so tagging a window updates the roster instantly with no round trip.
///
/// <b>Hotkey suspension and the run-event stream are scoped to the «Макросы» mode.</b>
/// See <see cref="ApplyMacrosModeScope"/>.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The library macro behind "Опознать все". Identification stopped being a built-in in
    /// W0.2b — it is the <c>pw-identify</c> example graph, so the button runs that and is
    /// disabled when the library has no such macro rather than firing a request the daemon
    /// would reject.
    /// </summary>
    public const string IdentifyMacroName = "pw-identify";

    private readonly IMacroLauncher? _launcher;
    private ShellModeViewModel _selectedMode;
    private bool _hotkeysSuspended;
    private bool _runEventsSubscribed;

    public ShellViewModel(WorkspaceViewModel workspace, MacroEditorViewModel editor, IMacroLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(editor);

        Workspace = workspace;
        Editor = editor;
        _launcher = launcher;

        Modes =
        [
            new ShellModeViewModel(ShellMode.Windows, "Окна"),
            new ShellModeViewModel(ShellMode.Macros, "Макросы"),
            new ShellModeViewModel(ShellMode.Runs, "Прогоны"),
            new ShellModeViewModel(ShellMode.Templates, "Шаблоны"),
            new ShellModeViewModel(ShellMode.Log, "Лог"),
        ];

        _selectedMode = Modes[0];
        _selectedMode.IsSelected = true;

        Workspace.WindowsChanged += OnWindowsChanged;
        Workspace.Runs.CollectionChanged += OnRunsChanged;
        Editor.Macros.CollectionChanged += OnMacrosChanged;

        RefreshWindowState();
        RefreshRunState();
        RefreshMacroState();
    }

    /// <summary>Windows and runs — the body of «Окна» and «Прогоны», and the run bar.</summary>
    public WorkspaceViewModel Workspace { get; }

    /// <summary>The macro library and the canvas editor — the body of «Макросы».</summary>
    public MacroEditorViewModel Editor { get; }

    /// <summary>The sidebar's rows, in display order.</summary>
    public IReadOnlyList<ShellModeViewModel> Modes { get; }

    /// <summary>Tag → window count, busiest first. The only place the whole roster is visible at once.</summary>
    public ObservableCollection<TagSummaryItemViewModel> TagSummary { get; } = [];

    /// <summary><c>true</c> while any window carries a tag — otherwise the summary shows its empty line.</summary>
    public bool HasTagSummary => TagSummary.Count > 0;

    /// <summary>
    /// Selected sidebar row. Bound two-way from the <c>ListBox</c>; assigning it is the only
    /// way the visible mode changes.
    /// </summary>
    public ShellModeViewModel SelectedMode
    {
        get => _selectedMode;
        set
        {
            // A ListBox pushes null while its ItemsSource churns, and a shell with no mode
            // would render as a blank work area.
            if (value is null || ReferenceEquals(value, _selectedMode))
            {
                return;
            }

            _selectedMode.IsSelected = false;
            SetField(ref _selectedMode, value);
            _selectedMode.IsSelected = true;

            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(IsWindowsMode));
            OnPropertyChanged(nameof(IsMacrosMode));
            OnPropertyChanged(nameof(IsRunsMode));
            OnPropertyChanged(nameof(IsTemplatesMode));
            OnPropertyChanged(nameof(IsLogMode));

            ApplyMacrosModeScope();
        }
    }

    /// <summary>Which mode is on screen.</summary>
    public ShellMode CurrentMode => _selectedMode.Mode;

    /// <summary>Switches modes by identity rather than by row — what code-behind and tests want.</summary>
    public void SelectMode(ShellMode mode) => SelectedMode = Mode(mode);

    public bool IsWindowsMode => CurrentMode == ShellMode.Windows;

    public bool IsMacrosMode => CurrentMode == ShellMode.Macros;

    public bool IsRunsMode => CurrentMode == ShellMode.Runs;

    public bool IsTemplatesMode => CurrentMode == ShellMode.Templates;

    public bool IsLogMode => CurrentMode == ShellMode.Log;

    // ---- run bar ----------------------------------------------------------------------

    /// <summary>
    /// The run the bar names. First of the list rather than "most recent": the list is
    /// normally one entry, and a stable choice keeps the bar from flickering between two
    /// concurrent runs.
    /// </summary>
    public RunningMacroRowViewModel? PrimaryRun =>
        Workspace.Runs.Count > 0 ? Workspace.Runs[0] : null;

    /// <summary><c>true</c> while anything is running — the bar's live/idle switch.</summary>
    public bool HasRuns => Workspace.Runs.Count > 0;

    /// <summary>What the bar says when nothing is running. The bar never collapses.</summary>
    public string IdleText => "нет активных прогонов";

    /// <summary>"+2" when more runs are in flight than the bar can name; empty otherwise.</summary>
    public string OtherRunsText => Workspace.Runs.Count > 1
        ? string.Create(CultureInfo.CurrentCulture, $"+{Workspace.Runs.Count - 1}")
        : string.Empty;

    /// <summary><c>true</c> when <see cref="OtherRunsText"/> has something to show.</summary>
    public bool HasOtherRuns => Workspace.Runs.Count > 1;

    // ---- «Окна» header actions ----------------------------------------------------------

    /// <summary><c>true</c> when the library actually contains <see cref="IdentifyMacroName"/>.</summary>
    public bool CanIdentifyAll { get; private set; }

    /// <summary>Runs the identification macro across every window its selectors match.</summary>
    public void IdentifyAll()
    {
        if (!CanIdentifyAll || _launcher is null)
        {
            return;
        }
        _launcher.RunMacro(IdentifyMacroName);
    }

    // ---- hotkeys ------------------------------------------------------------------------

    /// <summary><c>true</c> while the daemon's global hotkeys are switched off on our behalf.</summary>
    public bool HotkeysSuspended => _hotkeysSuspended;

    /// <summary>
    /// Restores the daemon's hotkeys if this shell suspended them. Called on the way out of
    /// «Макросы» and again when the window closes — the daemon does NOT re-register on its
    /// own when a client disconnects, so a missed resume leaves every global hotkey dead
    /// until the daemon restarts.
    /// </summary>
    public async Task ResumeHotkeysIfSuspendedAsync()
    {
        if (!_hotkeysSuspended)
        {
            return;
        }
        _hotkeysSuspended = false;
        OnPropertyChanged(nameof(HotkeysSuspended));
        await SafeAsync(Editor.ResumeHotkeysAsync(), "resume").ConfigureAwait(false);
    }

    /// <summary>Ticks the run bar and the «Прогоны» list. Driven by the window's 1s timer.</summary>
    public void RefreshElapsed() => Workspace.RefreshElapsed();

    public void Dispose()
    {
        Workspace.WindowsChanged -= OnWindowsChanged;
        Workspace.Runs.CollectionChanged -= OnRunsChanged;
        Editor.Macros.CollectionChanged -= OnMacrosChanged;
        Workspace.Dispose();
        Editor.Dispose();
    }

    // ---- internals ------------------------------------------------------------------------

    /// <summary>
    /// Two things are bracketed by «Макросы» being on screen: global hotkeys go down, and
    /// the run-event stream comes up.
    ///
    /// <b>Hotkeys.</b> Win32 <c>RegisterHotKey</c> swallows presses of a chord it already
    /// owns, so a chord currently bound to a macro would never reach the picker — precisely
    /// the chord a user is most likely to be re-binding. Before D2 the bracket was the
    /// dialog's lifetime; with the editor becoming a mode, the mode's activation is the
    /// nearest equivalent. It is deliberately NOT scoped to "a picker is armed": arming
    /// happens on a click and the suspend is a round trip, so the very first keypress could
    /// still race the daemon.
    ///
    /// <b>Run events (D3b).</b> Same bracket for a different reason: the canvas is the only
    /// thing that renders them, the stream is the only high-rate message in the protocol,
    /// and the daemon produces nothing while nobody is subscribed. Leaving it on for the
    /// whole life of the panel would mean the engine formats a log line for every node of
    /// every macro while the user is looking at a list of windows.
    /// </summary>
    private void ApplyMacrosModeScope()
    {
        var inMacros = CurrentMode == ShellMode.Macros;

        // Two independent latches, deliberately not one: the hotkey one is released early by
        // ResumeHotkeysIfSuspendedAsync on the way out of the process, and sharing a flag
        // would make that release swallow the unsubscribe of a later mode switch.
        if (inMacros != _runEventsSubscribed)
        {
            _runEventsSubscribed = inMacros;
            _ = SafeAsync(Editor.SetRunEventSubscriptionAsync(inMacros), "run-events");
        }

        if (inMacros == _hotkeysSuspended)
        {
            return;
        }

        _hotkeysSuspended = inMacros;
        OnPropertyChanged(nameof(HotkeysSuspended));
        _ = inMacros
            ? SafeAsync(Editor.SuspendHotkeysAsync(), "suspend")
            : SafeAsync(Editor.ResumeHotkeysAsync(), "resume");
    }

    private static async Task SafeAsync(Task task, string what)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось выполнить '{What}' для глобальных хоткеев", what);
        }
    }

    private void OnWindowsChanged() => RefreshWindowState();

    private void OnRunsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshRunState();

    private void OnMacrosChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshMacroState();

    private void RefreshWindowState()
    {
        Mode(ShellMode.Windows).SetCount(Workspace.Windows.Count);
        RebuildTagSummary();
    }

    private void RefreshRunState()
    {
        var count = Workspace.Runs.Count;
        Mode(ShellMode.Runs).SetCount(count, activityDot: count > 0);
        OnPropertyChanged(nameof(PrimaryRun));
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(OtherRunsText));
        OnPropertyChanged(nameof(HasOtherRuns));
    }

    private void RefreshMacroState()
    {
        Mode(ShellMode.Macros).SetCount(Editor.Macros.Count);

        var canIdentify = Editor.Macros.Any(
            item => string.Equals(item.Name, IdentifyMacroName, StringComparison.Ordinal));
        if (canIdentify != CanIdentifyAll)
        {
            CanIdentifyAll = canIdentify;
            OnPropertyChanged(nameof(CanIdentifyAll));
        }
    }

    // Busiest tag first, then alphabetical — a stable order that puts the party's actual
    // composition at the top of the rail.
    private void RebuildTagSummary()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var window in Workspace.Windows)
        {
            foreach (var chip in window.Tags)
            {
                counts[chip.Text] = counts.TryGetValue(chip.Text, out var n) ? n + 1 : 1;
            }
        }

        var ordered = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.CurrentCulture)
            .Select(pair => new TagSummaryItemViewModel(pair.Key, pair.Value))
            .ToList();

        TagSummary.Clear();
        foreach (var item in ordered)
        {
            TagSummary.Add(item);
        }
        OnPropertyChanged(nameof(HasTagSummary));
    }

    private ShellModeViewModel Mode(ShellMode mode) => Modes.First(row => row.Mode == mode);
}
