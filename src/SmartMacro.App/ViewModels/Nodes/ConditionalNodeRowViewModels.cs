using System.Diagnostics.CodeAnalysis;
using SmartMacro.Macros.Model;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Общая форма трёх условных нод: два ребра-исхода и НИКАКОГО селектора целей — они по замыслу
/// снимают и сопоставляют контекстное окно прогона (план §0.2), и именно поэтому макрос по
/// хоткею обязан добираться до них через веер <c>RunMacroNode</c>.
/// </summary>
public abstract class ConditionalNodeRowViewModel : NodeRowViewModel
{
    protected ConditionalNodeRowViewModel(
        string nodeId,
        string positiveLabel,
        string? positiveTarget,
        string negativeLabel,
        string? negativeTarget)
        : base(nodeId, target: null,
            new NodeEdgeViewModel(positiveLabel, positiveTarget),
            new NodeEdgeViewModel(negativeLabel, negativeTarget))
    {
    }

    /// <summary>Ребро, по которому уходят при удачной проверке (Found / Matched).</summary>
    public NodeEdgeViewModel PositiveEdge => Edges[0];

    /// <summary>Ребро на случай неудачи (NotFound / Timeout / NotMatched).</summary>
    public NodeEdgeViewModel NegativeEdge => Edges[1];

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
            ? "всё окно"
            : $"{rect.Width}×{rect.Height}";
    }
}

/// <summary>Редактор <see cref="FindElementNode"/>: поиск шаблона в один заход.</summary>
public sealed class FindElementNodeRowViewModel : ConditionalNodeRowViewModel
{
    private string _template;
    private string _foundPointVar;

    public FindElementNodeRowViewModel(FindElementNode node)
        : base(node.Id, "Найдено", node.Found, "Не найдено", node.NotFound)
    {
        _template = node.Template;
        _foundPointVar = node.FoundPointVar ?? string.Empty;
        Region = RegionEditorViewModel.FromRect(node.Region);
        TrackRegion(Region);
    }

    public override string TypeLabel => "Найти элемент";

    public override string Summary => Join(_template, DescribeRegion(Region));

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
        Id = NodeId,
        Editor = Editor,
        Template = _template,
        Region = Region.ToOptionalRect(),
        FoundPointVar = string.IsNullOrWhiteSpace(_foundPointVar) ? null : _foundPointVar,
        Found = PositiveEdge.TargetOrNull,
        NotFound = NegativeEdge.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_template))
        {
            yield return $"[{NodeId}] шаблон не задан.";
        }

        foreach (var error in Region.GetInputErrors(NodeId))
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
        : base(node.Id, "Найдено", node.Found, "Таймаут", node.Timeout)
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

    public override string TypeLabel => "Ждать элемент";

    public override string Summary =>
        Join(_template, $"{NodeInput.FormatSeconds(NodeInput.ParseInt(_timeoutMsText) ?? 0)} с");

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
        Id = NodeId,
        Editor = Editor,
        Template = _template,
        Region = Region.ToOptionalRect(),
        TimeoutMs = NodeInput.ParseInt(_timeoutMsText) ?? 0,
        FoundPointVar = string.IsNullOrWhiteSpace(_foundPointVar) ? null : _foundPointVar,
        Found = PositiveEdge.TargetOrNull,
        Timeout = NegativeEdge.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_template))
        {
            yield return $"[{NodeId}] шаблон не задан.";
        }

        if (NodeInput.ParseInt(_timeoutMsText) is null)
        {
            yield return $"[{NodeId}] таймаут: «{_timeoutMsText}» — не целое число миллисекунд.";
        }

        foreach (var error in Region.GetInputErrors(NodeId))
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
        : base(node.Id, "Распознано", node.Matched, "Не распознано", node.NotMatched)
    {
        _templateSet = node.TemplateSet;
        _resultVar = node.ResultVar;
        _applyTag = node.ApplyTag;
        Region = RegionEditorViewModel.FromRect(node.Region);
        TrackRegion(Region);
    }

    public override string TypeLabel => "Распознать тег";

    public override string Summary =>
        Join(_templateSet.Length > 0 ? $"набор {_templateSet}" : null, DescribeRegion(Region));

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
        Id = NodeId,
        Editor = Editor,
        TemplateSet = _templateSet,
        Region = Region.ToRect(),
        ApplyTag = _applyTag,
        ResultVar = _resultVar,
        Matched = PositiveEdge.TargetOrNull,
        NotMatched = NegativeEdge.TargetOrNull,
    };

    public override IEnumerable<string> GetInputErrors()
    {
        if (string.IsNullOrWhiteSpace(_templateSet))
        {
            yield return $"[{NodeId}] набор шаблонов не задан.";
        }

        if (string.IsNullOrWhiteSpace(_resultVar))
        {
            yield return $"[{NodeId}] имя переменной результата не задано.";
        }

        foreach (var error in Region.GetInputErrors(NodeId))
        {
            yield return error;
        }
    }
}
