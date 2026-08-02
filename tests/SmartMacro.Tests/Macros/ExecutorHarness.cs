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
        Action<string>? onNodeEntered = null)
    {
        return new MacroRunContext
        {
            ContextWindow = window,
            Variables = variables ?? new MacroVariables(),
            OnNodeEntered = onNodeEntered,
        };
    }

    public static MacroGraph Graph(string name, string startId, params MacroNode[] nodes) =>
        new() { Name = name, StartNodeId = startId, Nodes = [.. nodes] };
}
