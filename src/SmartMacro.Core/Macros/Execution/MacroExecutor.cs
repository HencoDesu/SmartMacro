using Microsoft.Extensions.Logging;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Walks a <see cref="MacroGraph"/>: execute the current node, follow its edge (per
/// outcome for conditionals), stop cleanly on a <c>null</c> edge. Side effects go through
/// <see cref="IMacroPrimitives"/> (input/vision/icon) and <see cref="WindowRegistry"/>
/// (tags); sub-macros resolve via <see cref="IMacroGraphResolver"/>.
///
/// Semantics:
///   * Action node with a Target selector — the registry snapshot is taken at that moment
///     and the SAME action fans out to every matching window in parallel. Zero matches is
///     a legal no-op. Without Target the action hits the context window; no context = abort.
///   * Conditional nodes require the context window; outcomes pick the edge and write
///     <c>FoundPointVar</c>/<c>ResultVar</c> before the edge is taken.
///   * <see cref="RunMacroNode"/> — sub-runs with copied variables, depth ≤ <see cref="MaxDepth"/>,
///     name cycles abort. <c>Await=false</c> is fire-and-forget (failures only logged).
///   * Cancellation is honored between nodes and inside primitives; a cancelled run ends
///     silently with <see cref="MacroRunStatus.Cancelled"/>.
///
/// Stateless and registry-agnostic — safe as a singleton; run bookkeeping lives in
/// <see cref="MacroRunRegistry"/>, wired up by the caller via <see cref="MacroRunContext.OnNodeEntered"/>.
/// </summary>
public sealed partial class MacroExecutor
{
    /// <summary>Maximum <see cref="RunMacroNode"/> nesting depth (root run = 0).</summary>
    public const int MaxDepth = 4;

    private readonly IMacroPrimitives _primitives;
    private readonly WindowRegistry _windows;
    private readonly IMacroGraphResolver _resolver;
    private readonly ILogger<MacroExecutor> _logger;

    public MacroExecutor(
        IMacroPrimitives primitives,
        WindowRegistry windows,
        IMacroGraphResolver resolver,
        ILogger<MacroExecutor> logger)
    {
        _primitives = primitives;
        _windows = windows;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Runs the graph to completion. Never throws for run-level failures — the outcome
    /// (completed / aborted with reason / cancelled) is the returned result.
    /// </summary>
    public async Task<MacroRunResult> RunAsync(MacroGraph macro, MacroRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await RunCoreAsync(macro, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogCancelled(macro.Name);
            return MacroRunResult.Cancelled;
        }
        catch (MacroVariableException ex)
        {
            LogAborted(macro.Name, ex.Message);
            return MacroRunResult.Aborted(ex.Message);
        }
        catch (MacroRunAbortException ex)
        {
            LogAborted(macro.Name, ex.Message);
            return MacroRunResult.Aborted(ex.Message);
        }
    }

    private async Task<MacroRunResult> RunCoreAsync(MacroGraph macro, MacroRunContext context, CancellationToken ct)
    {
        var nodesById = new Dictionary<string, MacroNode>(StringComparer.Ordinal);
        foreach (var node in macro.Nodes)
        {
            if (!nodesById.TryAdd(node.Id, node))
            {
                throw new MacroRunAbortException($"Macro '{macro.Name}' contains duplicate node id '{node.Id}'.");
            }
        }

        // The chain including THIS macro — cycle checks and child contexts build on it.
        var callChain = new List<string>(context.CallChain.Count + 1);
        callChain.AddRange(context.CallChain);
        callChain.Add(macro.Name);

        var currentId = macro.StartNodeId;
        while (currentId is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (!nodesById.TryGetValue(currentId, out var node))
            {
                throw new MacroRunAbortException($"Macro '{macro.Name}': edge points to unknown node '{currentId}'.");
            }

            context.OnNodeEntered?.Invoke(node.Id);
            currentId = await ExecuteNodeAsync(macro, node, context, callChain, ct).ConfigureAwait(false);
        }

        LogCompleted(macro.Name);
        return MacroRunResult.Completed;
    }

    private async Task<string?> ExecuteNodeAsync(
        MacroGraph macro,
        MacroNode node,
        MacroRunContext context,
        IReadOnlyList<string> callChain,
        CancellationToken ct)
    {
        switch (node)
        {
            case KeyPressNode n:
            {
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.PressKeyAsync(hwnd, n.Key, ct))).ConfigureAwait(false);
                return n.Next;
            }
            case ClickNode n:
            {
                var point = ResolveClickPoint(n, context.Variables);
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.ClickAsync(hwnd, point, n.DoubleClick, ct))).ConfigureAwait(false);
                return n.Next;
            }
            case DelayNode n:
            {
                if (n.Ms > 0)
                {
                    await Task.Delay(n.Ms, ct).ConfigureAwait(false);
                }
                return n.Next;
            }
            case AddTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                foreach (var hwnd in ResolveTargets(n.Id, n.Target, context))
                {
                    _windows.AddTag(hwnd, tag);
                }
                return n.Next;
            }
            case RemoveTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                foreach (var hwnd in ResolveTargets(n.Id, n.Target, context))
                {
                    _windows.RemoveTag(hwnd, tag);
                }
                return n.Next;
            }
            case SetIconNode n:
            {
                var iconPath = context.Variables.Interpolate(n.IconPath);
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.SetIconAsync(hwnd, iconPath, ct))).ConfigureAwait(false);
                return n.Next;
            }
            case RunMacroNode n:
                return await ExecuteRunMacroAsync(macro, n, context, callChain, ct).ConfigureAwait(false);
            case FindElementNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var found = await _primitives.FindElementAsync(hwnd, n.Template, n.Region, ct).ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        context.Variables.Set(n.FoundPointVar, point);
                    }
                    return n.Found;
                }
                return n.NotFound;
            }
            case WaitForElementNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var found = await _primitives.WaitForElementAsync(hwnd, n.Template, n.Region, n.TimeoutMs, ct).ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        context.Variables.Set(n.FoundPointVar, point);
                    }
                    return n.Found;
                }
                return n.Timeout;
            }
            case RecognizeTagNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var tag = await _primitives.RecognizeAsync(hwnd, n.TemplateSet, n.Region, ct).ConfigureAwait(false);
                if (tag is not null)
                {
                    context.Variables.Set(n.ResultVar, tag);
                    if (n.ApplyTag)
                    {
                        _windows.AddTag(hwnd, tag);
                    }
                    return n.Matched;
                }
                return n.NotMatched;
            }
            default:
                throw new MacroRunAbortException($"Macro '{macro.Name}': node '{node.Id}' has unsupported type {node.GetType().Name}.");
        }
    }

    private async Task<string?> ExecuteRunMacroAsync(
        MacroGraph macro,
        RunMacroNode node,
        MacroRunContext context,
        IReadOnlyList<string> callChain,
        CancellationToken ct)
    {
        var name = context.Variables.Interpolate(node.MacroName);

        if (context.Depth + 1 > MaxDepth)
        {
            throw new MacroRunAbortException(
                $"Macro '{macro.Name}': running '{name}' would exceed the sub-macro depth limit ({MaxDepth}).");
        }
        if (callChain.Contains(name, StringComparer.Ordinal))
        {
            throw new MacroRunAbortException(
                $"Macro '{macro.Name}': macro call cycle {string.Join(" → ", callChain)} → {name}.");
        }

        var subMacro = _resolver.TryGet(name)
                       ?? throw new MacroRunAbortException($"Macro '{macro.Name}': node '{node.Id}' references unknown macro '{name}'.");

        List<MacroRunContext> childContexts = [];
        if (node.Target is { } selector)
        {
            foreach (var window in SelectorEvaluator.Select(_windows.Snapshot(), selector))
            {
                childContexts.Add(BuildChildContext(context, callChain, window.Hwnd));
            }
            if (childContexts.Count == 0)
            {
                LogNoTargets(macro.Name, node.Id);
            }
        }
        else
        {
            var hwnd = RequireContext(node.Id, context);
            childContexts.Add(BuildChildContext(context, callChain, hwnd));
        }

        var subRuns = childContexts.Select(child => RunAsync(subMacro, child, ct)).ToList();
        if (node.Await)
        {
            var results = await Task.WhenAll(subRuns).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result.Status == MacroRunStatus.Cancelled)
                {
                    throw new OperationCanceledException(ct);
                }
                if (result.Status == MacroRunStatus.Aborted)
                {
                    throw new MacroRunAbortException($"Macro '{macro.Name}': sub-macro '{name}' aborted: {result.Error}");
                }
            }
        }
        else
        {
            _ = ObserveDetachedSubRunsAsync(name, subRuns);
        }

        return node.Next;
    }

    private static MacroRunContext BuildChildContext(MacroRunContext parent, IReadOnlyList<string> callChain, IntPtr contextWindow)
    {
        return new MacroRunContext
        {
            ContextWindow = contextWindow,
            Variables = parent.Variables.Clone(),
            Depth = parent.Depth + 1,
            CallChain = callChain,
            OnNodeEntered = parent.OnNodeEntered,
        };
    }

    /// <summary>
    /// Fire-and-forget sub-runs still get their failures observed and logged — they just
    /// don't block the parent walk.
    /// </summary>
    private async Task ObserveDetachedSubRunsAsync(string name, IReadOnlyList<Task<MacroRunResult>> subRuns)
    {
        try
        {
            var results = await Task.WhenAll(subRuns).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result.Status == MacroRunStatus.Aborted)
                {
                    LogDetachedSubRunAborted(name, result.Error ?? "(no details)");
                }
            }
        }
        catch (Exception ex)
        {
            LogDetachedSubRunFailed(ex, name);
        }
    }

    private IReadOnlyList<IntPtr> ResolveTargets(string nodeId, TargetSelector? target, MacroRunContext context)
    {
        if (target is null)
        {
            if (context.ContextWindow is { } hwnd)
            {
                return [hwnd];
            }
            throw new MacroRunAbortException(
                $"Node '{nodeId}' has no Target selector and this run has no context window (hotkey-triggered runs have none).");
        }

        var matched = SelectorEvaluator.Select(_windows.Snapshot(), target);
        return matched.Select(window => window.Hwnd).ToArray();
    }

    private static IntPtr RequireContext(string nodeId, MacroRunContext context)
    {
        return context.ContextWindow
               ?? throw new MacroRunAbortException(
                   $"Node '{nodeId}' requires a context window and this run has none (hotkey-triggered runs have none).");
    }

    private static ScreenPoint ResolveClickPoint(ClickNode node, MacroVariables variables)
    {
        return (node.Point, node.PointVar) switch
        {
            ({ } point, null) => point,
            (null, { } pointVar) => variables.GetPoint(pointVar),
            _ => throw new MacroRunAbortException($"Click node '{node.Id}' must set exactly one of Point / PointVar."),
        };
    }

    [LoggerMessage(LogLevel.Information, "Macro '{MacroName}' completed")]
    partial void LogCompleted(string macroName);

    [LoggerMessage(LogLevel.Information, "Macro '{MacroName}' cancelled")]
    partial void LogCancelled(string macroName);

    [LoggerMessage(LogLevel.Warning, "Macro '{MacroName}' aborted: {Reason}")]
    partial void LogAborted(string macroName, string reason);

    [LoggerMessage(LogLevel.Debug, "Macro '{MacroName}': node '{NodeId}' selector matched no windows — no-op")]
    partial void LogNoTargets(string macroName, string nodeId);

    [LoggerMessage(LogLevel.Warning, "Detached sub-macro '{MacroName}' aborted: {Reason}")]
    partial void LogDetachedSubRunAborted(string macroName, string reason);

    [LoggerMessage(LogLevel.Error, "Detached sub-macro '{MacroName}' failed unexpectedly")]
    partial void LogDetachedSubRunFailed(Exception exception, string macroName);
}

/// <summary>
/// Internal control-flow exception: a node hit a run-level error. Converted by
/// <see cref="MacroExecutor.RunAsync"/> into <see cref="MacroRunStatus.Aborted"/> —
/// it never escapes the executor.
/// </summary>
internal sealed class MacroRunAbortException(string message) : Exception(message);
