using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Validation;

/// <summary>
/// Статические проверки, которые гоняются при сохранении и загрузке, — ловят сломанные графы
/// раньше, чем исполнителю придётся прерываться в рантайме.
///
/// Ошибки: несуществующий или пустой StartNodeId; дубликаты id нод; рёбра в несуществующие
/// ноды; ClickNode, у которого заданы оба или ни одного из Point/PointVar; и правило
/// контекста — макрос, способный ЗАПУСТИТЬСЯ САМ без контекстного окна (то есть у него есть
/// триггеры, но нет <see cref="ProcessAppearedTrigger"/>), не имеет права содержать
/// ДОСТИЖИМУЮ условную ноду или ноду действия без селектора (упрощённое правило по §0.2
/// плана; подпрогоны RunMacro с Target, которые контекст всё же дали бы, намеренно не
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
/// компонентам достижимого подграфа).
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
        var byId = new Dictionary<string, MacroNode>(StringComparer.Ordinal);
        foreach (var node in macro.Nodes)
        {
            if (!byId.TryAdd(node.Id, node))
            {
                issues.Add(Error(node.Id, $"Дубликат id ноды: «{node.Id}»."));
            }
        }

        // Стартовая нода.
        var startIsValid = !string.IsNullOrWhiteSpace(macro.StartNodeId) && byId.ContainsKey(macro.StartNodeId);
        if (!startIsValid)
        {
            issues.Add(Error(null, $"StartNodeId «{macro.StartNodeId}» не указывает ни на одну ноду."));
        }

        // Сломанные рёбра.
        foreach (var (id, node) in byId)
        {
            foreach (var (edgeName, targetId) in OutgoingEdges(node))
            {
                if (targetId is not null && !byId.ContainsKey(targetId))
                {
                    issues.Add(Error(id, $"Ребро {edgeName} ведёт в несуществующую ноду «{targetId}»."));
                }
            }
        }

        // ClickNode: ровно одно из Point / PointVar.
        foreach (var node in byId.Values.OfType<ClickNode>())
        {
            if (node.Point is null == node.PointVar is null)
            {
                issues.Add(Error(node.Id, "У ClickNode должно быть задано ровно одно из Point / PointVar."));
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
                        issues.Add(Error(id,
                            "Условной ноде нужно контекстное окно, но этот макрос может стартовать без него (нет триггера на появление процесса)."));
                        break;
                    case KeyPressNode { Target: null } or ClickNode { Target: null } or AddTagNode { Target: null }
                        or RemoveTagNode { Target: null } or SetIconNode { Target: null } or RunMacroNode { Target: null }:
                        issues.Add(Error(id,
                            "Ноде действия без селектора Target нужно контекстное окно, но этот макрос может стартовать без него (нет триггера на появление процесса)."));
                        break;
                }
            }
        }

        // Недостижимые ноды.
        foreach (var (id, _) in byId)
        {
            if (!reachable.Contains(id))
            {
                issues.Add(Warning(id, "Нода недостижима из StartNodeId."));
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
                issues.Add(Warning(component[0],
                    $"Цикл без ноды Delay/WaitForElement — будет крутиться вхолостую: {string.Join(" → ", component)}."));
            }
        }

        return issues;
    }

    private static ValidationIssue Error(string? nodeId, string message) =>
        new(ValidationSeverity.Error, nodeId, message);

    private static ValidationIssue Warning(string? nodeId, string message) =>
        new(ValidationSeverity.Warning, nodeId, message);

    private static IEnumerable<(string EdgeName, string? TargetId)> OutgoingEdges(MacroNode node) => node switch
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

    private static HashSet<string> ComputeReachable(MacroGraph macro, Dictionary<string, MacroNode> byId, bool startIsValid)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (!startIsValid)
        {
            return visited;
        }

        var stack = new Stack<string>();
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
                if (targetId is not null && byId.ContainsKey(targetId))
                {
                    stack.Push(targetId);
                }
            }
        }
        return visited;
    }

    private static bool HasSelfLoop(string id, Dictionary<string, MacroNode> byId) =>
        OutgoingEdges(byId[id]).Any(edge => string.Equals(edge.TargetId, id, StringComparison.Ordinal));

    // Алгоритм Тарьяна. Рекурсия здесь нормальна: графы макросов пишет человек, и они крошечные.
    private static List<List<string>> StronglyConnectedComponents(
        HashSet<string> reachable,
        Dictionary<string, MacroNode> byId)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var components = new List<List<string>>();
        var nextIndex = 0;

        foreach (var id in reachable)
        {
            if (!index.ContainsKey(id))
            {
                StrongConnect(id);
            }
        }
        return components;

        void StrongConnect(string v)
        {
            index[v] = lowLink[v] = nextIndex++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var (_, w) in OutgoingEdges(byId[v]))
            {
                if (w is null || !reachable.Contains(w))
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
                var component = new List<string>();
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                } while (!string.Equals(member, v, StringComparison.Ordinal));
                components.Add(component);
            }
        }
    }
}
