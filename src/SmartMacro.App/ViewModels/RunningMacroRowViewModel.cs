using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Execution;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// One entry of the "running macros" panel: what is running, for how long, and where the
/// walker currently is.
///
/// <see cref="Elapsed"/> is refreshed by an external tick (the main window drives a 1s
/// timer) rather than by the VM owning a <c>DispatcherTimer</c> — that keeps every VM in
/// this assembly free of Avalonia types and therefore unit-testable.
/// </summary>
public sealed class RunningMacroRowViewModel : ObservableObject
{
    private string? _currentNodeId;
    private string _elapsed = "0:00";

    public RunningMacroRowViewModel(MacroRunSnapshot snapshot)
    {
        RunId = snapshot.RunId;
        MacroName = snapshot.MacroName;
        StartedUtc = snapshot.StartedUtc;
        _currentNodeId = snapshot.CurrentNodeId;
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Registry id — what <c>MacroRunRegistry.StopAsync</c> takes.</summary>
    public Guid RunId { get; }

    /// <summary>Name of the macro being run.</summary>
    public string MacroName { get; }

    /// <summary>Run start time, UTC.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>Id of the node the walker last entered; <c>null</c> before the first node.</summary>
    public string? CurrentNodeId
    {
        get => _currentNodeId;
        private set
        {
            if (SetField(ref _currentNodeId, value))
            {
                OnPropertyChanged(nameof(CurrentNodeText));
            }
        }
    }

    /// <summary>Display form of <see cref="CurrentNodeId"/>.</summary>
    public string CurrentNodeText => string.IsNullOrEmpty(_currentNodeId) ? "—" : _currentNodeId;

    /// <summary>Wall-clock time since the run started, as <c>m:ss</c> (or <c>h:mm:ss</c>).</summary>
    public string Elapsed
    {
        get => _elapsed;
        private set => SetField(ref _elapsed, value);
    }

    /// <summary>
    /// Re-renders <see cref="Elapsed"/> and picks up the walker's latest node. Called on a
    /// timer tick and whenever the registry reports a change.
    /// </summary>
    public void Refresh(DateTime nowUtc, string? currentNodeId = null)
    {
        if (currentNodeId is not null)
        {
            CurrentNodeId = currentNodeId;
        }

        var elapsed = nowUtc - StartedUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        Elapsed = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
