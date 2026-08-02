using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Validation;

/// <summary>
/// Static checks run on save/load — catches broken graphs before the executor has to
/// abort at runtime.
///
/// Errors: unknown/blank StartNodeId; duplicate node ids; edges referencing unknown
/// nodes; ClickNode with both or neither of Point/PointVar; and the context rule — a
/// macro that can START ON ITS OWN without a context window (i.e. it has triggers, but no
/// <see cref="ProcessAppearedTrigger"/>) may not contain a REACHABLE conditional node or
/// a targetless action node (simplified rule per plan §0.2; RunMacro-with-Target sub-runs
/// that would supply a context are deliberately not modeled).
///
/// A macro with NO triggers at all is exempt from the context rule: it is library-only,
/// reachable solely through <see cref="RunMacroNode"/> or a UI Run against a window, and
/// its context therefore always comes from the caller. Targetless nodes are in fact the
/// CORRECT shape for such a macro — that is what makes it reusable per window.
///
/// Warnings: unreachable nodes; cycles containing no <see cref="DelayNode"/> /
/// <see cref="WaitForElementNode"/> (hot loops — detected per strongly connected
/// component of the reachable subgraph).
/// </summary>
public static class MacroGraphValidator
{
    /// <summary>Validates the graph. Empty list = clean.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(MacroGraph macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var issues = new List<ValidationIssue>();

        // Duplicate ids. byId keeps the FIRST occurrence — later rules work off that map.
        var byId = new Dictionary<string, MacroNode>(StringComparer.Ordinal);
        foreach (var node in macro.Nodes)
        {
            if (!byId.TryAdd(node.Id, node))
            {
                issues.Add(Error(node.Id, $"Duplicate node id '{node.Id}'."));
            }
        }

        // Start node.
        var startIsValid = !string.IsNullOrWhiteSpace(macro.StartNodeId) && byId.ContainsKey(macro.StartNodeId);
        if (!startIsValid)
        {
            issues.Add(Error(null, $"StartNodeId '{macro.StartNodeId}' does not reference a node."));
        }

        // Broken edges.
        foreach (var (id, node) in byId)
        {
            foreach (var (edgeName, targetId) in OutgoingEdges(node))
            {
                if (targetId is not null && !byId.ContainsKey(targetId))
                {
                    issues.Add(Error(id, $"Edge {edgeName} references unknown node '{targetId}'."));
                }
            }
        }

        // ClickNode: exactly one of Point / PointVar.
        foreach (var node in byId.Values.OfType<ClickNode>())
        {
            if (node.Point is null == node.PointVar is null)
            {
                issues.Add(Error(node.Id, "ClickNode must set exactly one of Point / PointVar."));
            }
        }

        var reachable = ComputeReachable(macro, byId, startIsValid);

        // Context rule. Skipped entirely for trigger-less (library-only) graphs — the
        // caller always supplies their context window.
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
                            "Conditional node requires a context window, but this macro can start without one (no process trigger)."));
                        break;
                    case KeyPressNode { Target: null } or ClickNode { Target: null } or AddTagNode { Target: null }
                        or RemoveTagNode { Target: null } or SetIconNode { Target: null } or RunMacroNode { Target: null }:
                        issues.Add(Error(id,
                            "Action node without a Target selector requires a context window, but this macro can start without one (no process trigger)."));
                        break;
                }
            }
        }

        // Unreachable nodes.
        foreach (var (id, _) in byId)
        {
            if (!reachable.Contains(id))
            {
                issues.Add(Warning(id, "Node is unreachable from StartNodeId."));
            }
        }

        // Hot loops: cyclic SCCs of the reachable subgraph with no pause inside.
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
                    $"Cycle without a Delay/WaitForElement node (hot loop): {string.Join(" → ", component)}."));
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

    // Tarjan. Recursion is fine — macro graphs are user-authored and tiny.
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
