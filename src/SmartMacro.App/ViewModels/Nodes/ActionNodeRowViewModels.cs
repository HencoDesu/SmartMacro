using System.Collections.ObjectModel;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Common shape of the seven action nodes: exactly one outgoing edge (<c>Next</c>). The
/// edge object is created here so subclasses (and the XAML) can reach it by name instead
/// of by index.
/// </summary>
public abstract class ActionNodeRowViewModel : NodeRowViewModel
{
    protected ActionNodeRowViewModel(string nodeId, TargetSelectorViewModel? target, string? next)
        : base(nodeId, target, new NodeEdgeViewModel("Далее", next))
    {
    }

    /// <summary>The single outgoing edge.</summary>
    public NodeEdgeViewModel Next => Edges[0];
}

/// <summary>Editor for <see cref="KeyPressNode"/>: one key, picked with the game-style capture control.</summary>
public sealed class KeyPressNodeRowViewModel : ActionNodeRowViewModel
{
    private string _keyName;

    public KeyPressNodeRowViewModel(KeyPressNode node)
        : base(node.Id, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        _keyName = node.Key.ToString();
    }

    public override string TypeLabel => "Нажать клавишу";

    /// <summary>
    /// <see cref="VirtualKey"/> member name — the string form
    /// <c>Controls.KeyBindingPicker</c> binds to (it captures Avalonia key names, which
    /// the enum deliberately mirrors).
    /// </summary>
    public string KeyName
    {
        get => _keyName;
        set => SetField(ref _keyName, value ?? string.Empty);
    }

    public override MacroNode ToNode() => new KeyPressNode
    {
        Id = NodeId,
        Editor = Editor,
        Key = ParseKey(_keyName),
        Target = Target?.ToSelector(),
        Next = Next.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (!Enum.TryParse<VirtualKey>(_keyName, out var key) || key == 0)
        {
            yield return $"[{NodeId}] клавиша не задана.";
        }
    }

    private static VirtualKey ParseKey(string name) =>
        Enum.TryParse<VirtualKey>(name, out var key) ? key : default;
}

/// <summary>
/// Editor for <see cref="ClickNode"/>. The model requires EXACTLY one of Point / PointVar,
/// so the two are presented as a toggle rather than as two independently fillable fields —
/// there is no way to leave both set or both empty from here.
/// </summary>
public sealed class ClickNodeRowViewModel : ActionNodeRowViewModel
{
    private string _xText;
    private string _yText;
    private string _pointVar;
    private bool _useVariable;
    private bool _doubleClick;

    public ClickNodeRowViewModel(ClickNode node)
        : base(node.Id, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        var point = node.Point ?? default;
        _xText = NodeInput.FormatInt(point.X);
        _yText = NodeInput.FormatInt(point.Y);
        _useVariable = node.PointVar is not null;
        _pointVar = node.PointVar ?? "cursor";
        _doubleClick = node.DoubleClick;
    }

    public override string TypeLabel => "Клик";

    /// <summary><c>true</c> = take the point from a run variable instead of the literal X/Y.</summary>
    public bool UseVariable
    {
        get => _useVariable;
        set
        {
            if (SetField(ref _useVariable, value))
            {
                OnPropertyChanged(nameof(UseLiteralPoint));
            }
        }
    }

    /// <summary>Inverse of <see cref="UseVariable"/>, for the X/Y block's visibility.</summary>
    public bool UseLiteralPoint => !_useVariable;

    public string XText { get => _xText; set => SetField(ref _xText, value); }

    public string YText { get => _yText; set => SetField(ref _yText, value); }

    /// <summary>Run variable holding the click point — <c>"cursor"</c> is the one the trigger always seeds.</summary>
    public string PointVar
    {
        get => _pointVar;
        set => SetField(ref _pointVar, value ?? string.Empty);
    }

    public bool DoubleClick { get => _doubleClick; set => SetField(ref _doubleClick, value); }

    public override MacroNode ToNode() => new ClickNode
    {
        Id = NodeId,
        Editor = Editor,
        Point = _useVariable ? null : new ScreenPoint(NodeInput.ParseInt(_xText) ?? 0, NodeInput.ParseInt(_yText) ?? 0),
        PointVar = _useVariable ? _pointVar : null,
        DoubleClick = _doubleClick,
        Target = Target?.ToSelector(),
        Next = Next.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (_useVariable)
        {
            if (string.IsNullOrWhiteSpace(_pointVar))
            {
                yield return $"[{NodeId}] имя переменной не задано.";
            }
            yield break;
        }

        if (NodeInput.ParseInt(_xText) is null)
        {
            yield return $"[{NodeId}] X: «{_xText}» — не целое число.";
        }
        if (NodeInput.ParseInt(_yText) is null)
        {
            yield return $"[{NodeId}] Y: «{_yText}» — не целое число.";
        }
    }
}

/// <summary>
/// Editor for <see cref="DelayNode"/>. Entered in SECONDS with decimals (1.5 → 1500 ms) —
/// the unit the game states cast times in, and the same convention the pre-graph editor
/// used, so existing muscle memory carries over. No target selector: a pause is global to
/// the run, not per-window.
/// </summary>
public sealed class DelayNodeRowViewModel : ActionNodeRowViewModel
{
    private string _secondsText;

    public DelayNodeRowViewModel(DelayNode node)
        : base(node.Id, target: null, node.Next)
    {
        _secondsText = NodeInput.FormatSeconds(node.Ms);
    }

    public override string TypeLabel => "Пауза";

    /// <summary>Delay in seconds as typed; converted to milliseconds on save.</summary>
    public string SecondsText
    {
        get => _secondsText;
        set => SetField(ref _secondsText, value);
    }

    public override MacroNode ToNode() => new DelayNode
    {
        Id = NodeId,
        Editor = Editor,
        Ms = NodeInput.ParseSecondsToMs(_secondsText) ?? 0,
        Next = Next.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (NodeInput.ParseSecondsToMs(_secondsText) is null)
        {
            yield return $"[{NodeId}] пауза: «{_secondsText}» — не неотрицательное число секунд.";
        }
    }
}

/// <summary>Shared editor body for the two tag nodes — they differ only in direction.</summary>
public abstract class TagNodeRowViewModel : ActionNodeRowViewModel
{
    private string _tag;

    protected TagNodeRowViewModel(string nodeId, TargetSelector? target, string? next, string tag)
        : base(nodeId, TargetSelectorViewModel.FromSelector(target), next)
    {
        _tag = tag;
    }

    /// <summary>Tag text; supports <c>{var}</c> interpolation from run variables.</summary>
    public string Tag
    {
        get => _tag;
        set => SetField(ref _tag, value ?? string.Empty);
    }

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_tag))
        {
            yield return $"[{NodeId}] тег не задан.";
        }
    }
}

/// <summary>Editor for <see cref="AddTagNode"/>.</summary>
public sealed class AddTagNodeRowViewModel : TagNodeRowViewModel
{
    public AddTagNodeRowViewModel(AddTagNode node)
        : base(node.Id, node.Target, node.Next, node.Tag)
    {
    }

    public override string TypeLabel => "Добавить тег";

    public override MacroNode ToNode() => new AddTagNode
    {
        Id = NodeId,
        Editor = Editor,
        Tag = Tag,
        Target = Target?.ToSelector(),
        Next = Next.TargetOrNull,
    };
}

/// <summary>Editor for <see cref="RemoveTagNode"/>.</summary>
public sealed class RemoveTagNodeRowViewModel : TagNodeRowViewModel
{
    public RemoveTagNodeRowViewModel(RemoveTagNode node)
        : base(node.Id, node.Target, node.Next, node.Tag)
    {
    }

    public override string TypeLabel => "Снять тег";

    public override MacroNode ToNode() => new RemoveTagNode
    {
        Id = NodeId,
        Editor = Editor,
        Tag = Tag,
        Target = Target?.ToSelector(),
        Next = Next.TargetOrNull,
    };
}

/// <summary>Editor for <see cref="SetIconNode"/>: a file path, typically with a <c>{tag}</c> placeholder.</summary>
public sealed class SetIconNodeRowViewModel : ActionNodeRowViewModel
{
    private string _iconPath;

    public SetIconNodeRowViewModel(SetIconNode node)
        : base(node.Id, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        _iconPath = node.IconPath;
    }

    public override string TypeLabel => "Сменить иконку";

    /// <summary>Path to the image; supports <c>{var}</c> interpolation.</summary>
    public string IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value ?? string.Empty);
    }

    public override MacroNode ToNode() => new SetIconNode
    {
        Id = NodeId,
        Editor = Editor,
        IconPath = _iconPath,
        Target = Target?.ToSelector(),
        Next = Next.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_iconPath))
        {
            yield return $"[{NodeId}] путь к иконке не задан.";
        }
    }
}

/// <summary>Editor for <see cref="RunMacroNode"/>: a macro from the library plus the await flag.</summary>
public sealed class RunMacroNodeRowViewModel : ActionNodeRowViewModel
{
    private string _macroName;
    private bool _await;
    private ObservableCollection<string> _macroChoices = [];

    public RunMacroNodeRowViewModel(RunMacroNode node)
        : base(node.Id, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        _macroName = node.MacroName;
        _await = node.Await;
    }

    public override string TypeLabel => "Запустить макрос";

    /// <summary>
    /// Name of the sub-macro. The setter ignores null/blank because a <c>ComboBox</c>
    /// pushes null whenever its ItemsSource is rebuilt, which would otherwise wipe a
    /// perfectly good reference every time the macro list refreshes.
    /// </summary>
    public string MacroName
    {
        get => _macroName;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                SetField(ref _macroName, value);
            }
        }
    }

    /// <summary>Wait for the sub-run(s) before following <c>Next</c>.</summary>
    public bool Await { get => _await; set => SetField(ref _await, value); }

    /// <summary>Library names offered by the drop-down; assigned by the editor.</summary>
    public ObservableCollection<string> MacroChoices
    {
        get => _macroChoices;
        set => SetField(ref _macroChoices, value);
    }

    public override MacroNode ToNode() => new RunMacroNode
    {
        Id = NodeId,
        Editor = Editor,
        MacroName = _macroName,
        Await = _await,
        Target = Target?.ToSelector(),
        Next = Next.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_macroName))
        {
            yield return $"[{NodeId}] не выбран макрос.";
        }
    }
}
