using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Model;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>One tag of a selector's require/exclude list, as a removable chip (mockup 1g).</summary>
public sealed class SelectorTagChipViewModel
{
    private readonly Action<string> _remove;

    internal SelectorTagChipViewModel(string text, Action<string> remove)
    {
        Text = text;
        _remove = remove;
    }

    /// <summary>The tag itself.</summary>
    public string Text { get; }

    /// <summary>Drops this tag from the list it belongs to.</summary>
    public void Remove() => _remove(Text);
}

/// <summary>One window the badge names in its expanded form.</summary>
/// <param name="Label">"0x1402F8 Лучник" — the handle plus whatever tags it carries.</param>
/// <param name="IsExcluded">Rendered struck through: the selector deliberately skips it.</param>
public sealed record TargetWindowChip(string Label, bool IsExcluded);

/// <summary>
/// Editor for a node's <see cref="TargetSelector"/>: two comma-separated tag lists, plus
/// (since D4) the live «8 окон · кроме Склад» badge over them.
///
/// <see cref="UseSelector"/> is what distinguishes the two things a null-vs-empty selector
/// means in the model, which the tag boxes alone cannot express:
///   * off — <c>Target = null</c>: act on the run's CONTEXT window;
///   * on with both boxes empty — <c>Target = new TargetSelector()</c>: fan out to EVERY
///     registered window.
/// Without the flag those two collapse into each other and a graph would not survive a
/// load/save round-trip.
///
/// <b>The badge is computed here, against the same rule the engine uses.</b>
/// <see cref="TargetSelector.Matches"/> lives in Contracts precisely so this class and the
/// daemon's <c>SelectorEvaluator</c> cannot answer "which windows does this hit" differently
/// — a badge that disagrees with the executor would be worse than no badge, since saying
/// what a run will hit is the entire point of it. The window list comes from
/// <see cref="Windows"/>, the editor's live catalogue; with no catalogue attached (unit
/// tests of the round-trip, a panel that has not seeded yet) the badge reports "нет окон"
/// rather than inventing a number.
/// </summary>
public sealed class TargetSelectorViewModel : ObservableObject
{
    /// <summary>Windows named individually before the badge collapses the rest into "+N".</summary>
    private const int MaxNamedWindows = 4;

    private bool _useSelector;
    private string _requireText = string.Empty;
    private string _excludeText = string.Empty;
    private string _newRequireTag = string.Empty;
    private string _newExcludeTag = string.Empty;
    private WindowCatalog? _windows;

    /// <summary><c>false</c> = act on the context window (<c>Target = null</c>).</summary>
    public bool UseSelector
    {
        get => _useSelector;
        set => SetField(ref _useSelector, value);
    }

    /// <summary>Comma-separated tags a window must all carry.</summary>
    public string RequireText
    {
        get => _requireText;
        set
        {
            if (SetField(ref _requireText, value ?? string.Empty))
            {
                RebuildChips();
            }
        }
    }

    /// <summary>Comma-separated tags that disqualify a window.</summary>
    public string ExcludeText
    {
        get => _excludeText;
        set
        {
            if (SetField(ref _excludeText, value ?? string.Empty))
            {
                RebuildChips();
            }
        }
    }

    /// <summary>
    /// The editor's live window snapshot, shared by every selector of the open graph.
    /// Assigned by <c>MacroEditorViewModel</c> when it attaches a node row; <c>null</c> in
    /// isolation, which the badge reports honestly.
    /// </summary>
    public WindowCatalog? Windows
    {
        get => _windows;
        set
        {
            if (ReferenceEquals(_windows, value))
            {
                return;
            }
            if (_windows is not null)
            {
                _windows.Changed -= OnWindowsChanged;
            }
            _windows = value;
            if (_windows is not null)
            {
                _windows.Changed += OnWindowsChanged;
            }
            OnWindowsChanged();
        }
    }

    // ---- selector text (what the node stores) -----------------------------------------

    /// <summary>
    /// What the SELECTOR says, with no window count in it: «кроме Склад», «Лучник»,
    /// «все окна». The badge puts the live count in front of this.
    /// </summary>
    public string Summary
    {
        get
        {
            if (!_useSelector)
            {
                return "контекст-окно";
            }
            var require = _requireText.Trim();
            var exclude = _excludeText.Trim();
            return (require.Length, exclude.Length) switch
            {
                (0, 0) => "все окна",
                (_, 0) => require,
                (0, _) => $"кроме {exclude}",
                _ => $"{require} · кроме {exclude}",
            };
        }
    }

    // ---- badge (mockup 1g) --------------------------------------------------------------

    /// <summary>
    /// The collapsed badge: «8 окон · кроме Склад». The number leads because it is the
    /// question the badge exists to answer — the tags are already in the inspector below it.
    /// </summary>
    public string BadgeText
    {
        get
        {
            if (!_useSelector)
            {
                return "1 окно · контекст";
            }
            if (TotalCount == 0)
            {
                return "нет окон";
            }
            var count = string.Create(CultureInfo.CurrentCulture, $"{MatchCount} {Plural(MatchCount)}");
            var require = _requireText.Trim();
            var exclude = _excludeText.Trim();
            return (require.Length, exclude.Length) switch
            {
                (0, 0) => count,
                (_, 0) => $"{count} · {require}",
                (0, _) => $"{count} · кроме {exclude}",
                _ => $"{count} · {require} · кроме {exclude}",
            };
        }
    }

    /// <summary>
    /// The badge with the tags stripped off: «7 окон», «контекст», «нет окон».
    ///
    /// What the canvas box shows. A node box is 210px wide and already carries a type label
    /// and a family glyph; the full «7 окон · кроме Лучник, Шаман» crowds the label out of
    /// existence, and the label is what tells you what the node DOES. The count is the
    /// headline the badge exists for, the rest is one hover (or one click on the box) away.
    /// </summary>
    public string BadgeCountText
    {
        get
        {
            if (!_useSelector)
            {
                return "контекст";
            }
            if (TotalCount == 0)
            {
                return "нет окон";
            }
            return string.Create(CultureInfo.CurrentCulture, $"{MatchCount} {Plural(MatchCount)}");
        }
    }

    /// <summary>Windows the selector hits right now. Meaningless while <see cref="UseSelector"/> is off.</summary>
    public int MatchCount { get; private set; }

    /// <summary>Windows the daemon is tracking at all.</summary>
    public int TotalCount => _windows?.Count ?? 0;

    /// <summary>
    /// Accent badge: the selector routes by tags and hits something. Both this and
    /// <see cref="BadgeIsDanger"/> are false for the context-window case, which is the
    /// neutral pill.
    /// </summary>
    public bool BadgeIsAccent => _useSelector && TotalCount > 0 && MatchCount > 0;

    /// <summary>
    /// Zero matches, rendered as an error — the mockup is explicit that «0 окон» must not
    /// read as a neutral number.
    ///
    /// It requires windows to exist. With the game closed EVERY selector matches nothing,
    /// and painting every node of every macro red because nothing is running would train the
    /// user to ignore the colour that is supposed to mean "this selector is wrong". That
    /// case gets its own quiet «нет окон» instead.
    /// </summary>
    public bool BadgeIsDanger => _useSelector && TotalCount > 0 && MatchCount == 0;

    /// <summary>The four share bars. <c>true</c> = filled; a non-zero share always fills at least one.</summary>
    public IReadOnlyList<bool> Bars { get; private set; } = [false, false, false, false];

    /// <summary>Bars are drawn only when there is a share to show — see <see cref="ShowsHollowDot"/>.</summary>
    public bool ShowsBars => _useSelector && TotalCount > 0 && MatchCount > 0;

    /// <summary>The zero-match marker: a filled danger dot where the share bars would be.</summary>
    public bool ShowsDangerDot => BadgeIsDanger;

    /// <summary>
    /// The quiet marker: an outline ring. Context-window routing and "the daemon is tracking
    /// nothing" both land here — neither is an error, and neither has a share to draw.
    /// </summary>
    public bool ShowsHollowDot => !ShowsBars && !BadgeIsDanger;

    /// <summary>«8 из 11» in the expanded popup's footer.</summary>
    public string HitText => TotalCount == 0
        ? "нет окон под управлением"
        : string.Create(CultureInfo.CurrentCulture, $"{MatchCount} из {TotalCount}");

    /// <summary>Up to <see cref="MaxNamedWindows"/> windows the run will hit, by handle and tags.</summary>
    public ObservableCollection<TargetWindowChip> HitWindows { get; } = [];

    /// <summary>Tagged windows the selector skips, struck through in the popup.</summary>
    public ObservableCollection<TargetWindowChip> MissedWindows { get; } = [];

    /// <summary>«+4» when more windows match than the popup names. Empty otherwise.</summary>
    public string MoreText { get; private set; } = string.Empty;

    /// <summary><c>true</c> when <see cref="MoreText"/> has something to show.</summary>
    public bool HasMore => MoreText.Length > 0;

    /// <summary>«2 без тегов» — untagged windows the selector cannot reach. Empty when there are none.</summary>
    public string UntaggedText { get; private set; } = string.Empty;

    /// <summary><c>true</c> when <see cref="UntaggedText"/> has something to show.</summary>
    public bool HasUntagged => UntaggedText.Length > 0;

    // ---- tag chips (the popup's editor) -------------------------------------------------

    /// <summary>Required tags as removable chips. Mirrors <see cref="RequireText"/>.</summary>
    public ObservableCollection<SelectorTagChipViewModel> RequireChips { get; } = [];

    /// <summary>Excluded tags as removable chips. Mirrors <see cref="ExcludeText"/>.</summary>
    public ObservableCollection<SelectorTagChipViewModel> ExcludeChips { get; } = [];

    /// <summary>Text of the popup's "+ тег" box for the require list.</summary>
    public string NewRequireTag
    {
        get => _newRequireTag;
        set => SetField(ref _newRequireTag, value ?? string.Empty);
    }

    /// <summary>Text of the popup's "+ тег" box for the exclude list.</summary>
    public string NewExcludeTag
    {
        get => _newExcludeTag;
        set => SetField(ref _newExcludeTag, value ?? string.Empty);
    }

    /// <summary>Commits <see cref="NewRequireTag"/> (the box's Enter key). Duplicates and blanks are ignored.</summary>
    public void CommitRequireTag()
    {
        if (AddTag(ref _requireText, _newRequireTag))
        {
            NewRequireTag = string.Empty;
            OnPropertyChanged(nameof(RequireText));
            RebuildChips();
        }
    }

    /// <summary>Commits <see cref="NewExcludeTag"/> (the box's Enter key).</summary>
    public void CommitExcludeTag()
    {
        if (AddTag(ref _excludeText, _newExcludeTag))
        {
            NewExcludeTag = string.Empty;
            OnPropertyChanged(nameof(ExcludeText));
            RebuildChips();
        }
    }

    // ---- model round trip ----------------------------------------------------------------

    /// <summary>Builds the model selector, or <c>null</c> when targeting the context window.</summary>
    public TargetSelector? ToSelector() => _useSelector
        ? new TargetSelector
        {
            RequireTags = SplitTags(_requireText),
            ExcludeTags = SplitTags(_excludeText),
        }
        : null;

    /// <summary>Loads a model selector (<c>null</c> = context window).</summary>
    public static TargetSelectorViewModel FromSelector(TargetSelector? selector)
    {
        var vm = new TargetSelectorViewModel
        {
            UseSelector = selector is not null,
            RequireText = JoinTags(selector?.RequireTags),
            ExcludeText = JoinTags(selector?.ExcludeTags),
        };
        vm.RebuildChips();
        return vm;
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        // Every field of this object feeds the summary and the badge, so rather than
        // hand-raising in each setter (and forgetting one), anything that is not itself a
        // derived property re-raises the derived set.
        if (propertyName is null || IsDerived(propertyName))
        {
            return;
        }
        base.OnPropertyChanged(nameof(Summary));
        Recompute();
    }

    private static bool IsDerived(string propertyName) => propertyName is
        nameof(Summary) or nameof(BadgeText) or nameof(BadgeCountText)
        or nameof(MatchCount) or nameof(TotalCount)
        or nameof(BadgeIsAccent) or nameof(BadgeIsDanger) or nameof(Bars) or nameof(ShowsBars)
        or nameof(ShowsDangerDot) or nameof(ShowsHollowDot)
        or nameof(HitText) or nameof(MoreText) or nameof(HasMore)
        or nameof(UntaggedText) or nameof(HasUntagged)
        or nameof(NewRequireTag) or nameof(NewExcludeTag);

    private void OnWindowsChanged() => Recompute();

    /// <summary>
    /// Re-evaluates the selector against the catalogue. Cheap by construction — ten windows
    /// and a handful of tags — so it runs on every keystroke in the tag boxes rather than
    /// being debounced, which is what makes the count feel attached to what is being typed.
    /// </summary>
    private void Recompute()
    {
        var windows = _windows?.Windows ?? [];
        var selector = _useSelector ? ToSelector() : null;

        var hit = new List<WindowDto>();
        var missed = new List<WindowDto>();
        var untaggedMisses = 0;

        foreach (var window in windows)
        {
            if (selector is not null && selector.Matches(window.Tags))
            {
                hit.Add(window);
            }
            else if (window.Tags.Count == 0)
            {
                untaggedMisses++;
            }
            else
            {
                missed.Add(window);
            }
        }

        MatchCount = hit.Count;
        Bars = BuildBars(hit.Count, windows.Count);

        Replace(HitWindows, hit.Take(MaxNamedWindows).Select(w => new TargetWindowChip(Describe(w), false)));
        Replace(MissedWindows, missed.Take(MaxNamedWindows).Select(w => new TargetWindowChip(Describe(w), true)));

        var overflow = hit.Count - MaxNamedWindows;
        MoreText = overflow > 0
            ? string.Create(CultureInfo.InvariantCulture, $"+{overflow}")
            : string.Empty;
        UntaggedText = untaggedMisses > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{untaggedMisses} без тегов")
            : string.Empty;

        base.OnPropertyChanged(nameof(BadgeText));
        base.OnPropertyChanged(nameof(BadgeCountText));
        base.OnPropertyChanged(nameof(MatchCount));
        base.OnPropertyChanged(nameof(TotalCount));
        base.OnPropertyChanged(nameof(BadgeIsAccent));
        base.OnPropertyChanged(nameof(BadgeIsDanger));
        base.OnPropertyChanged(nameof(Bars));
        base.OnPropertyChanged(nameof(ShowsBars));
        base.OnPropertyChanged(nameof(ShowsDangerDot));
        base.OnPropertyChanged(nameof(ShowsHollowDot));
        base.OnPropertyChanged(nameof(HitText));
        base.OnPropertyChanged(nameof(MoreText));
        base.OnPropertyChanged(nameof(HasMore));
        base.OnPropertyChanged(nameof(UntaggedText));
        base.OnPropertyChanged(nameof(HasUntagged));
    }

    // Four bars, mockup-exact: 8 of 11 fills three, 1 of 11 fills one. Rounding alone would
    // leave a single match showing an empty strip, which reads as "nothing" — so any
    // non-zero share is worth at least one bar.
    private static IReadOnlyList<bool> BuildBars(int matched, int total)
    {
        var filled = total <= 0 || matched <= 0
            ? 0
            : Math.Clamp((int)Math.Round(matched * 4.0 / total, MidpointRounding.AwayFromZero), 1, 4);
        return [filled > 0, filled > 1, filled > 2, filled > 3];
    }

    private static string Describe(WindowDto window) => window.Tags.Count == 0
        ? string.Create(CultureInfo.InvariantCulture, $"0x{window.Hwnd:X}")
        : string.Create(CultureInfo.InvariantCulture, $"0x{window.Hwnd:X} {string.Join(' ', window.Tags)}");

    private void RebuildChips()
    {
        Rebuild(RequireChips, SplitTags(_requireText), RemoveRequireTag);
        Rebuild(ExcludeChips, SplitTags(_excludeText), RemoveExcludeTag);
    }

    private void RemoveRequireTag(string tag)
    {
        RequireText = string.Join(", ", SplitTags(_requireText).Where(t => !string.Equals(t, tag, StringComparison.Ordinal)));
    }

    private void RemoveExcludeTag(string tag)
    {
        ExcludeText = string.Join(", ", SplitTags(_excludeText).Where(t => !string.Equals(t, tag, StringComparison.Ordinal)));
    }

    private static bool AddTag(ref string list, string candidate)
    {
        var tag = candidate.Trim();
        if (tag.Length == 0)
        {
            return false;
        }
        var tags = SplitTags(list);
        if (tags.Contains(tag, StringComparer.Ordinal))
        {
            return false;
        }
        tags.Add(tag);
        list = string.Join(", ", tags);
        return true;
    }

    private static void Rebuild(
        ObservableCollection<SelectorTagChipViewModel> target,
        IReadOnlyList<string> tags,
        Action<string> remove)
    {
        target.Clear();
        foreach (var tag in tags)
        {
            target.Add(new SelectorTagChipViewModel(tag, remove));
        }
    }

    private static void Replace(ObservableCollection<TargetWindowChip> target, IEnumerable<TargetWindowChip> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    // Tags containing a comma cannot be expressed here. They are class names and other
    // short labels in practice, so the trade-off buys a one-line editor for the common case.
    private static List<string> SplitTags(string text) =>
    [
        .. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ];

    private static string JoinTags(IReadOnlyList<string>? tags) =>
        tags is null || tags.Count == 0 ? string.Empty : string.Join(", ", tags);

    // окно / окна / окон. Written out rather than pulled from a pluralisation library
    // because it is one word and the UI is Russian-only.
    private static string Plural(int count)
    {
        var mod100 = count % 100;
        if (mod100 is >= 11 and <= 14)
        {
            return "окон";
        }
        return (count % 10) switch
        {
            1 => "окно",
            2 or 3 or 4 => "окна",
            _ => "окон",
        };
    }
}
