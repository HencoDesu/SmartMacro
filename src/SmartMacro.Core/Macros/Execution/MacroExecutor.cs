using System.Globalization;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
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
///
/// <b>Tracing (wave D3b).</b> Every call to <see cref="RunAsync"/> is one WALK with its own
/// id and its own clock, reported to <see cref="MacroRunContext.Observer"/>. The walk, not
/// the run, is the unit: a <see cref="RunMacroNode"/> fan-out forks one walk per window, and
/// they are only distinguishable downstream because each got its own id here. Node-level
/// events — and the detail strings that go with them — are produced ONLY while the observer
/// says someone is listening, so an unwatched daemon pays one flag read per node.
///
/// <b>Debugging (wave D5).</b> <see cref="MacroRunContext.Debugger"/> can park the walk
/// BETWEEN two nodes — never inside one. That boundary is the whole safety argument: every
/// input node's <c>ActivateAsync</c>/<c>DeactivateAsync</c> bracket and every vision tick's
/// wake/re-freeze live entirely inside <see cref="IMacroPrimitives"/>, so by the time control
/// is back here no game window is left woken. Pausing here cannot strand a frozen client;
/// pausing anywhere deeper could. Same <c>IsActive</c> gate discipline as the observer — an
/// undebugged walk pays one volatile read per node and allocates nothing.
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

        // Opened before the first node and closed in every exit path below, so the observer's
        // roster of live walks can never leak an entry — that roster is what a panel
        // connecting mid-run is shown.
        var trace = MacroWalkTrace.Begin(
            context.Observer,
            context.RunId,
            macro.Name,
            context.ContextWindow,
            context.Depth);

        MacroRunResult result;
        try
        {
            result = await RunCoreAsync(macro, context, trace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogCancelled(macro.Name);
            result = MacroRunResult.Cancelled;
        }
        catch (MacroVariableException ex)
        {
            LogAborted(macro.Name, ex.Message);
            result = MacroRunResult.Aborted(ex.Message);
        }
        catch (MacroRunAbortException ex)
        {
            LogAborted(macro.Name, ex.Message);
            result = MacroRunResult.Aborted(ex.Message);
        }

        // Before the finish event, so a panel that reacts to WalkFinished by re-reading the
        // debugger cannot see a walk that is both over and still registered.
        context.Debugger?.WalkFinished(trace.WalkId);
        trace.Finished(WalkOutcome(result.Status), result.Error);
        return result;
    }

    private static string WalkOutcome(MacroRunStatus status) => status switch
    {
        MacroRunStatus.Completed => RunOutcomes.Completed,
        MacroRunStatus.Cancelled => RunOutcomes.Cancelled,
        _ => RunOutcomes.Aborted,
    };

    private async Task<MacroRunResult> RunCoreAsync(MacroGraph macro, MacroRunContext context, MacroWalkTrace trace, CancellationToken ct)
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

        // The variables a walk starts with — in practice the trigger's `cursor` seed, plus
        // whatever a parent walk passed down. Reported once, so the panel's variables panel
        // has a live value for the one variable no node ever writes.
        if (trace.IsTracing)
        {
            foreach (var (name, value) in context.Variables.Entries)
            {
                trace.VariableSet(name, value.DisplayString, nodeId: null);
            }
        }

        var currentId = macro.StartNodeId;
        while (currentId is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (!nodesById.TryGetValue(currentId, out var node))
            {
                throw new MacroRunAbortException($"Macro '{macro.Name}': edge points to unknown node '{currentId}'.");
            }

            context.OnNodeEntered?.Invoke(node.Id);
            trace.NodeEntered(node.Id);

            // THE DEBUGGER GATE. Between two nodes and before the node's clock starts, so a
            // pause costs the paused node no measured time and — see the class comment — no
            // game window is sitting woken while we wait.
            await GateAsync(context, trace, macro.Name, node.Id, ct).ConfigureAwait(false);

            var nodeStart = MacroWalkTrace.Now;

            NodeStep step;
            try
            {
                step = await ExecuteNodeAsync(macro, node, context, callChain, trace, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (trace.IsTracing)
            {
                // The walk is over either way — this only makes the log say WHICH node ended
                // it, which is the first thing anyone reading the strip wants to know. The
                // filter keeps the whole branch off the path when nobody is watching.
                trace.NodeExited(
                    node.Id,
                    ex is OperationCanceledException ? RunOutcomes.Cancelled : RunOutcomes.Error,
                    ex is OperationCanceledException ? null : ex.Message,
                    nodeStart);
                throw;
            }

            trace.NodeExited(node.Id, step.Outcome, step.Detail, nodeStart);
            currentId = step.Next;
        }

        LogCompleted(macro.Name);
        return MacroRunResult.Completed;
    }

    /// <summary>
    /// Holds the walk before <paramref name="nodeId"/> if the debugger says so.
    ///
    /// Structured as one non-async fast path plus an async slow path so that the ordinary
    /// case — no debugger, or nothing armed — is a volatile read and a returned
    /// <see cref="Task.CompletedTask"/>, with no state machine allocated per node.
    /// </summary>
    private static Task GateAsync(
        MacroRunContext context,
        MacroWalkTrace trace,
        string macroName,
        string nodeId,
        CancellationToken ct)
    {
        if (context.Debugger is not { IsActive: true } debugger)
        {
            return Task.CompletedTask;
        }
        // Breakpoints are keyed by (macro, node) — the same pair the editor sets them with.
        if (debugger.Arm(trace.WalkId, macroName, nodeId) is not { } gate)
        {
            return Task.CompletedTask;
        }
        return WaitAsync(debugger, gate, trace, ct);

        static async Task WaitAsync(IMacroDebugger debugger, MacroDebugGate gate, MacroWalkTrace trace, CancellationToken ct)
        {
            trace.Paused(gate.NodeId, gate.Reason);
            try
            {
                // Cancellation (■ Стоп, daemon shutdown) unparks: the OCE propagates and the
                // walk ends Cancelled, rather than sitting here holding its single-flight slot.
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                debugger.Disarm(gate);
            }
            trace.Resumed(gate.NodeId);
        }
    }

    /// <summary>
    /// What one node did: where to go next, which way it went, and (only when traced) a
    /// line describing it. A readonly struct so an untraced walk allocates nothing per node.
    /// </summary>
    private readonly record struct NodeStep(string? Next, string Outcome, string? Detail = null)
    {
        /// <summary>An action node: exactly one way out.</summary>
        public static NodeStep Done(string? next, string? detail) => new(next, RunOutcomes.Ok, detail);
    }

    private async Task<NodeStep> ExecuteNodeAsync(
        MacroGraph macro,
        MacroNode node,
        MacroRunContext context,
        IReadOnlyList<string> callChain,
        MacroWalkTrace trace,
        CancellationToken ct)
    {
        switch (node)
        {
            case KeyPressNode n:
            {
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.PressKeyAsync(hwnd, n.Key, ct))).ConfigureAwait(false);
                return NodeStep.Done(n.Next, Detail(trace, () => Fanout(n.Key.ToString(), targets.Count)));
            }
            case ClickNode n:
            {
                var point = ResolveClickPoint(n, context.Variables);
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.ClickAsync(hwnd, point, n.DoubleClick, ct))).ConfigureAwait(false);
                return NodeStep.Done(n.Next, Detail(trace, () => Fanout(
                    n.DoubleClick ? $"{point.X},{point.Y} dbl" : $"{point.X},{point.Y}",
                    targets.Count)));
            }
            case DelayNode n:
            {
                if (n.Ms > 0)
                {
                    await Task.Delay(n.Ms, ct).ConfigureAwait(false);
                }
                return NodeStep.Done(n.Next, Detail(trace, () => $"{n.Ms} мс"));
            }
            case AddTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                var targets = ResolveTargets(n.Id, n.Target, context);
                foreach (var hwnd in targets)
                {
                    _windows.AddTag(hwnd, tag);
                }
                return NodeStep.Done(n.Next, Detail(trace, () => Fanout($"+{tag}", targets.Count)));
            }
            case RemoveTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                var targets = ResolveTargets(n.Id, n.Target, context);
                foreach (var hwnd in targets)
                {
                    _windows.RemoveTag(hwnd, tag);
                }
                return NodeStep.Done(n.Next, Detail(trace, () => Fanout($"−{tag}", targets.Count)));
            }
            case SetIconNode n:
            {
                var iconPath = context.Variables.Interpolate(n.IconPath);
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.SetIconAsync(hwnd, iconPath, ct))).ConfigureAwait(false);
                return NodeStep.Done(n.Next, Detail(trace, () => Fanout(FileNameOf(iconPath), targets.Count)));
            }
            case RunMacroNode n:
                return await ExecuteRunMacroAsync(macro, n, context, callChain, trace, ct).ConfigureAwait(false);
            case FindElementNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var found = await _primitives.FindElementAsync(hwnd, n.Template, n.Region, ct).ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        SetVariable(trace, context, n.Id, n.FoundPointVar, point);
                    }
                    return new NodeStep(n.Found, RunOutcomes.Found, Detail(trace, () => $"{n.Template} @ {point.X},{point.Y}"));
                }
                return new NodeStep(n.NotFound, RunOutcomes.NotFound, Detail(trace, () => n.Template));
            }
            case WaitForElementNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var found = await _primitives.WaitForElementAsync(hwnd, n.Template, n.Region, n.TimeoutMs, ct).ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        SetVariable(trace, context, n.Id, n.FoundPointVar, point);
                    }
                    return new NodeStep(n.Found, RunOutcomes.Found, Detail(trace, () => $"{n.Template} @ {point.X},{point.Y}"));
                }
                return new NodeStep(n.Timeout, RunOutcomes.Timeout, Detail(trace, () => $"{n.Template} · лимит {n.TimeoutMs} мс"));
            }
            case RecognizeTagNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var tag = await _primitives.RecognizeAsync(hwnd, n.TemplateSet, n.Region, ct).ConfigureAwait(false);
                if (tag is not null)
                {
                    SetVariable(trace, context, n.Id, n.ResultVar, tag);
                    if (n.ApplyTag)
                    {
                        _windows.AddTag(hwnd, tag);
                    }
                    return new NodeStep(n.Matched, RunOutcomes.Matched, Detail(trace, () => $"{n.TemplateSet} → {tag}"));
                }
                return new NodeStep(n.NotMatched, RunOutcomes.NotMatched, Detail(trace, () => n.TemplateSet));
            }
            default:
                throw new MacroRunAbortException($"Macro '{macro.Name}': node '{node.Id}' has unsupported type {node.GetType().Name}.");
        }
    }

    // The only three places a node writes a variable (spec §5.3). Routed through one helper
    // so the report cannot be forgotten at one of them — the variables panel showing a stale
    // value for {tag} would be indistinguishable from the recognition having failed.
    private static void SetVariable(MacroWalkTrace trace, MacroRunContext context, string nodeId, string name, VariableValue value)
    {
        context.Variables.Set(name, value);
        trace.VariableSet(name, value.DisplayString, nodeId);
    }

    // Detail strings exist only for the log strip, so they are built only when something is
    // reading it. Everything above passes a lambda rather than a string for that reason —
    // the interpolations are the one genuinely per-node allocation this class would
    // otherwise make on every run of every macro, watched or not.
    private static string? Detail(MacroWalkTrace trace, Func<string> build) => trace.IsTracing ? build() : null;

    // "C" for the ordinary one-window case, "C ×7" for a selector fan-out, "C ×0" for a
    // selector that matched nothing — which is a legal no-op and exactly the thing someone
    // reading the log is trying to find out.
    private static string Fanout(string what, int targets) =>
        targets == 1 ? what : string.Create(CultureInfo.InvariantCulture, $"{what} ×{targets}");

    private static string FileNameOf(string path)
    {
        try
        {
            return Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        }
        catch (ArgumentException)
        {
            // An interpolated variable can put anything in here, including invalid path
            // characters. The raw string is a perfectly good log line.
            return path;
        }
    }

    private async Task<NodeStep> ExecuteRunMacroAsync(
        MacroGraph macro,
        RunMacroNode node,
        MacroRunContext context,
        IReadOnlyList<string> callChain,
        MacroWalkTrace trace,
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

        return NodeStep.Done(node.Next, Detail(trace, () => Fanout(
            node.Await ? name : $"{name} (без ожидания)",
            childContexts.Count)));
    }

    private static MacroRunContext BuildChildContext(MacroRunContext parent, IReadOnlyList<string> callChain, IntPtr contextWindow)
    {
        return new MacroRunContext
        {
            ContextWindow = contextWindow,
            Variables = parent.Variables.Clone(),
            Depth = parent.Depth + 1,
            CallChain = callChain,
            RunId = parent.RunId,
            OnNodeEntered = parent.OnNodeEntered,
            // Inherited, not per-child: the observer is a singleton and the CHILD WALK's own
            // identity comes from MacroWalkTrace.Begin inside the child's RunAsync. That is
            // what makes a ten-window fan-out ten separately followable walks that still
            // report one RunId.
            Observer = parent.Observer,
            // Same reasoning: the session is a singleton, the per-walk pause state is keyed
            // by the CHILD's own walk id, so each fork of a fan-out is paused and stepped
            // independently.
            Debugger = parent.Debugger,
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
