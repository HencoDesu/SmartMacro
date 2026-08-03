using System.Text.RegularExpressions;

namespace SmartMacro.Macros.Model;

/// <summary>
/// The two facts about run variables that BOTH processes have to agree on: the name the
/// trigger always seeds, and the syntax of a <c>{name}</c> placeholder.
///
/// They live in Contracts because the daemon's <c>MacroVariables</c> (which substitutes) and
/// the panel's variables panel (which reports who reads what) must not each carry their own
/// copy. A regex that drifted by one character would make the panel claim a node reads a
/// variable the executor never substitutes — the exact class of lie wave D4 refused to allow
/// for the targets badge.
/// </summary>
public static partial class MacroVariableNames
{
    /// <summary>
    /// The variable every trigger seeds with the cursor position at fire time — hotkey,
    /// process-appeared and UI Run alike. Nothing in a graph writes it.
    /// </summary>
    public const string Cursor = "cursor";

    /// <summary>
    /// <c>{name}</c>, with the name in group 1.
    ///
    /// <b>There is no escaping.</b> A literal <c>{</c> cannot be written, and that is a
    /// deliberate v1 ceiling (spec §5.3) rather than an oversight — anything that needs one
    /// needs expressions, which is ScriptNode territory.
    /// </summary>
    [GeneratedRegex(@"\{([^{}]+)\}")]
    public static partial Regex Placeholder();
}
