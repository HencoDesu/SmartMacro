using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Threading;
using SmartMacro.App.Mvvm;
using SmartMacro.Macro;
using SmartMacro.Native;
using SmartMacro.Orchestration;

namespace SmartMacro.App.ViewModels;

// VM for MacrosDialog. Hierarchy:
//   MacrosDialogViewModel
//     Rows: ObservableCollection<MacroRowViewModel>
//       TagPanels: ObservableCollection<MacroTagPanelViewModel>
//         Actions: ObservableCollection<MacroActionRowViewModel>  // polymorphic
//
// Polymorphic action rows render via Window-level DataTemplates: KeyPressRowViewModel
// → KeyBindingPicker; DelayRowViewModel → TextBox in SECONDS (decimal) for ergonomic
// match with PW's in-game cast-time units (e.g. 1.5 sec instead of 1500 ms).
//
// Save: walks the VM tree and rebuilds Dictionary<string, List<MacroAction>> (tag →
// actions) for each Macro, validates, persists via MacroLibrary.
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
            foreach (var (tag, actions) in m.ActionsByTag)
            {
                var panel = new MacroTagPanelViewModel(tag);
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
                row.TagPanels.Add(panel);
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
            if (row.TagPanels.Count == 0)
            {
                ErrorMessage = $"Macro '{name}': add at least one tag block.";
                return false;
            }

            var actionsByTag = new Dictionary<string, List<MacroAction>>(StringComparer.Ordinal);
            foreach (var panel in row.TagPanels)
            {
                var tag = panel.Tag?.Trim() ?? string.Empty;
                if (tag.Length == 0)
                {
                    ErrorMessage = $"Macro '{name}': tag block left empty — type a tag.";
                    return false;
                }
                if (actionsByTag.ContainsKey(tag))
                {
                    ErrorMessage = $"Macro '{name}': tag '{tag}' appears twice — merge the blocks.";
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
                                ErrorMessage = $"Macro '{name}', tag '{tag}': '{k.Key}' is not a known VirtualKey.";
                                return false;
                            }
                            actions.Add(new KeyPressAction(vk));
                            break;

                        case DelayRowViewModel d:
                            if (!double.TryParse(d.SecondsText, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
                            {
                                ErrorMessage = $"Macro '{name}', tag '{tag}': delay '{d.SecondsText}' is not a non-negative number of seconds.";
                                return false;
                            }
                            actions.Add(new DelayAction((int)Math.Round(seconds * 1000.0)));
                            break;

                        case ClickRowViewModel c:
                            if (!int.TryParse(c.XText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) || x < 0)
                            {
                                ErrorMessage = $"Macro '{name}', tag '{tag}': click X '{c.XText}' is not a non-negative integer.";
                                return false;
                            }
                            if (!int.TryParse(c.YText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) || y < 0)
                            {
                                ErrorMessage = $"Macro '{name}', tag '{tag}': click Y '{c.YText}' is not a non-negative integer.";
                                return false;
                            }
                            actions.Add(new ClickAction(new ScreenPoint(x, y), c.DoubleClick));
                            break;
                    }
                }
                actionsByTag[tag] = actions;
            }

            parsed.Add(new Macro.Macro { Name = name, ActionsByTag = actionsByTag });
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
        TagPanels = new ObservableCollection<MacroTagPanelViewModel>();
        TagPanels.CollectionChanged += OnTagPanelsChanged;
    }

    public string Name { get => _name; set => SetField(ref _name, value); }

    public ObservableCollection<MacroTagPanelViewModel> TagPanels { get; }

    public void AddTagPanel() => TagPanels.Add(new MacroTagPanelViewModel(string.Empty));

    public void RemoveTagPanel(MacroTagPanelViewModel panel) => TagPanels.Remove(panel);

    public void MoveTagPanelUp(MacroTagPanelViewModel panel)
    {
        var idx = TagPanels.IndexOf(panel);
        if (idx > 0) TagPanels.Move(idx, idx - 1);
    }

    public void MoveTagPanelDown(MacroTagPanelViewModel panel)
    {
        var idx = TagPanels.IndexOf(panel);
        if (idx >= 0 && idx < TagPanels.Count - 1) TagPanels.Move(idx, idx + 1);
    }

    private void OnTagPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (MacroTagPanelViewModel panel in e.NewItems)
        {
            panel.Parent = this;
        }
    }
}

public sealed class MacroTagPanelViewModel : ObservableObject
{
    private string _tag;

    public MacroTagPanelViewModel(string tag)
    {
        _tag = tag;
        Actions = new ObservableCollection<MacroActionRowViewModel>();
        Actions.CollectionChanged += OnActionsChanged;
    }

    public MacroRowViewModel? Parent { get; internal set; }

    /// <summary>Free-form tag key — actions run on windows whose tag set contains it.</summary>
    public string Tag { get => _tag; set => SetField(ref _tag, value); }

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
    public MacroTagPanelViewModel? Parent { get; internal set; }
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
