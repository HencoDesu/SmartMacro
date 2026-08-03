using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Macros;

/// <summary>
/// Hand-rolled recording fake for <see cref="IMacroPrimitives"/> — walker tests assert
/// call sequences, which reads clearer than FakeItEasy ordered-call setup. Vision results
/// are plugged via the *Handler funcs; <see cref="PressKeyGate"/> lets await-semantics
/// tests block a run at a known point.
/// </summary>
internal sealed class RecordingPrimitives : IMacroPrimitives
{
    public sealed record Call(string Op, IntPtr Hwnd, object? A = null, object? B = null);

    private readonly Lock _lock = new();
    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public Func<IntPtr, string, ScreenRect?, ScreenPoint?> FindHandler { get; set; } = (_, _, _) => null;
    public Func<IntPtr, string, ScreenRect?, int, ScreenPoint?> WaitHandler { get; set; } = (_, _, _, _) => null;
    public Func<IntPtr, string, ScreenRect, string?> RecognizeHandler { get; set; } = (_, _, _) => null;

    /// <summary>When set, every PressKeyAsync awaits the returned task after recording the call.</summary>
    public Func<Task>? PressKeyGate { get; set; }

    public async Task PressKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken ct)
    {
        Record(new Call("PressKey", hwnd, key));
        if (PressKeyGate is { } gate)
        {
            await gate();
        }
    }

    public Task ClickAsync(IntPtr hwnd, ScreenPoint point, bool doubleClick, CancellationToken ct)
    {
        Record(new Call("Click", hwnd, point, doubleClick));
        return Task.CompletedTask;
    }

    public Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, string template, ScreenRect? region, CancellationToken ct)
    {
        Record(new Call("Find", hwnd, template));
        return Task.FromResult(FindHandler(hwnd, template, region));
    }

    public Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, string template, ScreenRect? region, int timeoutMs, CancellationToken ct)
    {
        Record(new Call("Wait", hwnd, template, timeoutMs));
        return Task.FromResult(WaitHandler(hwnd, template, region, timeoutMs));
    }

    public Task<string?> RecognizeAsync(IntPtr hwnd, string templateSet, ScreenRect region, CancellationToken ct)
    {
        Record(new Call("Recognize", hwnd, templateSet));
        return Task.FromResult(RecognizeHandler(hwnd, templateSet, region));
    }

    public Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct)
    {
        Record(new Call("SetIcon", hwnd, iconPath));
        return Task.CompletedTask;
    }

    private void Record(Call call)
    {
        lock (_lock)
        {
            _calls.Add(call);
        }
    }
}

/// <summary>Dictionary-backed <see cref="IMacroGraphResolver"/> for sub-macro tests.</summary>
internal sealed class DictionaryResolver : IMacroGraphResolver
{
    private readonly Dictionary<string, MacroGraph> _graphs = new(StringComparer.Ordinal);

    public void Add(MacroGraph graph) => _graphs[graph.Name] = graph;

    public MacroGraph? TryGet(string name) => _graphs.GetValueOrDefault(name);
}

/// <summary>One executor wired to a real WindowRegistry, a recording primitives fake, and a dictionary resolver.</summary>
internal sealed class ExecutorHarness
{
    /// <summary>Default context window used by tests.</summary>
    public static readonly IntPtr Window = new(0xA);

    public WindowRegistry Registry { get; } = new(NullLogger<WindowRegistry>.Instance);
    public RecordingPrimitives Primitives { get; } = new();
    public DictionaryResolver Resolver { get; } = new();
    public MacroExecutor Executor { get; }

    public ExecutorHarness()
    {
        Executor = new MacroExecutor(Primitives, Registry, Resolver, NullLogger<MacroExecutor>.Instance);
    }

    public MacroRunContext Context(
        IntPtr? window = null,
        MacroVariables? variables = null,
        Action<string>? onNodeEntered = null,
        IMacroRunObserver? observer = null,
        Guid runId = default,
        IMacroDebugger? debugger = null)
    {
        return new MacroRunContext
        {
            ContextWindow = window,
            Variables = variables ?? new MacroVariables(),
            OnNodeEntered = onNodeEntered,
            Observer = observer,
            RunId = runId,
            Debugger = debugger,
        };
    }

    public static MacroGraph Graph(string name, string startId, params MacroNode[] nodes) =>
        new() { Name = name, StartNodeId = startId, Nodes = [.. nodes] };
}

/// <summary>
/// Recording <see cref="IMacroRunObserver"/> for the tracing tests.
///
/// <see cref="IsEnabled"/> is settable because the flag is the whole flooding mitigation:
/// the walker is supposed to skip timing and detail formatting when it is off, and the only
/// way to assert that is to turn it off and check that nothing arrives.
/// </summary>
internal sealed class RecordingObserver : IMacroRunObserver
{
    /// <summary>One reported event, flattened.</summary>
    /// <param name="Kind">walk-start / enter / exit / walk-end.</param>
    /// <param name="WalkId">Which walk it belongs to.</param>
    /// <param name="NodeId">Node, or <c>null</c> for the walk-level kinds.</param>
    /// <param name="Outcome">A <c>RunOutcomes</c> constant, or <c>null</c>.</param>
    /// <param name="Detail">The log line, or <c>null</c>.</param>
    internal sealed record Entry(string Kind, Guid WalkId, string? NodeId, string? Outcome, string? Detail);

    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];
    private readonly List<MacroWalkStart> _walks = [];

    public bool IsEnabled { get; set; } = true;

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Every walk that was announced, in order.</summary>
    public IReadOnlyList<MacroWalkStart> Walks
    {
        get
        {
            lock (_lock)
            {
                return [.. _walks];
            }
        }
    }

    /// <summary>Entries of one kind, in order.</summary>
    public IReadOnlyList<Entry> OfKind(string kind) => [.. Entries.Where(e => e.Kind == kind)];

    public void WalkStarted(MacroWalkStart walk)
    {
        lock (_lock)
        {
            _walks.Add(walk);
            _entries.Add(new Entry("walk", walk.WalkId, null, null, walk.MacroName));
        }
    }

    public void NodeEntered(Guid walkId, int elapsedMs, string nodeId) =>
        Add(new Entry("enter", walkId, nodeId, null, null));

    public void NodeExited(Guid walkId, int elapsedMs, string nodeId, string outcome, string? detail, int durationMs) =>
        Add(new Entry("exit", walkId, nodeId, outcome, detail));

    public void WalkFinished(Guid walkId, int elapsedMs, string outcome, string? detail) =>
        Add(new Entry("end", walkId, null, outcome, detail));

    // ---- D5 -----------------------------------------------------------------------------
    // Flattened into the same Entry stream so the ORDER of a pause relative to its node's
    // enter/exit is assertable — which is the whole safety property (park between nodes,
    // never inside one).

    /// <summary>Kind string of a <c>VariableSet</c>; <c>NodeId</c> is the writer, <c>Outcome</c> the variable name, <c>Detail</c> the value.</summary>
    public const string VariableKind = "var";

    /// <summary>Kind string of a pause; <c>Outcome</c> carries the <see cref="DebugPauseReason"/>.</summary>
    public const string PausedKind = "paused";

    /// <summary>Kind string of a resume.</summary>
    public const string ResumedKind = "resumed";

    public void VariableSet(Guid walkId, int elapsedMs, string name, string value, string? nodeId) =>
        Add(new Entry(VariableKind, walkId, nodeId, name, value));

    public void WalkPaused(Guid walkId, int elapsedMs, string nodeId, DebugPauseReason reason) =>
        Add(new Entry(PausedKind, walkId, nodeId, reason.ToString(), null));

    public void WalkResumed(Guid walkId, int elapsedMs, string nodeId) =>
        Add(new Entry(ResumedKind, walkId, nodeId, null, null));

    private void Add(Entry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }
}
