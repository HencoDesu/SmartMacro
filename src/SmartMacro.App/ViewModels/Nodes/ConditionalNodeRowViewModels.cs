using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using SmartMacro.Macros.Model;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Общая форма трёх условных нод: два ребра-исхода и НИКАКОГО селектора целей — они по замыслу
/// снимают и сопоставляют контекстное окно прогона (план §0.2), и именно поэтому макрос по
/// хоткею обязан добираться до них через веер селектора: у прогона по хоткею контекстного окна
/// нет, и условная нода, до которой добрались не через веер, снимать ей нечего.
/// </summary>
public abstract class ConditionalNodeRowViewModel : NodeRowViewModel
{
    private string _matchThresholdText;

    protected ConditionalNodeRowViewModel(
        MacroNode node,
        double? matchThreshold,
        string positiveLabel,
        Guid? positiveTarget,
        string negativeLabel,
        Guid? negativeTarget)
        : base(node, target: null,
            new NodeEdgeViewModel(positiveLabel, positiveTarget),
            new NodeEdgeViewModel(negativeLabel, negativeTarget))
    {
        _matchThresholdText = NodeInput.FormatThreshold(matchThreshold);
    }

    /// <summary>Ребро, по которому уходят при удачной проверке (Found / Matched).</summary>
    public NodeEdgeViewModel PositiveEdge => Edges[0];

    /// <summary>Ребро на случай неудачи (NotFound / Timeout / NotMatched).</summary>
    public NodeEdgeViewModel NegativeEdge => Edges[1];

    /// <summary>
    /// Порог совпадения ЭТОЙ ноды, как его набрали. Пустая строка — законное «взять умолчание
    /// слоя зрения», и именно она в поле по умолчанию.
    ///
    /// <b>Подсказка под полем не называет число.</b> Умолчание живёт в настройках ДЕМОНА, а
    /// панель их не читает; напечатать здесь «0.7» значило бы напечатать значение из коробки, а
    /// не то, с которым нода на самом деле побежит, — та же ложь, что и бейдж целей, спорящий с
    /// исполнителем. Поэтому приглушённое поле говорит «из настроек», а число видно там, где оно
    /// и задаётся.
    /// </summary>
    public string MatchThresholdText
    {
        get => _matchThresholdText;
        set => SetField(ref _matchThresholdText, value);
    }

    /// <summary>Модельная форма порога: <c>null</c>, пока поле пусто.</summary>
    protected double? MatchThresholdOrNull =>
        NodeInput.TryParseThreshold(_matchThresholdText, out var value) ? value : null;

    /// <summary>Кусочек сводки коробки: «порог 0.85», когда он задан, иначе ничего.</summary>
    protected string? DescribeThreshold =>
        MatchThresholdOrNull is { } value
            ? string.Format(CultureInfo.CurrentCulture, Strings_App.Node_Summary_Threshold,
                NodeInput.FormatThreshold(value))
            : null;

    /// <summary>Претензии к полю порога. Диапазон проверяет валидатор графа — здесь только разбор.</summary>
    protected IEnumerable<string> GetThresholdErrors()
    {
        if (!NodeInput.TryParseThreshold(_matchThresholdText, out _))
        {
            yield return string.Format(
                CultureInfo.CurrentCulture, Strings_App.Node_Error_Threshold,
                DisplayName, _matchThresholdText);
        }
    }

    /// <summary>
    /// Заводит изменения редактора области в <see cref="NodeRowViewModel.Summary"/>. Область
    /// живёт в отдельной view-model, поэтому её четыре поля не поднимаются наверх с
    /// уведомлениями самой строки — и «160×35» на коробке иначе не обновлялось бы никогда.
    /// </summary>
    protected void TrackRegion(RegionEditorViewModel region)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    /// <summary>«160×35» либо «всё окно» для вырожденного прямоугольника.</summary>
    protected static string DescribeRegion(RegionEditorViewModel region)
    {
        var rect = region.ToRect();
        return rect.Width <= 0 || rect.Height <= 0
            ? Strings_App.Node_Summary_WholeWindow
            : $"{rect.Width}×{rect.Height}";
    }
}

/// <summary>Редактор <see cref="FindElementNode"/>: поиск шаблона в один заход.</summary>
public sealed class FindElementNodeRowViewModel : ConditionalNodeRowViewModel
{
    private string _template;
    private string _foundPointVar;

    public FindElementNodeRowViewModel(FindElementNode node)
        : base(node, node.MatchThreshold, Strings_App.Node_Edge_Found, node.Found, Strings_App.Node_Edge_NotFound, node.NotFound)
    {
        _template = node.Template;
        _foundPointVar = node.FoundPointVar ?? string.Empty;
        Region = RegionEditorViewModel.FromRect(node.Region);
        TrackRegion(Region);
    }

    public override string TypeLabel => Strings_App.Node_Type_FindElement;

    public override string Summary => Join(_template, DescribeRegion(Region), DescribeThreshold);

    /// <summary>Основа имени файла шаблона; разрешает её слой примитивов.</summary>
    [AllowNull]
    public string Template
    {
        get => _template;
        set => SetField(ref _template, value ?? string.Empty);
    }

    /// <summary>Вырезка в клиентских координатах; нулевая ширина или высота значит «искать по всему окну».</summary>
    public RegionEditorViewModel Region { get; }

    /// <summary>Необязательная переменная прогона, куда пишется центр совпадения. Пусто — не записывать.</summary>
    [AllowNull]
    public string FoundPointVar
    {
        get => _foundPointVar;
        set => SetField(ref _foundPointVar, value ?? string.Empty);
    }

    public override MacroNode ToNode() => new FindElementNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        Template = _template,
        Region = Region.ToOptionalRect(),
        MatchThreshold = MatchThresholdOrNull,
        FoundPointVar = string.IsNullOrWhiteSpace(_foundPointVar) ? null : _foundPointVar,
        Found = PositiveEdge.TargetId,
        NotFound = NegativeEdge.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_template))
        {
            yield return string.Format(
                CultureInfo.CurrentCulture, Strings_App.Node_Error_TemplateMissing, DisplayName);
        }

        foreach (var error in Region.GetInputErrors(DisplayName))
        {
            yield return error;
        }

        foreach (var error in GetThresholdErrors())
        {
            yield return error;
        }
    }
}

/// <summary>Редактор <see cref="WaitForElementNode"/>: опрос, пока шаблон не появится или не кончится отведённое время.</summary>
public sealed class WaitForElementNodeRowViewModel : ConditionalNodeRowViewModel
{
    private string _template;
    private string _timeoutMsText;
    private string _foundPointVar;

    public WaitForElementNodeRowViewModel(WaitForElementNode node)
        : base(node, node.MatchThreshold, Strings_App.Node_Edge_Found, node.Found, Strings_App.Node_Edge_Timeout, node.Timeout)
    {
        _template = node.Template;
        // Миллисекунды, а не секунды: отведённое на ожидание время — это технический таймаут
        // (загрузка PW способна занять ~60000), а не игровое время каста, и в плане оно
        // записано именно в мс.
        _timeoutMsText = NodeInput.FormatInt(node.TimeoutMs);
        _foundPointVar = node.FoundPointVar ?? string.Empty;
        Region = RegionEditorViewModel.FromRect(node.Region);
        TrackRegion(Region);
    }

    public override string TypeLabel => Strings_App.Node_Type_WaitForElement;

    public override string Summary =>
        Join(_template, string.Format(
            CultureInfo.CurrentCulture,
            Strings_App.Node_Summary_Seconds,
            NodeInput.FormatSeconds(NodeInput.ParseInt(_timeoutMsText) ?? 0)), DescribeThreshold);

    [AllowNull]
    public string Template
    {
        get => _template;
        set => SetField(ref _template, value ?? string.Empty);
    }

    public RegionEditorViewModel Region { get; }

    /// <summary>Сколько всего миллисекунд отведено на ожидание.</summary>
    public string TimeoutMsText
    {
        get => _timeoutMsText;
        set => SetField(ref _timeoutMsText, value);
    }

    [AllowNull]
    public string FoundPointVar
    {
        get => _foundPointVar;
        set => SetField(ref _foundPointVar, value ?? string.Empty);
    }

    public override MacroNode ToNode() => new WaitForElementNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        Template = _template,
        Region = Region.ToOptionalRect(),
        TimeoutMs = NodeInput.ParseInt(_timeoutMsText) ?? 0,
        MatchThreshold = MatchThresholdOrNull,
        FoundPointVar = string.IsNullOrWhiteSpace(_foundPointVar) ? null : _foundPointVar,
        Found = PositiveEdge.TargetId,
        Timeout = NegativeEdge.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_template))
        {
            yield return string.Format(
                CultureInfo.CurrentCulture, Strings_App.Node_Error_TemplateMissing, DisplayName);
        }

        if (NodeInput.ParseInt(_timeoutMsText) is null)
        {
            yield return string.Format(
                CultureInfo.CurrentCulture, Strings_App.Node_Error_Timeout,
                DisplayName, _timeoutMsText);
        }

        foreach (var error in Region.GetInputErrors(DisplayName))
        {
            yield return error;
        }

        foreach (var error in GetThresholdErrors())
        {
            yield return error;
        }
    }
}

/// <summary>
/// Редактор <see cref="RecognizeTagNode"/>: лучшее совпадение НАБОРА шаблонов по области.
/// Область здесь обязательна (в отличие от Find/Wait) — сопоставлять целый набор с целым окном
/// и медленно, и неоднозначно.
/// </summary>
public sealed class RecognizeTagNodeRowViewModel : ConditionalNodeRowViewModel
{
    private string _templateSet;
    private string _resultVar;
    private bool _applyTag;

    public RecognizeTagNodeRowViewModel(RecognizeTagNode node)
        : base(node, node.MatchThreshold, Strings_App.Node_Edge_Matched, node.Matched, Strings_App.Node_Edge_NotMatched, node.NotMatched)
    {
        _templateSet = node.TemplateSet;
        _resultVar = node.ResultVar;
        _applyTag = node.ApplyTag;
        Region = RegionEditorViewModel.FromRect(node.Region);
        TrackRegion(Region);
    }

    public override string TypeLabel => Strings_App.Node_Type_RecognizeTag;

    public override string Summary =>
        Join(_templateSet.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings_App.Node_Summary_TemplateSet, _templateSet)
                : null, DescribeRegion(Region), DescribeThreshold);

    /// <summary>Имя набора шаблонов; основа имени каждого файла в наборе — кандидат в теги.</summary>
    [AllowNull]
    public string TemplateSet
    {
        get => _templateSet;
        set => SetField(ref _templateSet, value ?? string.Empty);
    }

    /// <summary>Обязательная вырезка в клиентских координатах, по которой сопоставляется набор.</summary>
    public RegionEditorViewModel Region { get; }

    /// <summary>Повесить на контекстное окно тег с именем победившего шаблона.</summary>
    public bool ApplyTag
    {
        get => _applyTag;
        set => SetField(ref _applyTag, value);
    }

    /// <summary>Переменная прогона, куда пишется победившее имя (умолчание модели — <c>"tag"</c>).</summary>
    [AllowNull]
    public string ResultVar
    {
        get => _resultVar;
        set => SetField(ref _resultVar, value ?? string.Empty);
    }

    public override MacroNode ToNode() => new RecognizeTagNode
    {
        Id = Id,
        DisplayName = DisplayName,
        Editor = Editor,
        TemplateSet = _templateSet,
        Region = Region.ToRect(),
        MatchThreshold = MatchThresholdOrNull,
        ApplyTag = _applyTag,
        ResultVar = _resultVar,
        Matched = PositiveEdge.TargetId,
        NotMatched = NegativeEdge.TargetId,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_templateSet))
        {
            yield return string.Format(
                CultureInfo.CurrentCulture, Strings_App.Node_Error_TemplateSetMissing, DisplayName);
        }

        if (string.IsNullOrWhiteSpace(_resultVar))
        {
            yield return string.Format(
                CultureInfo.CurrentCulture, Strings_App.Node_Error_ResultVarMissing, DisplayName);
        }

        foreach (var error in Region.GetInputErrors(DisplayName))
        {
            yield return error;
        }

        foreach (var error in GetThresholdErrors())
        {
            yield return error;
        }
    }
}
