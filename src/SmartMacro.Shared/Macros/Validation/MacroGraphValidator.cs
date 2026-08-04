using System.Globalization;
using SmartMacro.Macros.Model;

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
/// Макрос СОВСЕМ без триггеров от правила контекста освобождён: он библиотечный, попасть в
/// него можно только через <see cref="RunMacroNode"/> или ручной запуск из интерфейса против
/// конкретного окна, а значит контекст всегда приходит от вызывающего. Ноды без селектора для
/// такого макроса — как раз ПРАВИЛЬНАЯ форма: именно она и делает его переиспользуемым для
/// каждого окна.
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
/// </summary>
public static class MacroGraphValidator
{
    /// <summary>Проверяет граф. Пустой список = всё чисто.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(MacroGraph macro)
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
                issues.Add(Error(node, "Дубликат id ноды: две ноды графа несут один и тот же идентификатор."));
            }
        }

        // Дубликаты подписей — только предупреждение, см. комментарий к классу.
        foreach (var group in macro.Nodes
                     .GroupBy(MacroNodeNames.Display, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (var node in group)
            {
                issues.Add(Warning(node,
                    $"Имя «{group.Key}» носит больше одной ноды — в логе прогона их будет не различить."));
            }
        }

        // Стартовая нода.
        var startIsValid = macro.StartNodeId != Guid.Empty && byId.ContainsKey(macro.StartNodeId);
        if (!startIsValid)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, null, null,
                "Стартовая нода не задана или не найдена в графе."));
        }

        // Сломанные рёбра. Целевой id не называем: ноды с таким id в графе нет, а голый guid
        // читателю ничего не сообщит — важно, ЧЕЙ исход повис.
        foreach (var node in byId.Values)
        {
            foreach (var (edgeName, targetId) in OutgoingEdges(node))
            {
                if (targetId is { } target && !byId.ContainsKey(target))
                {
                    issues.Add(Error(node, $"Ребро {edgeName} ведёт в ноду, которой в графе нет."));
                }
            }
        }

        // ClickNode: ровно одно из Point / PointVar.
        foreach (var node in byId.Values.OfType<ClickNode>())
        {
            if (node.Point is null == node.PointVar is null)
            {
                issues.Add(Error(node, "У ClickNode должно быть задано ровно одно из Point / PointVar."));
            }
        }

        // Порог совпадения. Ноль означал бы «совпадает что угодно», выше единицы — «не совпадёт
        // никогда»: и то и другое не настройка точности, а выключенная нода.
        foreach (var node in byId.Values)
        {
            if (MatchThresholdOf(node) is { } threshold && (threshold <= 0 || threshold > 1))
            {
                issues.Add(Error(node,
                    $"Порог совпадения {threshold.ToString("0.###", CultureInfo.InvariantCulture)} вне диапазона (0; 1]."));
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
                        issues.Add(Error(byId[id],
                            "Условной ноде нужно контекстное окно, но этот макрос может стартовать без него (нет триггера на появление процесса)."));
                        break;
                    case KeyPressNode { Target: null } or ClickNode { Target: null } or AddTagNode { Target: null }
                        or RemoveTagNode { Target: null } or SetIconNode { Target: null }
                        or RunMacroNode { Target: null }:
                        issues.Add(Error(byId[id],
                            "Ноде действия без селектора Target нужно контекстное окно, но этот макрос может стартовать без него (нет триггера на появление процесса)."));
                        break;
                }
            }
        }

        // Недостижимые ноды.
        foreach (var (id, node) in byId)
        {
            if (!reachable.Contains(id))
            {
                issues.Add(Warning(node, "Нода недостижима из стартовой."));
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
                issues.Add(Warning(byId[component[0]],
                    $"Цикл без ноды Delay/WaitForElement — будет крутиться вхолостую: {string.Join(" → ", names)}."));
            }
        }

        return issues;
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

    private static IEnumerable<(string EdgeName, Guid? TargetId)> OutgoingEdges(MacroNode node) => node switch
    {
        KeyPressNode n => [("Next", n.Next)],
        ClickNode n => [("Next", n.Next)],
        DelayNode n => [("Next", n.Next)],
        AddTagNode n => [("Next", n.Next)],
        RemoveTagNode n => [("Next", n.Next)],
        SetIconNode n => [("Next", n.Next)],
        RunMacroNode n => [("Next", n.Next)],
        FindElementNode n => [("Found", n.Found), ("NotFound", n.NotFound)],
        WaitForElementNode n => [("Found", n.Found), ("Timeout", n.Timeout)],
        RecognizeTagNode n => [("Matched", n.Matched), ("NotMatched", n.NotMatched)],
        _ => [],
    };

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
