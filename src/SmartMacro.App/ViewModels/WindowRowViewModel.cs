using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

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

    /// <summary>Asks the daemon to drop this tag. Fire-and-forget — the chip disappears when the push comes back.</summary>
    public void Remove() => _ = _owner.RemoveTagAsync(Text);
}

/// <summary>
/// One tracked window in the main list: which process it belongs to, its handle, and its
/// live tag set with add/remove affordances.
///
/// The row owns no tag state of its own. Stage 3 did not change that, only where the owner
/// lives: it used to be Core's <c>WindowRegistry</c> in this process, and is now the
/// daemon's, reached over IPC. Add/remove therefore send a request and change nothing
/// locally — the visible chips are rebuilt by <see cref="ApplyTags"/> when the daemon pushes
/// <c>WindowTagsChanged</c> back. That keeps the display honest whether the change came from
/// this row, from another panel, or from a macro node.
/// </summary>
public sealed class WindowRowViewModel : ObservableObject
{
    private readonly IIpcClient _client;
    private string _newTagText = string.Empty;

    public WindowRowViewModel(IIpcClient client, WindowDto window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _client = client;
        Hwnd = window.Hwnd;
        ProcessName = window.ProcessName;
        ApplyTags(window.Tags);
    }

    /// <summary>Native handle — identity of the row, matching the daemon's key.</summary>
    public long Hwnd { get; }

    /// <summary>Owning process name, as reported by the daemon's process monitor.</summary>
    public string ProcessName { get; }

    /// <summary>Handle in the hex form the logs use, so a row can be matched to a log line.</summary>
    public string HwndHex => string.Create(CultureInfo.InvariantCulture, $"0x{Hwnd:X}");

    /// <summary>Live chips for the window's tags, in the order the daemon reported them.</summary>
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
    /// Sends whatever is typed in <see cref="NewTagText"/> to the daemon. The box is only
    /// cleared when a request actually went out, so a duplicate (or a failed call) leaves the
    /// text in place for the user to correct instead of vanishing silently.
    /// </summary>
    /// <returns><c>true</c> when an <c>AddTag</c> request was accepted by the daemon.</returns>
    public async Task<bool> AddTagAsync()
    {
        var tag = _newTagText.Trim();
        if (tag.Length == 0)
        {
            return false;
        }

        // Checked locally rather than by asking: the daemon treats a duplicate as a silent
        // no-op (it cannot know the difference matters to a text box), so this is the only
        // place that can tell "already there" from "added".
        if (Tags.Any(chip => string.Equals(chip.Text, tag, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!await SendAsync(IpcMessageTypes.AddTag, new AddTagRequest(Hwnd, tag)).ConfigureAwait(true))
        {
            return false;
        }

        NewTagText = string.Empty;
        return true;
    }

    /// <summary>Asks the daemon to remove one tag from the window.</summary>
    /// <returns><c>true</c> when the request was accepted.</returns>
    public Task<bool> RemoveTagAsync(string tag) =>
        SendAsync(IpcMessageTypes.RemoveTag, new RemoveTagRequest(Hwnd, tag));

    /// <summary>
    /// Rebuilds the chips from a daemon snapshot. Wholesale rather than diffed: a window
    /// carries a handful of tags at most, and the chips have no state worth preserving.
    /// </summary>
    public void ApplyTags(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(new TagChipViewModel(this, tag));
        }
        OnPropertyChanged(nameof(HasTags));
    }

    private async Task<bool> SendAsync(string type, object payload)
    {
        try
        {
            await _client.RequestAsync(type, payload).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // Tagging a window that died a moment ago is the common case and is not worth a
            // dialog; the row is about to disappear anyway.
            Log.Warning(ex, "Не удалось выполнить '{Request}' для окна 0x{Hwnd:X}", type, Hwnd);
            return false;
        }
    }
}
