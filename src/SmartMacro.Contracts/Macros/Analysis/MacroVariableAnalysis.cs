using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Analysis;

/// <summary>
/// The field a variable is written to or read from. Symbolic, never Russian — same rule as
/// <c>RunOutcomes</c>: the daemon and the analyser name the slot, the panel owns the wording
/// («в пути к иконке») and the colour.
/// </summary>
public enum VariableSlot
{
    /// <summary>Written: the match centre of a <c>FindElement</c> / <c>WaitForElement</c>.</summary>
    FoundPointVar,

    /// <summary>Written: the winning template name of a <c>RecognizeTag</c>.</summary>
    ResultVar,

    /// <summary>Read: <c>ClickNode.PointVar</c>, the only non-interpolating read in the model.</summary>
    PointVar,

    /// <summary>Read: <c>{var}</c> inside <c>AddTag</c> / <c>RemoveTag</c>'s tag.</summary>
    Tag,

    /// <summary>Read: <c>{var}</c> inside <c>SetIcon</c>'s path.</summary>
    IconPath,

    /// <summary>Read: <c>{var}</c> inside <c>RunMacro</c>'s macro name.</summary>
    MacroName,
}

/// <summary>What a variable holds, as far as the graph alone can tell.</summary>
public enum VariableKind
{
    /// <summary>Nothing in the graph pins it down: it is only ever interpolated into a string.</summary>
    Unknown,

    /// <summary>A screen point — <c>cursor</c>, a <c>FoundPointVar</c>, or something a <c>PointVar</c> reads.</summary>
    Point,

    /// <summary>A string — the tag a <c>RecognizeTag</c> wrote.</summary>
    Text,
}

/// <summary>One place a variable is touched.</summary>
/// <param name="NodeId">Node doing the touching.</param>
/// <param name="Slot">Which field of that node.</param>
public sealed record VariableReference(string NodeId, VariableSlot Slot);

/// <summary>
/// Everything the graph says about one variable: who writes it, who reads it, what it holds.
/// </summary>
/// <param name="Name">Variable name, as written between the braces. Case-sensitive.</param>
/// <param name="Kind">Inferred from the WRITE when there is one, otherwise from the reads.</param>
/// <param name="SeededByTrigger">
/// <c>true</c> only for <see cref="MacroVariableNames.Cursor"/> — the one variable no node
/// writes and every run has. The panel renders it as «пишет: триггер (сид)».
/// </param>
/// <param name="Writes">Nodes that assign it, in graph order. Empty for the trigger seed.</param>
/// <param name="Reads">Nodes that consume it, in graph order.</param>
public sealed record MacroVariableInfo(
    string Name,
    VariableKind Kind,
    bool SeededByTrigger,
    IReadOnlyList<VariableReference> Writes,
    IReadOnlyList<VariableReference> Reads)
{
    /// <summary>
    /// <c>false</c> when a node reads a variable nothing ever assigns — at run time that is
    /// an aborted run, not a blank substitution (spec §5.3).
    /// </summary>
    public bool IsDefined => SeededByTrigger || Writes.Count > 0;

    /// <summary><c>false</c> for a variable written and never used.</summary>
    public bool IsRead => Reads.Count > 0;
}

/// <summary>
/// Static "who writes / who reads" over a macro graph — the data behind the variables panel
/// of mockup 1d.
///
/// <b>Pure, and deliberately in Contracts.</b> It needs the model and nothing else: no
/// registry, no file IO, no running walk. That makes it testable on its own (which is where
/// its bugs would be) and usable by the panel, which has no reference to Core and never will.
/// The live VALUE of a variable is a separate concern and arrives over the run-event stream
/// as <c>RunEventKind.VariableSet</c>; this class only knows the shape of the graph.
///
/// <b>The write set is closed and tiny by design</b> (spec §5.3, §14): the trigger seeds
/// <c>cursor</c>, and conditional nodes write <c>FoundPointVar</c> / <c>ResultVar</c>. There
/// is no <c>SetVariableNode</c> and there will not be one, so this analysis cannot go stale
/// by missing a writer that someone added later — a new writer would be a new node type,
/// which lands in <see cref="Collect"/>'s switch as a compile-time-visible gap.
/// </summary>
public static class MacroVariableAnalysis
{
    /// <summary>
    /// Analyses one graph.
    ///
    /// Always contains <see cref="MacroVariableNames.Cursor"/>, whether or not the graph
    /// mentions it: every run has one, and a panel that only listed mentioned variables would
    /// hide the single value that is always available to click into a node.
    /// </summary>
    /// <returns>
    /// One entry per variable. Ordered so the ones a node WRITES come first, in the order
    /// their writer appears in <see cref="MacroGraph.Nodes"/>, then the read-only ones —
    /// which puts the interesting one at the top and <c>cursor</c> at the bottom for the
    /// typical identify-and-tag graph.
    /// </returns>
    public static IReadOnlyList<MacroVariableInfo> Analyze(MacroGraph macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var found = new Dictionary<string, Entry>(StringComparer.Ordinal);

        for (var index = 0; index < macro.Nodes.Count; index++)
        {
            Collect(macro.Nodes[index], index, found);
        }

        // The trigger seed. Added last so a graph that also READS it has already recorded
        // the read (and its ordinal) — this only supplies the "written by the trigger" half.
        var cursor = Get(found, MacroVariableNames.Cursor, order: macro.Nodes.Count);
        cursor.SeededByTrigger = true;
        cursor.Kind = VariableKind.Point;

        return
        [
            .. found.Values
                .OrderBy(entry => entry.Writes.Count > 0 ? 0 : 1)
                .ThenBy(entry => entry.Order)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => new MacroVariableInfo(
                    entry.Name,
                    entry.Kind,
                    entry.SeededByTrigger,
                    entry.Writes,
                    entry.Reads))
        ];
    }

    /// <summary>
    /// Variable names interpolated into <paramref name="template"/>, in order, without
    /// duplicates. Uses the SAME regex the executor substitutes with — see
    /// <see cref="MacroVariableNames.Placeholder"/>.
    /// </summary>
    public static IReadOnlyList<string> PlaceholdersIn(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return [];
        }

        List<string>? names = null;
        foreach (var match in MacroVariableNames.Placeholder().EnumerateMatches(template))
        {
            // EnumerateMatches gives no groups, so the name is the span minus the braces.
            var name = template.Substring(match.Index + 1, match.Length - 2);
            names ??= [];
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }
        return names ?? (IReadOnlyList<string>)[];
    }

    private static void Collect(MacroNode node, int order, Dictionary<string, Entry> found)
    {
        switch (node)
        {
            // ---- writers: the closed set of spec §5.3 -----------------------------------

            case FindElementNode n:
                Write(found, n.FoundPointVar, node.Id, VariableSlot.FoundPointVar, VariableKind.Point, order);
                break;

            case WaitForElementNode n:
                Write(found, n.FoundPointVar, node.Id, VariableSlot.FoundPointVar, VariableKind.Point, order);
                break;

            case RecognizeTagNode n:
                Write(found, n.ResultVar, node.Id, VariableSlot.ResultVar, VariableKind.Text, order);
                break;

            // ---- readers ----------------------------------------------------------------

            case ClickNode n:
                // The only read that names a variable outright rather than interpolating it.
                Read(found, n.PointVar, node.Id, VariableSlot.PointVar, VariableKind.Point, order);
                break;

            case AddTagNode n:
                Interpolated(found, n.Tag, node.Id, VariableSlot.Tag, order);
                break;

            case RemoveTagNode n:
                Interpolated(found, n.Tag, node.Id, VariableSlot.Tag, order);
                break;

            case SetIconNode n:
                Interpolated(found, n.IconPath, node.Id, VariableSlot.IconPath, order);
                break;

            case RunMacroNode n:
                Interpolated(found, n.MacroName, node.Id, VariableSlot.MacroName, order);
                break;

            default:
                // KeyPressNode and DelayNode touch no variables. A NEW node type lands here
                // silently, which is the one thing to remember when the catalogue grows.
                break;
        }
    }

    private static void Interpolated(
        Dictionary<string, Entry> found,
        string? template,
        string nodeId,
        VariableSlot slot,
        int order)
    {
        foreach (var name in PlaceholdersIn(template))
        {
            // Interpolation says nothing about the type — everything has a DisplayString.
            Read(found, name, nodeId, slot, VariableKind.Unknown, order);
        }
    }

    private static void Write(
        Dictionary<string, Entry> found,
        string? name,
        string nodeId,
        VariableSlot slot,
        VariableKind kind,
        int order)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var entry = Get(found, name, order);
        entry.Writes.Add(new VariableReference(nodeId, slot));
        // A write is authoritative about the type; a read only guesses.
        entry.Kind = kind;
    }

    private static void Read(
        Dictionary<string, Entry> found,
        string? name,
        string nodeId,
        VariableSlot slot,
        VariableKind kind,
        int order)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var entry = Get(found, name, order);
        entry.Reads.Add(new VariableReference(nodeId, slot));
        if (entry.Kind == VariableKind.Unknown && entry.Writes.Count == 0)
        {
            entry.Kind = kind;
        }
    }

    private static Entry Get(Dictionary<string, Entry> found, string name, int order)
    {
        if (!found.TryGetValue(name, out var entry))
        {
            entry = new Entry(name, order);
            found[name] = entry;
        }
        return entry;
    }

    // Mutable while collecting; projected to the immutable record at the end.
    private sealed class Entry(string name, int order)
    {
        public string Name { get; } = name;

        /// <summary>Index of the node that first mentioned it — the display order.</summary>
        public int Order { get; } = order;

        public VariableKind Kind { get; set; } = VariableKind.Unknown;

        public bool SeededByTrigger { get; set; }

        public List<VariableReference> Writes { get; } = [];

        public List<VariableReference> Reads { get; } = [];
    }
}
