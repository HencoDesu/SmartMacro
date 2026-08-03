namespace SmartMacro.Macros.Execution;

/// <summary>
/// Per-run state handed to <see cref="MacroExecutor.RunAsync"/>. The trigger layer builds
/// the root context (hotkey → no window, process-appeared → the new window);
/// <see cref="Model.RunMacroNode"/> derives child contexts with copied variables,
/// incremented depth, and the extended call chain.
/// </summary>
public sealed record MacroRunContext
{
    /// <summary>
    /// The window targetless actions and conditional nodes operate on. <c>null</c> for
    /// hotkey-triggered runs — nodes that need a window then abort the run.
    /// </summary>
    public IntPtr? ContextWindow { get; init; }

    /// <summary>Run variables. The trigger layer seeds <c>cursor</c> via <see cref="MacroVariables.ForTrigger"/>.</summary>
    public required MacroVariables Variables { get; init; }

    /// <summary>
    /// The tracked run this context belongs to, from <see cref="MacroRunHandle.RunId"/>.
    /// Carried purely so run events can be tied back to the row in «Прогоны»; the walker
    /// itself never reads it. <see cref="Guid.Empty"/> when the caller runs the executor
    /// outside the registry, which only tests do.
    /// </summary>
    public Guid RunId { get; init; }

    /// <summary>Sub-run nesting depth: 0 for a trigger-initiated run, +1 per <see cref="Model.RunMacroNode"/> level.</summary>
    public int Depth { get; init; }

    /// <summary>Names of ancestor macros (root first), EXCLUDING the macro this context runs. Used for cycle detection.</summary>
    public IReadOnlyList<string> CallChain { get; init; } = [];

    /// <summary>
    /// Progress hook invoked with the node id as the walker enters each node — the run
    /// registry uses it to expose the current node without coupling the executor to it.
    /// </summary>
    public Action<string>? OnNodeEntered { get; init; }

    /// <summary>
    /// Structured progress channel: nodes, outcomes, details, durations. <c>null</c> means
    /// "not instrumented", which is the shape every test that predates D3b has.
    ///
    /// Separate from <see cref="OnNodeEntered"/> rather than replacing it: that hook feeds
    /// the run registry, is one line of state, and fires on the same walk-shared handle for
    /// every sub-walk. This one is per WALK and is what the canvas follows.
    /// </summary>
    public IMacroRunObserver? Observer { get; init; }
}
