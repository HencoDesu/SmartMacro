using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Threading;
using PerfectWorldAgent.App.Mvvm;
using PerfectWorldAgent.Macro;
using PerfectWorldAgent.Models;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.App.ViewModels;

// VM for MacrosDialog. Hierarchy:
//   MacrosDialogViewModel
//     Rows: ObservableCollection<MacroRowViewModel>
//       ClassPanels: ObservableCollection<MacroClassPanelViewModel>
//         Actions: ObservableCollection<MacroActionRowViewModel>  // polymorphic
//
// Polymorphic action rows render via Window-level DataTemplates: KeyPressRowViewModel
// → KeyBindingPicker; DelayRowViewModel → TextBox in SECONDS (decimal) for ergonomic
// match with PW's in-game cast-time units (e.g. 1.5 sec instead of 1500 ms).
//
// Save: walks the VM tree and rebuilds Dictionary<CharacterClass, List<MacroAction>>
// for each Macro, validates, persists via MacroLibrary.
public sealed class MacrosDialogViewModel : ObservableObject, IDisposable
{
    private readonly MacroLibrary _library;
    private readonly Orchestrator _orchestrator;
    private string? _errorMessage;

    public MacrosDialogViewModel(MacroLibrary library, Orchestrator orchestrator)
    {
        _library = library;
        _orchestrator = orchestrator;
        Rows = new ObservableCollection<MacroRowViewModel>();
        RebuildRows(library.Macros);

        // Hot-reload — if macros.json is edited externally while the dialog is open,
        // library fires MacrosChanged on threadpool; marshal to UI thread and rebuild.
        // Warning to future us: this DISCARDS any in-progress edits in the dialog.
        _library.MacrosChanged += OnLibraryReloaded;
    }

    public void Dispose()
    {
        _library.MacrosChanged -= OnLibraryReloaded;
    }

    private void OnLibraryReloaded(IReadOnlyList<Macro.Macro> macros)
    {
        Dispatcher.UIThread.Post(() => RebuildRows(macros));
    }

    private void RebuildRows(IReadOnlyList<Macro.Macro> macros)
    {
        Rows.Clear();
        foreach (var m in macros)
        {
            var row = new MacroRowViewModel(m.Name);
            foreach (var (cls, actions) in m.ActionsByClass)
            {
                var panel = new MacroClassPanelViewModel(cls);
                foreach (var a in actions)
                {
                    MacroActionRowViewModel rowVm = a switch
                    {
                        KeyPressAction k => new KeyPressRowViewModel(k.Key),
                        DelayAction d => new DelayRowViewModel(d.Ms),
                        ClickAction c => new ClickRowViewModel(c.Point, c.DoubleClick),
                        _ => throw new InvalidOperationException($"Unknown action type {a.GetType()}"),
                    };
                    panel.Actions.Add(rowVm);
                }
                row.ClassPanels.Add(panel);
            }
            Rows.Add(row);
        }
    }

    public ObservableCollection<MacroRowViewModel> Rows { get; }

    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    public void AddMacro() =>
        Rows.Add(new MacroRowViewModel($"macro{Rows.Count + 1}"));

    public void RemoveMacro(MacroRowViewModel row) => Rows.Remove(row);

    public void RunMacro(MacroRowViewModel row)
    {
        var name = row.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorMessage = "Cannot run a macro with no name.";
            return;
        }
        ErrorMessage = null;
        _orchestrator.BroadcastMacro(name);
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        var parsed = new List<Macro.Macro>(Rows.Count);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Rows)
        {
            var name = row.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                ErrorMessage = "Every macro needs a name.";
                return false;
            }
            if (!seenNames.Add(name))
            {
                ErrorMessage = $"Duplicate macro name '{name}'.";
                return false;
            }
            if (row.ClassPanels.Count == 0)
            {
                ErrorMessage = $"Macro '{name}': add at least one class block.";
                return false;
            }

            var actionsByClass = new Dictionary<CharacterClass, List<MacroAction>>();
            foreach (var panel in row.ClassPanels)
            {
                if (panel.Class == CharacterClass.Unknown)
                {
                    ErrorMessage = $"Macro '{name}': class block left as Unknown — pick a real class.";
                    return false;
                }
                if (actionsByClass.ContainsKey(panel.Class))
                {
                    ErrorMessage = $"Macro '{name}': class {panel.Class} appears twice — merge the blocks.";
                    return false;
                }

                var actions = new List<MacroAction>();
                foreach (var ar in panel.Actions)
                {
                    switch (ar)
                    {
                        case KeyPressRowViewModel k:
                            if (!Enum.TryParse<VirtualKey>(k.Key, ignoreCase: true, out var vk))
                            {
                                ErrorMessage = $"Macro '{name}', class {panel.Class}: '{k.Key}' is not a known VirtualKey.";
                                return false;
                            }
                            actions.Add(new KeyPressAction(vk));
                            break;

                        case DelayRowViewModel d:
                            if (!double.TryParse(d.SecondsText, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
                            {
                                ErrorMessage = $"Macro '{name}', class {panel.Class}: delay '{d.SecondsText}' is not a non-negative number of seconds.";
                                return false;
                            }
                            actions.Add(new DelayAction((int)Math.Round(seconds * 1000.0)));
                            break;

                        case ClickRowViewModel c:
                            if (!int.TryParse(c.XText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) || x < 0)
                            {
                                ErrorMessage = $"Macro '{name}', class {panel.Class}: click X '{c.XText}' is not a non-negative integer.";
                                return false;
                            }
                            if (!int.TryParse(c.YText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) || y < 0)
                            {
                                ErrorMessage = $"Macro '{name}', class {panel.Class}: click Y '{c.YText}' is not a non-negative integer.";
                                return false;
                            }
                            actions.Add(new ClickAction(new ScreenPoint(x, y), c.DoubleClick));
                            break;
                    }
                }
                actionsByClass[panel.Class] = actions;
            }

            parsed.Add(new Macro.Macro { Name = name, ActionsByClass = actionsByClass });
        }

        try
        {
            await _library.ReplaceAsync(parsed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save: {ex.Message}";
            return false;
        }

        return true;
    }
}

public sealed class MacroRowViewModel : ObservableObject
{
    private string _name;

    public MacroRowViewModel(string name)
    {
        _name = name;
        ClassPanels = new ObservableCollection<MacroClassPanelViewModel>();
        ClassPanels.CollectionChanged += OnClassPanelsChanged;
    }

    public string Name { get => _name; set => SetField(ref _name, value); }

    public ObservableCollection<MacroClassPanelViewModel> ClassPanels { get; }

    public void AddClassPanel()
    {
        var used = new HashSet<CharacterClass>(ClassPanels.Select(p => p.Class));
        var firstUnused = MacroClassPanelViewModel.AllClasses.FirstOrDefault(c => !used.Contains(c));
        // If every class is used, fall back to first real class (rare — 18 classes available).
        if (firstUnused == default && MacroClassPanelViewModel.AllClasses.Count > 0)
        {
            firstUnused = MacroClassPanelViewModel.AllClasses[0];
        }
        ClassPanels.Add(new MacroClassPanelViewModel(firstUnused));
    }

    public void RemoveClassPanel(MacroClassPanelViewModel panel) => ClassPanels.Remove(panel);

    public void MoveClassPanelUp(MacroClassPanelViewModel panel)
    {
        var idx = ClassPanels.IndexOf(panel);
        if (idx > 0) ClassPanels.Move(idx, idx - 1);
    }

    public void MoveClassPanelDown(MacroClassPanelViewModel panel)
    {
        var idx = ClassPanels.IndexOf(panel);
        if (idx >= 0 && idx < ClassPanels.Count - 1) ClassPanels.Move(idx, idx + 1);
    }

    private void OnClassPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (MacroClassPanelViewModel panel in e.NewItems)
        {
            panel.Parent = this;
        }
    }
}

public sealed class MacroClassPanelViewModel : ObservableObject
{
    public static IReadOnlyList<CharacterClass> AllClasses { get; } =
        Enum.GetValues<CharacterClass>().Where(c => c != CharacterClass.Unknown).ToArray();

    private CharacterClass _class;

    public MacroClassPanelViewModel(CharacterClass cls)
    {
        _class = cls;
        Actions = new ObservableCollection<MacroActionRowViewModel>();
        Actions.CollectionChanged += OnActionsChanged;
    }

    public MacroRowViewModel? Parent { get; internal set; }

    public CharacterClass Class { get => _class; set => SetField(ref _class, value); }

    public ObservableCollection<MacroActionRowViewModel> Actions { get; }

    public void AddKeyPress() => Actions.Add(new KeyPressRowViewModel());
    public void AddDelay() => Actions.Add(new DelayRowViewModel());
    public void AddClick() => Actions.Add(new ClickRowViewModel());
    public void RemoveAction(MacroActionRowViewModel row) => Actions.Remove(row);

    public void MoveActionUp(MacroActionRowViewModel row)
    {
        var idx = Actions.IndexOf(row);
        if (idx > 0) Actions.Move(idx, idx - 1);
    }

    public void MoveActionDown(MacroActionRowViewModel row)
    {
        var idx = Actions.IndexOf(row);
        if (idx >= 0 && idx < Actions.Count - 1) Actions.Move(idx, idx + 1);
    }

    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (MacroActionRowViewModel action in e.NewItems)
        {
            action.Parent = this;
        }
    }
}

// Polymorphic base — concrete subclasses render via Window-level DataTemplates.
public abstract class MacroActionRowViewModel : ObservableObject
{
    public MacroClassPanelViewModel? Parent { get; internal set; }
}

public sealed class KeyPressRowViewModel : MacroActionRowViewModel
{
    private string _key;
    public KeyPressRowViewModel() : this(VirtualKey.F1) { }
    public KeyPressRowViewModel(VirtualKey key) { _key = key.ToString(); }
    public string Key { get => _key; set => SetField(ref _key, value); }
}

public sealed class DelayRowViewModel : MacroActionRowViewModel
{
    private string _secondsText;
    public DelayRowViewModel() : this(1000) { }
    public DelayRowViewModel(int ms)
    {
        _secondsText = (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);
    }
    public string SecondsText { get => _secondsText; set => SetField(ref _secondsText, value); }
}

public sealed class ClickRowViewModel : MacroActionRowViewModel
{
    private string _xText;
    private string _yText;
    private bool _doubleClick;
    public ClickRowViewModel() : this(new ScreenPoint(0, 0), false) { }
    public ClickRowViewModel(ScreenPoint point, bool doubleClick)
    {
        _xText = point.X.ToString(CultureInfo.InvariantCulture);
        _yText = point.Y.ToString(CultureInfo.InvariantCulture);
        _doubleClick = doubleClick;
    }
    public string XText { get => _xText; set => SetField(ref _xText, value); }
    public string YText { get => _yText; set => SetField(ref _yText, value); }
    public bool DoubleClick { get => _doubleClick; set => SetField(ref _doubleClick, value); }
}
