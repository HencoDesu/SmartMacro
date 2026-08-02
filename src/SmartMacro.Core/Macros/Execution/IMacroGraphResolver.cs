using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Resolves a macro name to its graph at execution time (<see cref="RunMacroNode"/> looks
/// macros up lazily so libraries can change between runs). W0.2b implements this over the
/// macro store; tests use a dictionary.
/// </summary>
public interface IMacroGraphResolver
{
    /// <summary>The graph registered under <paramref name="name"/>, or <c>null</c> when unknown.</summary>
    MacroGraph? TryGet(string name);
}
