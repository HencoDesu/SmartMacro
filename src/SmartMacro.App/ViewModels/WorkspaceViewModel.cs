using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// The live state the daemon reports: every tracked window with its tag chips, and every
/// macro run in flight. Before D2 this was <c>MainWindowViewModel</c> and it WAS the window;
/// now it is one of the shell's mode view-models, feeding «Окна», «Прогоны» and the run bar
/// at once — hence the rename, and hence the derived partitions below.
///
/// Stage 3 moved the data source out of the process. The shape is unchanged — subscribe
/// first, snapshot second, reconcile by key — but the events now arrive from the daemon and
/// the snapshot is a request:
///
///   * <b>Re-fetch on <see cref="IIpcClient.Connected"/>, not just at construction.</b> The
///     server drops a client that stops draining, so a reconnect is a normal event and
///     everything pushed during the gap is lost. Seeding again is the only way back to the
///     truth, and it is why <see cref="RefreshAsync"/> RECONCILES (dropping rows the daemon
///     no longer reports) instead of merely upserting.
///   * <b>Every handler marshals through <see cref="IUiDispatcher"/>.</b> Events are raised
///     on the client's reader thread; an <c>ObservableCollection</c> may only be touched on
///     the UI thread.
///
/// <b>Derived state (D2).</b> <see cref="TaggedWindows"/> / <see cref="UntaggedWindows"/> are
/// projections of <see cref="Windows"/>, kept current by <see cref="WindowsChanged"/>'s own
/// trigger points rather than by a re-fetch: the 1b layout puts untagged windows in a
/// separate group at the bottom, and the sidebar's tag summary needs the same signal. They
/// are RECONCILED, not rebuilt, so a row the user is typing a tag into keeps its container
/// (and therefore its focus) when an unrelated window appears.
/// </summary>
public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IIpcClient _client;
    private readonly IUiDispatcher _dispatcher;

    public WorkspaceViewModel(IIpcClient client, IUiDispatcher? dispatcher = null)
    {
        _client = client;
        _dispatcher = dispatcher ?? AvaloniaUiDispatcher.Instance;

        _client.Connected += OnConnected;
        _client.EventReceived += OnEventReceived;

        // The connection is normally established before Avalonia (and therefore this VM)
        // exists, so the first Connected has already come and gone. Seed from the live
        // connection; later reconnects go through OnConnected.
        if (_client.IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Raised after any change to the window list OR to any window's tags — the one signal
    /// the shell needs to re-derive its counters and its tag summary without asking the
    /// daemon anything.
    /// </summary>
    public event Action? WindowsChanged;

    /// <summary>Windows currently registered, in the order the daemon reports them.</summary>
    public ObservableCollection<WindowRowViewModel> Windows { get; } = [];

    /// <summary>Windows carrying at least one tag, in <see cref="Windows"/> order.</summary>
    public ObservableCollection<WindowRowViewModel> TaggedWindows { get; } = [];

    /// <summary>Windows with no tags — the 1b layout's subordinate group at the bottom.</summary>
    public ObservableCollection<WindowRowViewModel> UntaggedWindows { get; } = [];

    /// <summary>Macro runs currently tracked by the daemon.</summary>
    public ObservableCollection<RunningMacroRowViewModel> Runs { get; } = [];

    /// <summary>Number of windows that have been identified (= carry at least one tag).</summary>
    public int IdentifiedCount => TaggedWindows.Count;

    /// <summary>Number of windows still waiting for a tag.</summary>
    public int UntaggedCount => UntaggedWindows.Count;

    /// <summary>Header line of the «Окна» mode: "8 опознано · 3 без тегов".</summary>
    public string WindowsSummaryText => Windows.Count == 0
        ? "нет окон под управлением"
        : UntaggedCount == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{IdentifiedCount} опознано")
            : string.Create(CultureInfo.CurrentCulture, $"{IdentifiedCount} опознано · {UntaggedCount} без тегов");

    /// <summary>Separator above the untagged group. Uppercase because the label is rendered as an eyebrow.</summary>
    public string UntaggedHeaderText =>
        string.Create(CultureInfo.CurrentCulture, $"НЕ ОПОЗНАНО · {UntaggedCount}");

    /// <summary><c>true</c> while at least one window is untagged — gates the whole group.</summary>
    public bool HasUntagged => UntaggedWindows.Count > 0;

    /// <summary><c>true</c> while the daemon reports no windows at all — the mode's empty state.</summary>
    public bool HasNoWindows => Windows.Count == 0;

    /// <summary>Header of the «Прогоны» mode; doubles as its empty-state text.</summary>
    public string RunsHeaderText => Runs.Count == 0
        ? "нет активных прогонов"
        : string.Create(CultureInfo.CurrentCulture, $"активных прогонов: {Runs.Count}");

    /// <summary><c>true</c> while at least one run is tracked — gates the "Стоп всё" button.</summary>
    public bool HasRuns => Runs.Count > 0;

    /// <summary>
    /// Re-seeds both lists from the daemon. Called at construction and after every
    /// reconnect; safe to call at any time.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            var windows = await _client.RequestAsync<WindowDto[]>(IpcMessageTypes.GetWindows).ConfigureAwait(false);
            var runs = await _client.RequestAsync<RunningMacroDto[]>(IpcMessageTypes.GetRunningMacros).ConfigureAwait(false);
            // Logged because it is the panel's only externally visible sign of life: if the
            // list looks wrong, this line says whether the daemon reported it that way or
            // the UI mangled it.
            Log.Information(
                "Снимок от демона: окон {Windows}, запусков {Runs}",
                windows?.Length ?? 0,
                runs?.Length ?? 0);
            _dispatcher.Post(() =>
            {
                SyncWindows(windows ?? []);
                SyncRuns(runs ?? []);
            });
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // A drop between connecting and fetching. The maintain loop reconnects and
            // Connected fires again, which retries this — no recovery needed here.
            Log.Warning(ex, "Не удалось получить снимок состояния демона");
        }
    }

    /// <summary>Adds the tag typed into <paramref name="row"/>'s box.</summary>
    public Task<bool> AddTagAsync(WindowRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.AddTagAsync();
    }

    /// <summary>Removes one tag from a window.</summary>
    public Task<bool> RemoveTagAsync(WindowRowViewModel row, string tag)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.RemoveTagAsync(tag);
    }

    /// <summary>Cancels one run. The row disappears when the daemon pushes the new run list.</summary>
    public Task StopRunAsync(RunningMacroRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return StopAsync(row.RunId);
    }

    /// <summary>Cancels every tracked run (the panic button).</summary>
    public async Task StopAllRunsAsync()
    {
        // One request per run rather than a bulk message: StopMacro already exists, the list
        // is single-digit, and the daemon answers each one only after the runner acknowledges.
        foreach (var runId in Runs.Select(row => row.RunId).ToList())
        {
            await StopAsync(runId).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Re-renders the elapsed column. Driven by the window's 1s timer — the VM keeps no
    /// timer of its own so it stays free of Avalonia types.
    /// </summary>
    public void RefreshElapsed()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in Runs)
        {
            row.Refresh(now);
        }
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.EventReceived -= OnEventReceived;
    }

    // ---- daemon plumbing ---------------------------------------------------------------

    private void OnConnected() => _ = RefreshAsync();

    private void OnEventReceived(IpcEvent evt)
    {
        switch (evt.Type)
        {
            case IpcMessageTypes.WindowAppeared:
            case IpcMessageTypes.WindowTagsChanged:
                // Both carry the FULL new state, so one upsert serves both: an appearance is
                // just an upsert that happens to find nothing.
                if (IpcJson.Read<WindowDto>(evt.Payload) is { } window)
                {
                    _dispatcher.Post(() =>
                    {
                        Upsert(window);
                        NotifyWindowsChanged();
                    });
                }
                break;

            case IpcMessageTypes.WindowClosed:
                if (IpcJson.Read<WindowClosedEvent>(evt.Payload) is { } closed)
                {
                    _dispatcher.Post(() => Remove(closed.Hwnd));
                }
                break;

            case IpcMessageTypes.RunningMacrosChanged:
                // This one carries the whole new list, so no GetRunningMacros round trip.
                var runs = IpcJson.Read<RunningMacroDto[]>(evt.Payload) ?? [];
                _dispatcher.Post(() => SyncRuns(runs));
                break;

            default:
                break; // MacrosChanged / ActivateWindow belong to other listeners
        }
    }

    private async Task StopAsync(Guid runId)
    {
        try
        {
            await _client.RequestAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(runId)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось остановить запуск {RunId}", runId);
        }
    }

    // ---- collection reconciliation -----------------------------------------------------

    // Add-or-update, keyed on hwnd. Idempotent so the "subscribe, then snapshot" startup
    // order can't produce a duplicate row for a window that appeared in between.
    // Callers are responsible for NotifyWindowsChanged() — SyncWindows does a batch.
    private void Upsert(WindowDto window)
    {
        if (FindRow(window.Hwnd) is { } existing)
        {
            existing.ApplyTags(window.Tags);
            return;
        }

        Windows.Add(new WindowRowViewModel(_client, window));
    }

    private void Remove(long hwnd)
    {
        if (FindRow(hwnd) is { } row)
        {
            Windows.Remove(row);
            NotifyWindowsChanged();
        }
    }

    // Full reconcile: a reconnect may have missed a WindowClosed, so anything absent from
    // the snapshot has to go, while surviving windows keep their row (and its half-typed
    // tag box).
    private void SyncWindows(IReadOnlyList<WindowDto> snapshot)
    {
        var seen = new HashSet<long>();
        foreach (var window in snapshot)
        {
            seen.Add(window.Hwnd);
            Upsert(window);
        }

        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Windows[i].Hwnd))
            {
                Windows.RemoveAt(i);
            }
        }
        NotifyWindowsChanged();
    }

    // Runs come and go wholesale, but rows are matched on RunId so a surviving run keeps
    // its row object — and therefore its rendered elapsed value — across a refresh.
    private void SyncRuns(IReadOnlyList<RunningMacroDto> snapshot)
    {
        var now = DateTimeOffset.UtcNow;
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

    // The one place the derived state is refreshed. Cheap by design: the list is ~10 rows,
    // so an O(n²) reconcile is not worth avoiding, and doing it eagerly means no view ever
    // sees a stale partition.
    private void NotifyWindowsChanged()
    {
        Repartition();
        OnPropertyChanged(nameof(IdentifiedCount));
        OnPropertyChanged(nameof(UntaggedCount));
        OnPropertyChanged(nameof(WindowsSummaryText));
        OnPropertyChanged(nameof(UntaggedHeaderText));
        OnPropertyChanged(nameof(HasUntagged));
        OnPropertyChanged(nameof(HasNoWindows));
        WindowsChanged?.Invoke();
    }

    private void Repartition()
    {
        var tagged = new List<WindowRowViewModel>(Windows.Count);
        var untagged = new List<WindowRowViewModel>();
        foreach (var row in Windows)
        {
            (row.HasTags ? tagged : untagged).Add(row);
        }

        Reconcile(TaggedWindows, tagged);
        Reconcile(UntaggedWindows, untagged);

        // Alternating row backgrounds are the mockup's, and Avalonia's ItemsControl has no
        // alternation index — so the index lives on the row. Only the tagged group
        // alternates; the untagged group is uniformly muted.
        for (var i = 0; i < tagged.Count; i++)
        {
            tagged[i].IsAlternate = i % 2 == 1;
        }
        foreach (var row in untagged)
        {
            row.IsAlternate = false;
        }
    }

    // Remove-then-place rather than clear-and-refill: rebuilding the collection would
    // recreate every container and drop the focus out of a tag box mid-typing.
    private static void Reconcile(
        ObservableCollection<WindowRowViewModel> target,
        IReadOnlyList<WindowRowViewModel> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var current = target.IndexOf(desired[i]);
            if (current < 0)
            {
                target.Insert(i, desired[i]);
            }
            else if (current != i)
            {
                target.Move(current, i);
            }
        }
    }

    private WindowRowViewModel? FindRow(long hwnd)
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
