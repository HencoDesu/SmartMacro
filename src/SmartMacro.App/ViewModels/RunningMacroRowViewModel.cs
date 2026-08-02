using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;

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

    public RunningMacroRowViewModel(RunningMacroDto run)
    {
        ArgumentNullException.ThrowIfNull(run);
        RunId = run.RunId;
        MacroName = run.MacroName;
        StartedUtc = run.StartedUtc;
        _currentNodeId = run.CurrentNodeId;
        Refresh(DateTimeOffset.UtcNow);
    }

    /// <summary>Daemon-side run id — what a <c>StopMacro</c> request takes.</summary>
    public Guid RunId { get; }

    /// <summary>Name of the macro being run.</summary>
    public string MacroName { get; }

    /// <summary>
    /// Run start time. A <see cref="DateTimeOffset"/> rather than a <see cref="DateTime"/>
    /// because it crossed a process boundary: the wire DTO carries the offset explicitly so
    /// neither end has to guess a <c>DateTimeKind</c>.
    /// </summary>
    public DateTimeOffset StartedUtc { get; }

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
    /// timer tick and whenever the daemon pushes a new run snapshot.
    /// </summary>
    public void Refresh(DateTimeOffset nowUtc, string? currentNodeId = null)
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
