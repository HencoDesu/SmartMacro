using SmartMacro.Macros.Model;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Common shape of the three conditional nodes: two outcome edges and NO target selector —
/// they capture and match against the run's context window by design (plan §0.2), which is
/// why a hotkey macro has to reach them through a <c>RunMacroNode</c> fan-out.
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

    /// <summary>Edge taken when the check succeeds (Found / Matched).</summary>
    public NodeEdgeViewModel PositiveEdge => Edges[0];

    /// <summary>Edge taken when it does not (NotFound / Timeout / NotMatched).</summary>
    public NodeEdgeViewModel NegativeEdge => Edges[1];

    /// <summary>
    /// Wires a region editor's changes into <see cref="NodeRowViewModel.Summary"/>. The
    /// region lives in its own view-model, so its four fields do not travel up the row's
    /// own change notifications and the box's "160×35" would otherwise never update.
    /// </summary>
    protected void TrackRegion(RegionEditorViewModel region)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    /// <summary>"160×35", or "всё окно" for a degenerate rectangle.</summary>
    protected static string DescribeRegion(RegionEditorViewModel region)
    {
        var rect = region.ToRect();
        return rect.Width <= 0 || rect.Height <= 0
            ? "всё окно"
            : $"{rect.Width}×{rect.Height}";
    }
}

/// <summary>Editor for <see cref="FindElementNode"/>: one-shot template search.</summary>
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

    /// <summary>Template file stem, resolved by the primitives layer.</summary>
    public string Template
    {
        get => _template;
        set => SetField(ref _template, value ?? string.Empty);
    }

    /// <summary>Client-space crop; a zero width/height means "search the whole window".</summary>
    public RegionEditorViewModel Region { get; }

    /// <summary>Optional run variable the match centre is written to. Blank = don't record it.</summary>
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

/// <summary>Editor for <see cref="WaitForElementNode"/>: poll until the template appears or the budget runs out.</summary>
public sealed class WaitForElementNodeRowViewModel : ConditionalNodeRowViewModel
{
    private string _template;
    private string _timeoutMsText;
    private string _foundPointVar;

    public WaitForElementNodeRowViewModel(WaitForElementNode node)
        : base(node.Id, "Найдено", node.Found, "Таймаут", node.Timeout)
    {
        _template = node.Template;
        // Milliseconds, not seconds: a wait budget is a technical timeout (PW loading can
        // take ~60000) rather than a game-facing cast time, and the plan spells it as ms.
        _timeoutMsText = NodeInput.FormatInt(node.TimeoutMs);
        _foundPointVar = node.FoundPointVar ?? string.Empty;
        Region = RegionEditorViewModel.FromRect(node.Region);
        TrackRegion(Region);
    }

    public override string TypeLabel => "Ждать элемент";

    public override string Summary =>
        Join(_template, $"{NodeInput.FormatSeconds(NodeInput.ParseInt(_timeoutMsText) ?? 0)} с");

    public string Template
    {
        get => _template;
        set => SetField(ref _template, value ?? string.Empty);
    }

    public RegionEditorViewModel Region { get; }

    /// <summary>Overall wait budget in milliseconds.</summary>
    public string TimeoutMsText
    {
        get => _timeoutMsText;
        set => SetField(ref _timeoutMsText, value);
    }

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
/// Editor for <see cref="RecognizeTagNode"/>: best match of a template SET over a region.
/// The region is mandatory here (unlike Find/Wait) — matching a whole set against a whole
/// window is both slow and ambiguous.
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

    /// <summary>Template set name; each file's stem in the set is a candidate tag.</summary>
    public string TemplateSet
    {
        get => _templateSet;
        set => SetField(ref _templateSet, value ?? string.Empty);
    }

    /// <summary>Mandatory client-space crop the set is matched against.</summary>
    public RegionEditorViewModel Region { get; }

    /// <summary>Tag the context window with the winning template's name.</summary>
    public bool ApplyTag { get => _applyTag; set => SetField(ref _applyTag, value); }

    /// <summary>Run variable the winning name is written to (model default: <c>"tag"</c>).</summary>
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
