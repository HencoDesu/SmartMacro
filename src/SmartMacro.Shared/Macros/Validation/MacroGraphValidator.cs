using System.Globalization;
using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Resources;

namespace SmartMacro.Macros.Validation;

/// <summary>
/// Статические проверки, которые гоняются при сохранении и загрузке, — ловят сломанные графы
/// раньше, чем исполнителю придётся прерываться в рантайме.
///
/// Ошибки: несуществующий или пустой StartNodeId; дубликаты id нод; рёбра в несуществующие
/// ноды; ClickNode, у которого заданы оба или ни одного из Point/PointVar; порог совпадения вне
/// диапазона (0; 1]; и правило контекста — макрос, способный ЗАПУСТИТЬСЯ САМ без контекстного
/// окна (то есть у него есть триггеры, но нет <see cref="ProcessAppearedTrigger"/>), не имеет
/// права содержать ДОСТИЖИМУЮ условную ноду или ноду действия без селектора (упрощённое правило
/// по §0.2 плана; подпрогоны RunMacro с Target, которые контекст всё же дали бы, намеренно не
/// моделируются).
///
/// Макрос СОВСЕМ без триггеров от правила контекста освобождён: он библиотечный, запустить его
/// можно только вручную из интерфейса против конкретного окна, а значит контекст всегда приходит
/// от вызывающего. Ноды без селектора для такого макроса — как раз ПРАВИЛЬНАЯ форма: именно она
/// и делает его переиспользуемым для каждого окна. Под-макросы (у которых триггеров не бывает по
/// определению) от того же правила освобождены по той же причине.
///
/// Предупреждения: недостижимые ноды; циклы, внутри которых нет ни <see cref="DelayNode"/>,
/// ни <see cref="WaitForElementNode"/> (крутятся вхолостую — ищутся по сильно связным
/// компонентам достижимого подграфа); повторяющиеся подписи нод.
///
/// <b>Дубликат подписи — предупреждение, а дубликат id — ошибка.</b> Подпись ни на что не
/// влияет, кроме читаемости: граф с двумя нодами «Клик» исполняется совершенно однозначно.
/// Но строка лога «0:01.2 · Клик · ок», встретившаяся дважды, читателю уже ни о чём не говорит —
/// на канве неоднозначность снимет подсветка, а в тексте снять её нечем. Поэтому сказать надо, а
/// запрещать — нет.
///
/// <b>Шаблоны (волна F2).</b> С переездом шаблонов внутрь бандла набор шаблонов макроса известен
/// статически — ровно так же точно, как имена в нодах, — поэтому «нода называет шаблон, которого
/// нет» стало проверкой ЗДЕСЬ. До этого о том же узнавали двумя худшими способами: из раздела
/// «НЕТ ФАЙЛА» в браузере шаблонов (куда надо было пойти) и строчкой в журнале демона посреди
/// прогона (когда уже поздно). Проверка требует ОПИСИ и без неё пропускается целиком — см.
/// перегрузку <see cref="Validate(MacroGraph, MacroTemplateInventory?)"/>.
/// </summary>
public static class MacroGraphValidator
{
    /// <summary>
    /// Проверяет граф без сверки шаблонов. Пустой список = всё чисто.
    ///
    /// Так зовут те, у кого описи бандла на руках нет: редактор, пересчитывающий предупреждения
    /// по несохранённому черновику, и тесты модели.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Validate(MacroGraph macro) => Validate(macro, templates: null);

    /// <summary>
    /// Проверяет БАНДЛ ЦЕЛИКОМ: граф верхнего уровня, каждый его под-макрос и то, что лежит между
    /// ними (волна F4).
    ///
    /// <b>Точка входа для обеих сторон.</b> Демон судит бандл при загрузке и по этому вердикту
    /// решает, вооружать ли триггеры; панель судит его при показе строки библиотеки и при
    /// сохранении. Разъехаться им негде — код один; ради этого же <see cref="MacroBundleEntry"/>
    /// зовёт этот метод сам, а не повторяет его аргументы у каждого вызывающего.
    ///
    /// Проверки, которых нет у одиночного графа, и все они про правила из
    /// <see cref="MacroSubmacro"/>:
    /// <list type="bullet">
    ///   <item><b>Адресат существует.</b> <see cref="RunSubmacroNode"/>, чей <c>SubmacroId</c> не
    ///     разрешается, — ОШИБКА: прогон на этой ноде оборвётся, и лучше сказать заранее. Тем же
    ///     сообщением ловится и невыбранный адресат.</item>
    ///   <item><b>Триггер внутри под-макроса</b> — ошибка: под-макрос это функция, и хоткей на ней
    ///     превратил бы её в макрос верхнего уровня, которого <c>HotkeyListener</c> всё равно не
    ///     увидит (он смотрит только на верхний уровень). Молчаливо не работающий хоткей — тот
    ///     самый дефект, который закрывали в D4.</item>
    ///   <item><b>Вложенный вызов</b> — ошибка: под-макросы плоские, и это то, чем невозможность
    ///     циклов держится по построению.</item>
    ///   <item><b>Переменная не возвращается наружу</b> — предупреждение, см.
    ///     <see cref="AddEscapedVariableWarnings"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="macro">Граф верхнего уровня.</param>
    /// <param name="submacros">Под-макросы бандла; пусто — их нет.</param>
    /// <param name="templates">Опись шаблонов бандла или <c>null</c>; см. <see cref="Validate(MacroGraph, MacroTemplateInventory?)"/>.</param>
    /// <param name="submacroFaults">
    /// Вердикты читателя о записях <c>submacro/</c>, которые не разобрались. Каждый становится
    /// ошибкой уровня бандла: исполнять такой макрос нечем, а строка библиотеки обязана сказать,
    /// что именно в нём сломано.
    /// </param>
    public static IReadOnlyList<ValidationIssue> ValidateBundle(
        MacroGraph macro,
        IReadOnlyList<MacroSubmacro>? submacros,
        MacroTemplateInventory? templates = null,
        IReadOnlyList<string>? submacroFaults = null)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var all = submacros ?? [];

        var issues = new List<ValidationIssue>(Validate(macro, templates));

        foreach (var fault in submacroFaults ?? [])
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, null, null, fault));
        }

        // Адресаты вызовов. Пустой id — это «под-макрос не выбран», и говорить о нём надо иначе:
        // «ссылается на несуществующий» про пустое поле читалось бы как поломка файла, а это
        // недоделанная нода.
        var known = all.Select(submacro => submacro.Id).ToHashSet();
        foreach (var node in macro.Nodes.OfType<RunSubmacroNode>())
        {
            if (node.SubmacroId == Guid.Empty)
            {
                issues.Add(Error(node, Strings_Engine.Validation_Submacro_NotSelected));
            }
            else if (!known.Contains(node.SubmacroId))
            {
                issues.Add(Error(node, Strings_Engine.Validation_Submacro_NotFound));
            }
        }

        foreach (var submacro in all)
        {
            // Тот же самый проход, что и по родителю: под-макрос — обычный граф, и «ребро в
            // никуда» ломает его ровно так же.
            foreach (var issue in Validate(submacro.Graph, templates))
            {
                issues.Add(issue with { SubmacroId = submacro.Id });
            }

            if (submacro.Graph.Triggers.Count > 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    null,
                    null,
                    string.Format(CultureInfo.CurrentCulture, Strings_Engine.Validation_Submacro_HasTrigger, submacro.Name),
                    submacro.Id));
            }

            foreach (var node in submacro.Graph.Nodes.OfType<RunSubmacroNode>())
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    node.Id,
                    MacroNodeNames.Display(node),
                    Strings_Engine.Validation_Submacro_NestedCall,
                    submacro.Id));
            }
        }

        AddEscapedVariableWarnings(macro, all, issues);
        return issues;
    }

    /// <summary>
    /// Проверяет граф вместе с описью шаблонов его бандла.
    /// </summary>
    /// <param name="macro">Граф.</param>
    /// <param name="templates">
    /// Опись шаблонов бандла или <c>null</c>.
    ///
    /// <b><c>null</c> и пустая опись — РАЗНЫЕ вещи, и путать их нельзя.</b> <c>null</c> значит
    /// «состав бандла неизвестен» — тогда сверка не делается вовсе, потому что обвинить ноду в
    /// ссылке на несуществующий файл, не посмотрев в файл, значит соврать. Пустая опись значит
    /// «в бандле шаблонов нет», и это законный повод сказать про каждую ссылку.
    /// </param>
    public static IReadOnlyList<ValidationIssue> Validate(MacroGraph macro, MacroTemplateInventory? templates)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var issues = new List<ValidationIssue>();

        // Дубликаты id. byId оставляет ПЕРВОЕ вхождение — дальнейшие правила работают по этой
        // карте.
        var byId = new Dictionary<Guid, MacroNode>();
        foreach (var node in macro.Nodes)
        {
            if (!byId.TryAdd(node.Id, node))
            {
                issues.Add(Error(node, Strings_Engine.Validation_Node_DuplicateId));
            }
        }

        // Дубликаты подписей — только предупреждение, см. комментарий к классу.
        foreach (var group in macro.Nodes
                     .GroupBy(MacroNodeNames.Display, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (var node in group)
            {
                issues.Add(Warning(node, string.Format(
                    CultureInfo.CurrentCulture, Strings_Engine.Validation_Node_DuplicateName, group.Key)));
            }
        }

        // Стартовая нода.
        var startIsValid = macro.StartNodeId != Guid.Empty && byId.ContainsKey(macro.StartNodeId);
        if (!startIsValid)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, null, null,
                Strings_Engine.Validation_Graph_StartNodeMissing));
        }

        // Сломанные рёбра. Целевой id не называем: ноды с таким id в графе нет, а голый guid
        // читателю ничего не сообщит — важно, ЧЕЙ исход повис.
        foreach (var node in byId.Values)
        {
            foreach (var (edgeName, targetId) in OutgoingEdges(node))
            {
                if (targetId is { } target && !byId.ContainsKey(target))
                {
                    issues.Add(Error(node, string.Format(
                        CultureInfo.CurrentCulture, Strings_Engine.Validation_Node_EdgeToMissingNode, edgeName)));
                }
            }
        }

        // ClickNode: ровно одно из Point / PointVar.
        foreach (var node in byId.Values.OfType<ClickNode>())
        {
            if (node.Point is null == node.PointVar is null)
            {
                issues.Add(Error(node, Strings_Engine.Validation_Node_ClickNeedsExactlyOnePoint));
            }
        }

        // Порог совпадения. Ноль означал бы «совпадает что угодно», выше единицы — «не совпадёт
        // никогда»: и то и другое не настройка точности, а выключенная нода.
        foreach (var node in byId.Values)
        {
            if (MatchThresholdOf(node) is { } threshold && (threshold <= 0 || threshold > 1))
            {
                issues.Add(Error(node, string.Format(
                    CultureInfo.CurrentCulture,
                    Strings_Engine.Validation_Node_MatchThresholdOutOfRange,
                    threshold.ToString("0.###", CultureInfo.InvariantCulture))));
            }
        }

        // Шаблоны, которых в бандле нет.
        //
        // ПРЕДУПРЕЖДЕНИЕ, а не ошибка, и это выбор, а не осторожность. Ошибка запрещает
        // сохранение, а «набрал имя шаблона → импортировал файл» — совершенно нормальный порядок
        // действий, и запрещать первый шаг до второго значило бы требовать держать имя в голове.
        // К тому же ненайденный шаблон не обрывает прогон: нода честно уходит по «не найдено» /
        // «не совпало», и ветка на этот случай в графе как раз и предусмотрена. Сказать надо —
        // запрещать нет; та же логика, что у дубликата подписи выше.
        if (templates is not null)
        {
            foreach (var usage in MacroTemplateAnalysis.Analyze(macro))
            {
                if (templates.Has(usage.Name, usage.IsSet))
                {
                    continue;
                }

                foreach (var reference in usage.References)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        reference.NodeId,
                        reference.NodeName,
                        string.Format(
                            CultureInfo.CurrentCulture,
                            usage.IsSet
                                ? Strings_Engine.Validation_Node_TemplateSetMissing
                                : Strings_Engine.Validation_Node_TemplateMissing,
                            usage.Name)));
                }
            }
        }

        var reachable = ComputeReachable(macro, byId, startIsValid);

        // Правило контекста. Для графов без триггеров (библиотечных) пропускается целиком —
        // им контекстное окно всегда даёт вызывающий.
        var isLibraryOnly = macro.Triggers.Count == 0;
        var hasProcessTrigger = macro.Triggers.Any(trigger => trigger is ProcessAppearedTrigger);
        if (!isLibraryOnly && !hasProcessTrigger)
        {
            foreach (var id in reachable)
            {
                switch (byId[id])
                {
                    case FindElementNode or WaitForElementNode or RecognizeTagNode:
                        issues.Add(Error(byId[id], Strings_Engine.Validation_Node_ConditionalNeedsContextWindow));
                        break;
                    case KeyPressNode { Target: null } or ClickNode { Target: null } or AddTagNode { Target: null }
                        or RemoveTagNode { Target: null } or SetIconNode { Target: null }
                        or RunSubmacroNode { Target: null }:
                        issues.Add(Error(byId[id], Strings_Engine.Validation_Node_ActionNeedsContextWindow));
                        break;
                }
            }
        }

        // Недостижимые ноды.
        foreach (var (id, node) in byId)
        {
            if (!reachable.Contains(id))
            {
                issues.Add(Warning(node, Strings_Engine.Validation_Node_Unreachable));
            }
        }

        // Циклы без пауз: циклические сильно связные компоненты достижимого подграфа, внутри
        // которых нет ни одной задержки.
        foreach (var component in StronglyConnectedComponents(reachable, byId))
        {
            var isCyclic = component.Count > 1 || HasSelfLoop(component[0], byId);
            if (!isCyclic)
            {
                continue;
            }

            if (!component.Any(id => byId[id] is DelayNode or WaitForElementNode))
            {
                var names = component.Select(id => MacroNodeNames.Display(byId[id]));
                issues.Add(Warning(byId[component[0]], string.Format(
                    CultureInfo.CurrentCulture,
                    Strings_Engine.Validation_Graph_LoopWithoutDelay,
                    string.Join(" → ", names))));
            }
        }

        return issues;
    }

    /// <summary>
    /// «Под-макрос это записал, а снаружи не видно» — предупреждение на ноде вызова (волна F4).
    ///
    /// <b>Зачем оно вообще.</b> Подпрогон получает КОПИЮ переменных родителя, и обратной записи
    /// нет (§5.3). Решение сохранено — оно уже было и оно безопаснее, — но обязано перестать быть
    /// молчаливым. Живой пример из спеки: <c>RecognizeTag</c> пишет <c>tag</c>, <c>SetIcon</c>
    /// читает <c>{tag}</c>. Пока обе ноды внутри одного под-макроса — работает; разнеси их, и
    /// прогон оборвётся на чтении неопределённой переменной, а связать этот отказ с причиной
    /// пользователь не сможет никак.
    ///
    /// <b>Условие сужено до случая, который И ЕСТЬ эта беда:</b> под-макрос переменную пишет,
    /// родитель её ЧИТАЕТ, а сам НЕ пишет нигде. Под-макрос, пишущий что-то для себя, ничего не
    /// нарушает; родитель, у которого есть свой писатель, получит своё значение. Предупреждать в
    /// этих случаях значило бы приучить не читать предупреждения — та же логика, по которой ноль
    /// совпадений у селектора красит ноду, а «окон нет вообще» — нет (D4).
    ///
    /// Предупреждение, а не ошибка, — как и «в макросе нет такого шаблона»: «вынес кусок в
    /// функцию, сейчас допишу» нормальный порядок действий, а запрет на сохранение посреди него
    /// стоил бы дороже.
    /// </summary>
    private static void AddEscapedVariableWarnings(
        MacroGraph macro,
        IReadOnlyList<MacroSubmacro> submacros,
        List<ValidationIssue> issues)
    {
        var calls = macro.Nodes.OfType<RunSubmacroNode>().ToList();
        if (calls.Count == 0 || submacros.Count == 0)
        {
            return;
        }

        var outside = MacroVariableAnalysis.Analyze(macro);
        var readOutside = outside
            .Where(variable => variable.IsRead && !variable.IsDefined)
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (readOutside.Count == 0)
        {
            return;
        }

        var written = submacros.ToDictionary(
            submacro => submacro.Id,
            submacro => MacroVariableAnalysis.Analyze(submacro.Graph)
                .Where(variable => variable.Writes.Count > 0)
                .Select(variable => variable.Name)
                .ToList());

        foreach (var call in calls)
        {
            if (!written.TryGetValue(call.SubmacroId, out var names))
            {
                continue;
            }

            var lost = names.Where(readOutside.Contains).ToList();
            if (lost.Count == 0)
            {
                continue;
            }

            var name = submacros.First(submacro => submacro.Id == call.SubmacroId).Name;
            issues.Add(Warning(call, lost.Count == 1
                ? string.Format(
                    CultureInfo.CurrentCulture, Strings_Engine.Validation_Submacro_VariableLost_One, name, lost[0])
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Strings_Engine.Validation_Submacro_VariableLost_Many,
                    name,
                    string.Join(", ", lost.Select(variable => $"«{variable}»")))));
        }
    }

    private static ValidationIssue Error(MacroNode node, string message) =>
        new(ValidationSeverity.Error, node.Id, MacroNodeNames.Display(node), message);

    private static ValidationIssue Warning(MacroNode node, string message) =>
        new(ValidationSeverity.Warning, node.Id, MacroNodeNames.Display(node), message);

    private static double? MatchThresholdOf(MacroNode node) => node switch
    {
        FindElementNode n => n.MatchThreshold,
        WaitForElementNode n => n.MatchThreshold,
        RecognizeTagNode n => n.MatchThreshold,
        _ => null,
    };

    // Состав исходов каждого типа ноды живёт в MacroNodeEdges — там же, где перенацеливание,
    // которым пользуется извлечение в под-макрос. Второй список полей разошёлся бы с первым на
    // первом же новом типе ноды, и разошёлся бы молча.
    private static IEnumerable<(string EdgeName, Guid? TargetId)> OutgoingEdges(MacroNode node) =>
        MacroNodeEdges.Outgoing(node);

    private static HashSet<Guid> ComputeReachable(MacroGraph macro, Dictionary<Guid, MacroNode> byId, bool startIsValid)
    {
        var visited = new HashSet<Guid>();
        if (!startIsValid)
        {
            return visited;
        }

        var stack = new Stack<Guid>();
        stack.Push(macro.StartNodeId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!visited.Add(id))
            {
                continue;
            }

            foreach (var (_, targetId) in OutgoingEdges(byId[id]))
            {
                if (targetId is { } target && byId.ContainsKey(target))
                {
                    stack.Push(target);
                }
            }
        }

        return visited;
    }

    private static bool HasSelfLoop(Guid id, Dictionary<Guid, MacroNode> byId) =>
        OutgoingEdges(byId[id]).Any(edge => edge.TargetId == id);

    // Алгоритм Тарьяна. Рекурсия здесь нормальна: графы макросов пишет человек, и они крошечные.
    private static List<List<Guid>> StronglyConnectedComponents(
        HashSet<Guid> reachable,
        Dictionary<Guid, MacroNode> byId)
    {
        var index = new Dictionary<Guid, int>();
        var lowLink = new Dictionary<Guid, int>();
        var onStack = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        var components = new List<List<Guid>>();
        var nextIndex = 0;

        foreach (var id in reachable)
        {
            if (!index.ContainsKey(id))
            {
                StrongConnect(id);
            }
        }

        return components;

        void StrongConnect(Guid v)
        {
            index[v] = lowLink[v] = nextIndex++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var (_, target) in OutgoingEdges(byId[v]))
            {
                if (target is not { } w || !reachable.Contains(w))
                {
                    continue;
                }

                if (!index.ContainsKey(w))
                {
                    StrongConnect(w);
                    lowLink[v] = Math.Min(lowLink[v], lowLink[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowLink[v] = Math.Min(lowLink[v], index[w]);
                }
            }

            if (lowLink[v] == index[v])
            {
                var component = new List<Guid>();
                Guid member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                } while (member != v);

                components.Add(component);
            }
        }
    }
}
