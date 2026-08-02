using System.Collections.ObjectModel;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Windows;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// One tag on a window, rendered as a removable chip. Carries a reference back to its row
/// so the chip's <c>×</c> button has somewhere to go without the view having to correlate
/// two data contexts.
/// </summary>
public sealed class TagChipViewModel
{
    private readonly WindowRowViewModel _owner;

    public TagChipViewModel(WindowRowViewModel owner, string text)
    {
        _owner = owner;
        Text = text;
    }

    /// <summary>The tag itself. Free-form, case-sensitive, usually Cyrillic.</summary>
    public string Text { get; }

    /// <summary>Removes this tag from the window through the registry.</summary>
    public void Remove() => _owner.RemoveTag(Text);
}

/// <summary>
/// One tracked window in the main list: which process it belongs to, its handle, and its
/// live tag set with add/remove affordances.
///
/// The row owns no tag state of its own — <see cref="WindowRegistry"/> is the sole owner,
/// so <see cref="AddTag"/>/<see cref="RemoveTag"/> call straight through and the visible
/// chips are refreshed by <see cref="ApplyTags"/> when the registry's
/// <c>WindowTagsChanged</c> event comes back. That keeps the display honest even when the
/// change originated from a macro node rather than from this row.
/// </summary>
public sealed class WindowRowViewModel : ObservableObject
{
    private readonly WindowRegistry _registry;
    private string _newTagText = string.Empty;

    public WindowRowViewModel(WindowRegistry registry, ManagedWindowInfo info)
    {
        _registry = registry;
        Hwnd = info.Hwnd;
        ProcessName = info.ProcessName;
        ApplyTags(info.Tags);
    }

    /// <summary>Native handle — identity of the row, matching the registry's key.</summary>
    public IntPtr Hwnd { get; }

    /// <summary>Owning process name, as reported by ProcessMonitor.</summary>
    public string ProcessName { get; }

    /// <summary>Handle in the hex form the logs use, so a row can be matched to a log line.</summary>
    public string HwndHex => string.Create(CultureInfo.InvariantCulture, $"0x{Hwnd.ToInt64():X}");

    /// <summary>Live chips for the window's tags, in registry (insertion) order.</summary>
    public ObservableCollection<TagChipViewModel> Tags { get; } = [];

    /// <summary>Text of the row's "add a tag" box.</summary>
    public string NewTagText
    {
        get => _newTagText;
        set => SetField(ref _newTagText, value);
    }

    /// <summary>An untagged window is the "not identified yet" case — the view greys the row.</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>
    /// Adds whatever is typed in <see cref="NewTagText"/> to the window. The box is only
    /// cleared when the tag actually landed, so a duplicate stays visible for the user to
    /// correct instead of vanishing silently.
    /// </summary>
    /// <returns><c>true</c> when the registry's tag set changed.</returns>
    public bool AddTag()
    {
        var tag = _newTagText.Trim();
        if (tag.Length == 0)
        {
            return false;
        }

        var added = _registry.AddTag(Hwnd, tag);
        if (added)
        {
            NewTagText = string.Empty;
        }
        return added;
    }

    /// <summary>Removes one tag from the window.</summary>
    /// <returns><c>true</c> when the registry's tag set changed.</returns>
    public bool RemoveTag(string tag) => _registry.RemoveTag(Hwnd, tag);

    /// <summary>
    /// Rebuilds the chips from a registry snapshot. Wholesale rather than diffed: a window
    /// carries a handful of tags at most, and the chips have no state worth preserving.
    /// </summary>
    public void ApplyTags(IReadOnlySet<string> tags)
    {
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(new TagChipViewModel(this, tag));
        }
        OnPropertyChanged(nameof(HasTags));
    }
}
