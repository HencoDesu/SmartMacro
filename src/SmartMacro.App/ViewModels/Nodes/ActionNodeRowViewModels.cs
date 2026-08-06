using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Общая форма семи нод-действий: ровно одно исходящее ребро (<c>Next</c>). Объект ребра
/// создаётся здесь, чтобы наследники (и XAML) могли добраться до него по имени, а не по
/// индексу.
/// </summary>
public abstract class ActionNodeRowViewModel : NodeRowViewModel
{
    protected ActionNodeRowViewModel(MacroNode node, TargetSelectorViewModel? target, Guid? next)
        : base(node, target, new NodeEdgeViewModel(Strings.Node_Edge_Next, next))
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

    public override string TypeLabel => Strings.Node_Type_KeyPress;

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
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_KeyMissing, DisplayName);
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

    public override string TypeLabel => Strings.Node_Type_Click;

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
                yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_PointVarMissing, DisplayName);
            }

            yield break;
        }

        if (NodeInput.ParseInt(_xText) is null)
        {
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_ClickX, DisplayName, _xText);
        }

        if (NodeInput.ParseInt(_yText) is null)
        {
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_ClickY, DisplayName, _yText);
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

    public override string TypeLabel => Strings.Node_Type_Delay;

    public override string Summary =>
        string.Format(CultureInfo.CurrentCulture, Strings.Node_Summary_Seconds, _secondsText);

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
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_Delay, DisplayName, _secondsText);
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
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_TagMissing, DisplayName);
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

    public override string TypeLabel => Strings.Node_Type_AddTag;

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

    public override string TypeLabel => Strings.Node_Type_RemoveTag;

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

    public override string TypeLabel => Strings.Node_Type_SetIcon;

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
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_IconMissing, DisplayName);
        }
    }
}

/// <summary>
/// Редактор <see cref="RunSubmacroNode"/>: под-макрос ЭТОГО бандла плюс флаг ожидания.
///
/// До волны F4 строка держала ИМЯ макроса библиотеки и выпадающий список имён. Теперь адресат —
/// <c>Guid</c> под-макроса, и список у него свой (<see cref="SubmacroChoiceViewModel"/>): выбрать
/// чужой макрос больше нельзя, потому что вызвать его нельзя.
/// </summary>
public sealed class RunSubmacroNodeRowViewModel : ActionNodeRowViewModel
{
    private Guid _submacroId;
    private bool _await;
    private ObservableCollection<SubmacroChoiceViewModel> _submacroChoices = [];

    public RunSubmacroNodeRowViewModel(RunSubmacroNode node)
        : base(node, TargetSelectorViewModel.FromSelector(node.Target), node.Next)
    {
        _submacroId = node.SubmacroId;
        _await = node.Await;
    }

    // ⚠️ Не «Запустить под-макрос», хотя именно так эта нода подписана в меню «+ Нода». Подпись
    // типа печатают ДВОЕ: шапка коробки шириной 210px и заголовок инспектора, у которого справа
    // стоит подсказка «двойной клик — правка на месте». Найдено глазами: длинный вариант въезжал
    // в неё вплотную, без единого пикселя зазора.
    public override string TypeLabel => Strings.Node_Type_RunSubmacro;

    public override string Summary => Join(SubmacroLabel, _await ? Strings.Node_Summary_Await : null);

    /// <summary>
    /// Личность вызываемого под-макроса. Сеттер игнорирует <see cref="Guid.Empty"/>, потому что
    /// <c>ComboBox</c> проталкивает null каждый раз, когда пересобирается его ItemsSource, —
    /// иначе вполне живая ссылка стиралась бы при каждом обновлении списка функций.
    /// </summary>
    public Guid SubmacroId
    {
        get => _submacroId;
        private set
        {
            if (SetField(ref _submacroId, value))
            {
                OnPropertyChanged(nameof(Submacro));
            }
        }
    }

    /// <summary>
    /// Выбранный элемент списка — то, к чему привязан <c>ComboBox</c>. Отдельно от
    /// <see cref="SubmacroId"/> по той же причине, по которой у ребра есть и <c>TargetId</c>, и
    /// разрешённый элемент: список хранит ССЫЛКУ, а модель — id.
    /// </summary>
    public SubmacroChoiceViewModel? Submacro
    {
        get => _submacroChoices.FirstOrDefault(choice => choice.Id == _submacroId);
        set
        {
            if (value is not null)
            {
                SubmacroId = value.Id;
            }
        }
    }

    /// <summary>Дождаться вложенных прогонов, прежде чем уходить по <c>Next</c>.</summary>
    public bool Await
    {
        get => _await;
        set => SetField(ref _await, value);
    }

    /// <summary>Под-макросы бандла, которые предлагает выпадающий список; проставляет их редактор.</summary>
    public ObservableCollection<SubmacroChoiceViewModel> SubmacroChoices
    {
        get => _submacroChoices;
        set
        {
            if (SetField(ref _submacroChoices, value))
            {
                OnPropertyChanged(nameof(Submacro));
            }
        }
    }

    /// <summary>Подпись выбранного под-макроса для коробки на канве; пусто, когда не выбран.</summary>
    private string SubmacroLabel =>
        _submacroChoices.FirstOrDefault(choice => choice.Id == _submacroId)?.Display ?? string.Empty;

    public override MacroNode ToNode() => new RunSubmacroNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        SubmacroId = _submacroId,
        Await = _await,
        Target = Target?.ToSelector(),
        Next = Next.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (_submacroId == Guid.Empty)
        {
            yield return string.Format(CultureInfo.CurrentCulture, Strings.Node_Error_SubmacroMissing, DisplayName);
        }
    }
}
