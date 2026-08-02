using System.Collections.ObjectModel;
using Avalonia.Threading;
using SmartMacro.App.Mvvm;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Orchestration;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Transitional VM for the macros dialog (W0.2b): lists the graphs in the library and
/// lets the operator Run or Stop each one. There is no editing here — the legacy row
/// editor bound to a macro model that no longer exists, and W0.3 builds a real node
/// editor. Meanwhile the JSON files under <c>macros/</c> are hand-editable and hot-reload,
/// so nothing is actually unreachable.
///
/// Two live sources drive the list: <see cref="MacroGraphStore.MacrosChanged"/> (files
/// added/edited/removed) and <see cref="MacroRunRegistry.RunsChanged"/> (Run/Stop button
/// state). Both fire from arbitrary threads, so updates marshal onto the UI thread.
/// </summary>
public sealed class MacrosDialogViewModel : ObservableObject, IDisposable
{
    private readonly MacroGraphStore _store;
    private readonly MacroRunRegistry _runs;
    private readonly Orchestrator _orchestrator;
    private string? _errorMessage;

    public MacrosDialogViewModel(MacroGraphStore store, MacroRunRegistry runs, Orchestrator orchestrator)
    {
        _store = store;
        _runs = runs;
        _orchestrator = orchestrator;

        Rebuild(store.All);
        _store.MacrosChanged += OnMacrosChanged;
        _runs.RunsChanged += OnRunsChanged;
    }

    public ObservableCollection<MacroRowViewModel> Rows { get; } = [];

    /// <summary>Folder holding the macro files — the only "editor" available until W0.3.</summary>
    public string FolderPath => _store.FolderPath;

    /// <summary>Display form of <see cref="FolderPath"/> with a note about hand-editing.</summary>
    public string FolderHint => $"Файлы макросов: {FolderPath}. Правка — вручную, изменения подхватываются на лету.";

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    /// <summary>Starts a macro with no context window — same path as pressing its hotkey.</summary>
    public void Run(MacroRowViewModel row)
    {
        ErrorMessage = null;
        _orchestrator.RunMacro(row.Name);
    }

    /// <summary>Cancels every tracked run of this macro (there is normally at most one).</summary>
    public void Stop(MacroRowViewModel row)
    {
        ErrorMessage = null;
        foreach (var run in _runs.Snapshot())
        {
            if (string.Equals(run.MacroName, row.Name, StringComparison.Ordinal))
            {
                _ = _runs.StopAsync(run.RunId);
            }
        }
    }

    public void Dispose()
    {
        _store.MacrosChanged -= OnMacrosChanged;
        _runs.RunsChanged -= OnRunsChanged;
    }

    private void OnMacrosChanged(IReadOnlyList<MacroGraph> macros) =>
        Dispatcher.UIThread.Post(() => Rebuild(macros));

    private void OnRunsChanged() =>
        Dispatcher.UIThread.Post(RefreshRunState);

    private void Rebuild(IReadOnlyList<MacroGraph> macros)
    {
        Rows.Clear();
        foreach (var macro in macros)
        {
            Rows.Add(new MacroRowViewModel(macro));
        }
        RefreshRunState();
    }

    private void RefreshRunState()
    {
        var running = _runs.Snapshot()
            .Select(run => run.MacroName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            row.IsRunning = running.Contains(row.Name);
        }
    }
}

/// <summary>One macro in the transitional list: its name, a one-line shape summary, and whether it's running.</summary>
public sealed class MacroRowViewModel : ObservableObject
{
    private bool _isRunning;

    public MacroRowViewModel(MacroGraph macro)
    {
        Name = macro.Name;
        Summary = Describe(macro);
    }

    public string Name { get; }

    /// <summary>Triggers and node count — enough to tell the graphs apart without an editor.</summary>
    public string Summary { get; }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

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
            : "без триггеров (вызывается из других макросов)";
        return $"{triggerText} · нод: {macro.Nodes.Count}";
    }

    private static string Chord(string modifiers, string key) =>
        string.Equals(modifiers, "None", StringComparison.Ordinal) ? key : $"{modifiers}+{key}";
}
