using System.Collections.ObjectModel;
using System.Globalization;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// One heading of the library panel: <c>pw · 6</c>, <c>баг госта · 5</c>, <c>прочее · 11</c>.
/// </summary>
public sealed class MacroLibraryGroupViewModel
{
    internal MacroLibraryGroupViewModel(string prefix, string header, IEnumerable<MacroListItemViewModel> items)
    {
        Prefix = prefix;
        Header = header;
        Items = [.. items];
    }

    /// <summary>The shared name prefix, or <see cref="MacroLibraryGrouping.OtherGroup"/>.</summary>
    public string Prefix { get; }

    /// <summary>Rendered heading, prefix plus count.</summary>
    public string Header { get; }

    /// <summary>Macros in the group, alphabetical.</summary>
    public ObservableCollection<MacroListItemViewModel> Items { get; }
}

/// <summary>
/// The library's grouping rule. Pure UI — nothing on disk knows about it.
///
/// <b>The rule:</b> a macro's prefix is the text before its first <c>-</c>, or the whole
/// name when it has none. Prefixes shared by TWO OR MORE macros become a group, headed by
/// the prefix in lower case and its member count; everything else falls into «прочее».
///
/// The "or the whole name" half is what the mockup demands and is easy to miss: its «баг
/// госта · 5» group contains both <c>Баг госта-Лучник</c> and the plain <c>Баг госта</c>,
/// so a rule that sent every dash-less name straight to «прочее» would split a family in
/// two. The "two or more" half is the other side of the same coin — without it every
/// one-off macro would get a heading of its own and the panel would be all headings.
///
/// Groups are ordered by prefix, with «прочее» pinned last however it sorts.
/// </summary>
public static class MacroLibraryGrouping
{
    /// <summary>Heading for macros whose prefix nobody else shares.</summary>
    public const string OtherGroup = "прочее";

    /// <summary>Text before the first <c>-</c>, or the whole name.</summary>
    public static string PrefixOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash <= 0 ? name : name[..dash];
    }

    /// <summary>Groups a (already ordered) library into headed sections.</summary>
    public static List<MacroLibraryGroupViewModel> Build(IEnumerable<MacroListItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var all = items.ToList();
        var byPrefix = all
            .GroupBy(item => PrefixOf(item.Name), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var groups = new List<MacroLibraryGroupViewModel>();
        var others = new List<MacroListItemViewModel>();

        foreach (var group in byPrefix)
        {
            if (group.Count() < 2)
            {
                others.AddRange(group);
                continue;
            }
            var members = group.OrderBy(item => item.Name, StringComparer.CurrentCulture).ToList();
            groups.Add(new MacroLibraryGroupViewModel(group.Key, Header(group.Key, members.Count), members));
        }

        groups.Sort((left, right) => string.Compare(left.Prefix, right.Prefix, StringComparison.CurrentCultureIgnoreCase));

        if (others.Count > 0)
        {
            others.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCulture));
            groups.Add(new MacroLibraryGroupViewModel(OtherGroup, Header(OtherGroup, others.Count), others));
        }
        return groups;
    }

    private static string Header(string prefix, int count) =>
        string.Create(CultureInfo.CurrentCulture, $"{prefix.ToLowerInvariant()} · {count}");
}
