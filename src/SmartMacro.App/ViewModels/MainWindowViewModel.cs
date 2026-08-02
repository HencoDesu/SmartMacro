using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// The main window: every tracked window with its tag chips, plus the live list of running
/// macros.
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
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IIpcClient _client;
    private readonly IUiDispatcher _dispatcher;

    public MainWindowViewModel(IIpcClient client, IUiDispatcher? dispatcher = null)
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

    /// <summary>Windows currently registered, in the order the daemon reports them.</summary>
    public ObservableCollection<WindowRowViewModel> Windows { get; } = [];

    /// <summary>Macro runs currently tracked by the daemon.</summary>
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
                    _dispatcher.Post(() => Upsert(window));
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
    private void Upsert(WindowDto window)
    {
        if (FindRow(window.Hwnd) is { } existing)
        {
            existing.ApplyTags(window.Tags);
            return;
        }

        Windows.Add(new WindowRowViewModel(_client, window));
        OnPropertyChanged(nameof(WindowCountText));
    }

    private void Remove(long hwnd)
    {
        if (FindRow(hwnd) is { } row)
        {
            Windows.Remove(row);
            OnPropertyChanged(nameof(WindowCountText));
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
        OnPropertyChanged(nameof(WindowCountText));
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
