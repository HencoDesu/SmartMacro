using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Общая форма семи нод-действий: ровно одно исходящее ребро (<c>Next</c>). Объект ребра
/// создаётся здесь, чтобы наследники (и XAML) могли добраться до него по имени, а не по
/// индексу.
/// </summary>
public abstract class ActionNodeRowViewModel : NodeRowViewModel
{
    protected ActionNodeRowViewModel(MacroNode node, TargetSelectorViewModel? target, Guid? next)
        : base(node, target, new NodeEdgeViewModel("Далее", next))
    {
    }

    /// <summary>Единственное исходящее ребро.</summary>
    public NodeEdgeViewModel Next => Edges[0];
}

/// <summary>Редактор <see cref="KeyPressNode"/>: одна клавиша, выбранная ловушкой в игровом духе.</summary>
public sealed class KeyPressNodeRowViewModel : ActionNodeRowViewModel
{
    private string _keyName;

    public KeyPressNodeRowViewModel(KeyPressNode node)
        : base(node, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        _keyName = node.Key.ToString();
    }

    public override string TypeLabel => "Нажать клавишу";

    public override string Summary => _keyName;

    /// <summary>Коробка рисует клавишу кейкапом, а не моноширинной строкой.</summary>
    public override string? Keycap => _keyName.Length > 0 ? _keyName : null;

    /// <summary>
    /// Имя члена <see cref="VirtualKey"/> — та строковая форма, к которой привязывается
    /// <c>Controls.KeyBindingPicker</c> (он ловит имена клавиш Avalonia, а перечисление их
    /// намеренно повторяет).
    /// </summary>
    [AllowNull]
    public string KeyName
    {
        get => _keyName;
        set => SetField(ref _keyName, value ?? string.Empty);
    }

    public override MacroNode ToNode() => new KeyPressNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        Key = ParseKey(_keyName),
        Target = Target?.ToSelector(),
        Next = Next.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (!Enum.TryParse<VirtualKey>(_keyName, out var key) || key == 0)
        {
            yield return $"[{DisplayName}] клавиша не задана.";
        }
    }

    private static VirtualKey ParseKey(string name) =>
        Enum.TryParse<VirtualKey>(name, out var key) ? key : default;
}

/// <summary>
/// Редактор <see cref="ClickNode"/>. Модель требует РОВНО одного из Point / PointVar, поэтому
/// оба поданы переключателем, а не двумя независимо заполняемыми полями, — отсюда невозможно
/// оставить заполненными оба или пустыми оба.
/// </summary>
public sealed class ClickNodeRowViewModel : ActionNodeRowViewModel
{
    private string _xText;
    private string _yText;
    private string _pointVar;
    private bool _useVariable;
    private bool _doubleClick;

    public ClickNodeRowViewModel(ClickNode node)
        : base(node, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        var point = node.Point ?? default;
        _xText = NodeInput.FormatInt(point.X);
        _yText = NodeInput.FormatInt(point.Y);
        _useVariable = node.PointVar is not null;
        _pointVar = node.PointVar ?? "cursor";
        _doubleClick = node.DoubleClick;
    }

    public override string TypeLabel => "Клик";

    public override string Summary =>
        (_useVariable ? $"{{{_pointVar}}}" : $"{_xText}, {_yText}") + (_doubleClick ? " ×2" : string.Empty);

    /// <summary><c>true</c> = брать точку из переменной прогона, а не из буквальных X/Y.</summary>
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

    /// <summary>Обратное к <see cref="UseVariable"/> — для видимости блока X/Y.</summary>
    public bool UseLiteralPoint => !_useVariable;

    public string XText
    {
        get => _xText;
        set => SetField(ref _xText, value);
    }

    public string YText
    {
        get => _yText;
        set => SetField(ref _yText, value);
    }

    /// <summary>Переменная прогона с точкой клика — <c>"cursor"</c> триггер засевает всегда.</summary>
    [AllowNull]
    public string PointVar
    {
        get => _pointVar;
        set => SetField(ref _pointVar, value ?? string.Empty);
    }

    public bool DoubleClick
    {
        get => _doubleClick;
        set => SetField(ref _doubleClick, value);
    }

    public override MacroNode ToNode() => new ClickNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        Point = _useVariable ? null : new ScreenPoint(NodeInput.ParseInt(_xText) ?? 0, NodeInput.ParseInt(_yText) ?? 0),
        PointVar = _useVariable ? _pointVar : null,
        DoubleClick = _doubleClick,
        Target = Target?.ToSelector(),
        Next = Next.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (_useVariable)
        {
            if (string.IsNullOrWhiteSpace(_pointVar))
            {
                yield return $"[{DisplayName}] имя переменной не задано.";
            }

            yield break;
        }

        if (NodeInput.ParseInt(_xText) is null)
        {
            yield return $"[{DisplayName}] X: «{_xText}» — не целое число.";
        }

        if (NodeInput.ParseInt(_yText) is null)
        {
            yield return $"[{DisplayName}] Y: «{_yText}» — не целое число.";
        }
    }
}

/// <summary>
/// Редактор <see cref="DelayNode"/>. Вводится в СЕКУНДАХ с дробной частью (1.5 → 1500 мс) —
/// в этих единицах игра называет время каста, и того же соглашения держался редактор до
/// графов, так что наработанная моторика переносится. Селектора целей нет: пауза общая на
/// прогон, а не на каждое окно.
/// </summary>
public sealed class DelayNodeRowViewModel : ActionNodeRowViewModel
{
    private string _secondsText;

    public DelayNodeRowViewModel(DelayNode node)
        : base(node, target: null, node.Next)
    {
        _secondsText = NodeInput.FormatSeconds(node.Ms);
    }

    public override string TypeLabel => "Пауза";

    public override string Summary => $"{_secondsText} с";

    /// <summary>Задержка в секундах, как её набрали; при сохранении переводится в миллисекунды.</summary>
    public string SecondsText
    {
        get => _secondsText;
        set => SetField(ref _secondsText, value);
    }

    public override MacroNode ToNode() => new DelayNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        Ms = NodeInput.ParseSecondsToMs(_secondsText) ?? 0,
        Next = Next.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (NodeInput.ParseSecondsToMs(_secondsText) is null)
        {
            yield return $"[{DisplayName}] пауза: «{_secondsText}» — не неотрицательное число секунд.";
        }
    }
}

/// <summary>Общее тело редактора для двух нод тегов — различаются они только направлением.</summary>
public abstract class TagNodeRowViewModel : ActionNodeRowViewModel
{
    private string _tag;

    protected TagNodeRowViewModel(MacroNode node, TargetSelector? target, Guid? next, string tag)
        : base(node, TargetSelectorViewModel.FromSelector(target), next)
    {
        _tag = tag;
    }

    /// <summary>Текст тега; поддерживает подстановку <c>{var}</c> из переменных прогона.</summary>
    [AllowNull]
    public string Tag
    {
        get => _tag;
        set => SetField(ref _tag, value ?? string.Empty);
    }

    public override string Summary => _tag;

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_tag))
        {
            yield return $"[{DisplayName}] тег не задан.";
        }
    }
}

/// <summary>Editor for <see cref="AddTagNode"/>.</summary>
public sealed class AddTagNodeRowViewModel : TagNodeRowViewModel
{
    public AddTagNodeRowViewModel(AddTagNode node)
        : base(node, node.Target, node.Next, node.Tag)
    {
    }

    public override string TypeLabel => "Добавить тег";

    public override MacroNode ToNode() => new AddTagNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        Tag = Tag,
        Target = Target?.ToSelector(),
        Next = Next.TargetId,
    };
}

/// <summary>Editor for <see cref="RemoveTagNode"/>.</summary>
public sealed class RemoveTagNodeRowViewModel : TagNodeRowViewModel
{
    public RemoveTagNodeRowViewModel(RemoveTagNode node)
        : base(node, node.Target, node.Next, node.Tag)
    {
    }

    public override string TypeLabel => "Снять тег";

    public override MacroNode ToNode() => new RemoveTagNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        Tag = Tag,
        Target = Target?.ToSelector(),
        Next = Next.TargetId,
    };
}

/// <summary>Редактор <see cref="SetIconNode"/>: путь к файлу, обычно с подстановкой <c>{tag}</c>.</summary>
public sealed class SetIconNodeRowViewModel : ActionNodeRowViewModel
{
    private string _iconPath;

    public SetIconNodeRowViewModel(SetIconNode node)
        : base(node, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        _iconPath = node.IconPath;
    }

    public override string TypeLabel => "Сменить иконку";

    public override string Summary => _iconPath;

    /// <summary>Путь к картинке; поддерживает подстановку <c>{var}</c>.</summary>
    [AllowNull]
    public string IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value ?? string.Empty);
    }

    public override MacroNode ToNode() => new SetIconNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        IconPath = _iconPath,
        Target = Target?.ToSelector(),
        Next = Next.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_iconPath))
        {
            yield return $"[{DisplayName}] путь к иконке не задан.";
        }
    }
}

/// <summary>Редактор <see cref="RunMacroNode"/>: макрос из библиотеки плюс флаг ожидания.</summary>
public sealed class RunMacroNodeRowViewModel : ActionNodeRowViewModel
{
    private string _macroName;
    private bool _await;
    private ObservableCollection<string> _macroChoices = [];

    public RunMacroNodeRowViewModel(RunMacroNode node)
        : base(node, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        _macroName = node.MacroName;
        _await = node.Await;
    }

    public override string TypeLabel => "Запустить макрос";

    public override string Summary => Join(_macroName, _await ? "ждать" : null);

    /// <summary>
    /// Имя вложенного макроса. Сеттер игнорирует null и пустую строку, потому что
    /// <c>ComboBox</c> проталкивает null каждый раз, когда пересобирается его ItemsSource, —
    /// иначе вполне живая ссылка стиралась бы при каждом обновлении списка макросов.
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

    /// <summary>Дождаться вложенных прогонов, прежде чем уходить по <c>Next</c>.</summary>
    public bool Await
    {
        get => _await;
        set => SetField(ref _await, value);
    }

    /// <summary>Имена из библиотеки, которые предлагает выпадающий список; проставляет их редактор.</summary>
    public ObservableCollection<string> MacroChoices
    {
        get => _macroChoices;
        set => SetField(ref _macroChoices, value);
    }

    public override MacroNode ToNode() => new RunMacroNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        MacroName = _macroName,
        Await = _await,
        Target = Target?.ToSelector(),
        Next = Next.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_macroName))
        {
            yield return $"[{DisplayName}] не выбран макрос.";
        }
    }
}
