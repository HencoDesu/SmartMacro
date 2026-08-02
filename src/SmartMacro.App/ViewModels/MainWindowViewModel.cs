using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Execution;
using SmartMacro.Windows;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// The main window: every tracked window with its tag chips, plus the live list of running
/// macros.
///
/// W0.3 replaced the old agent-row list with this. The change is not cosmetic — after W0.1
/// there is no per-character state to show, only windows and the free-form tags that route
/// macros at them, and after W0.2b the interesting runtime state is "which macros are
/// executing right now".
///
/// Both sources (<see cref="WindowRegistry"/>, <see cref="MacroRunRegistry"/>) raise events
/// from arbitrary threads, so every handler marshals through <see cref="IUiDispatcher"/>
/// before touching an <c>ObservableCollection</c>. Subscription happens BEFORE the initial
/// snapshot and the reconcile is keyed on hwnd/run-id, so a window that appears in that
/// window shows up exactly once instead of racing into a duplicate or a miss.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly WindowRegistry _registry;
    private readonly MacroRunRegistry _runs;
    private readonly IUiDispatcher _dispatcher;

    public MainWindowViewModel(WindowRegistry registry, MacroRunRegistry runs, IUiDispatcher? dispatcher = null)
    {
        _registry = registry;
        _runs = runs;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;

        _registry.WindowAppeared += OnWindowAppeared;
        _registry.WindowTagsChanged += OnWindowTagsChanged;
        _registry.WindowClosed += OnWindowClosed;
        _runs.RunsChanged += OnRunsChanged;

        SyncWindows(_registry.Snapshot());
        SyncRuns(_runs.Snapshot());
    }

    /// <summary>Windows currently registered, in order of appearance.</summary>
    public ObservableCollection<WindowRowViewModel> Windows { get; } = [];

    /// <summary>Macro runs currently tracked by the registry.</summary>
    public ObservableCollection<RunningMacroRowViewModel> Runs { get; } = [];

    /// <summary>Footer counter.</summary>
    public string WindowCountText =>
        string.Create(CultureInfo.CurrentCulture, $"Окон под управлением: {Windows.Count}");

    /// <summary>Header of the running-macros panel; doubles as its empty-state text.</summary>
    public string RunsHeaderText => Runs.Count == 0
        ? "Запущенные макросы: нет"
        : string.Create(CultureInfo.CurrentCulture, $"Запущенные макросы: {Runs.Count}");

    /// <summary><c>true</c> while at least one run is tracked — gates the "Стоп всё" button.</summary>
    public bool HasRuns => Runs.Count > 0;

    /// <summary>Adds the tag typed into <paramref name="row"/>'s box.</summary>
    public bool AddTag(WindowRowViewModel row) => row.AddTag();

    /// <summary>Removes one tag from a window.</summary>
    public bool RemoveTag(WindowRowViewModel row, string tag) => row.RemoveTag(tag);

    /// <summary>Cancels one run. The registry raises <c>RunsChanged</c> when the runner acknowledges.</summary>
    public void StopRun(RunningMacroRowViewModel row) => _ = _runs.StopAsync(row.RunId);

    /// <summary>Cancels every tracked run (the panic button).</summary>
    public void StopAllRuns() => _ = _runs.StopAllAsync();

    /// <summary>
    /// Re-renders the elapsed column. Driven by the window's 1s timer — the VM keeps no
    /// timer of its own so it stays free of Avalonia types.
    /// </summary>
    public void RefreshElapsed()
    {
        var now = DateTime.UtcNow;
        foreach (var row in Runs)
        {
            row.Refresh(now);
        }
    }

    public void Dispose()
    {
        _registry.WindowAppeared -= OnWindowAppeared;
        _registry.WindowTagsChanged -= OnWindowTagsChanged;
        _registry.WindowClosed -= OnWindowClosed;
        _runs.RunsChanged -= OnRunsChanged;
    }

    private void OnWindowAppeared(ManagedWindowInfo info) => _dispatcher.Post(() => Upsert(info));

    private void OnWindowTagsChanged(ManagedWindowInfo info) => _dispatcher.Post(() => Upsert(info));

    private void OnWindowClosed(ManagedWindowInfo info) => _dispatcher.Post(() =>
    {
        if (FindRow(info.Hwnd) is { } row)
        {
            Windows.Remove(row);
            OnPropertyChanged(nameof(WindowCountText));
        }
    });

    private void OnRunsChanged() => _dispatcher.Post(() => SyncRuns(_runs.Snapshot()));

    // Add-or-update, keyed on hwnd. Idempotent so the "subscribe, then snapshot" startup
    // order can't produce a duplicate row for a window that appeared in between.
    private void Upsert(ManagedWindowInfo info)
    {
        if (FindRow(info.Hwnd) is { } existing)
        {
            existing.ApplyTags(info.Tags);
            return;
        }

        Windows.Add(new WindowRowViewModel(_registry, info));
        OnPropertyChanged(nameof(WindowCountText));
    }

    private void SyncWindows(IReadOnlyList<ManagedWindowInfo> snapshot)
    {
        foreach (var info in snapshot)
        {
            Upsert(info);
        }
    }

    // Runs come and go wholesale, but rows are matched on RunId so a surviving run keeps
    // its row object — and therefore its rendered elapsed value — across a refresh.
    private void SyncRuns(IReadOnlyList<MacroRunSnapshot> snapshot)
    {
        var now = DateTime.UtcNow;
        var seen = new HashSet<Guid>();

        foreach (var run in snapshot)
        {
            seen.Add(run.RunId);
            if (FindRun(run.RunId) is { } existing)
            {
                existing.Refresh(now, run.CurrentNodeId);
            }
            else
            {
                Runs.Add(new RunningMacroRowViewModel(run));
            }
        }

        for (var i = Runs.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Runs[i].RunId))
            {
                Runs.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(RunsHeaderText));
        OnPropertyChanged(nameof(HasRuns));
    }

    private WindowRowViewModel? FindRow(IntPtr hwnd)
    {
        foreach (var row in Windows)
        {
            if (row.Hwnd == hwnd)
            {
                return row;
            }
        }
        return null;
    }

    private RunningMacroRowViewModel? FindRun(Guid runId)
    {
        foreach (var row in Runs)
        {
            if (row.RunId == runId)
            {
                return row;
            }
        }
        return null;
    }
}
