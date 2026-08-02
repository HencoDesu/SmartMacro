using Microsoft.Extensions.Logging;

namespace SmartMacro.Windows;

// The SOLE owner of window tags. Everything that wants to know "which windows exist and
// what are they tagged with" — agents, macro routing, UI — asks the registry; nothing
// else holds tag state. Tags are runtime-only (hwnds are ephemeral), case-sensitive,
// free-form strings, applied by identification, macros, or manually from the UI.
//
// All mutations are atomic under one lock; events are raised OUTSIDE the lock (with a
// snapshot computed inside) so subscribers can call back into the registry without
// deadlocking. Singleton in DI.
public sealed partial class WindowRegistry
{
    private sealed class Entry
    {
        public required string ProcessName { get; init; }

        // Insertion-ordered so "first tag" (used as an agent's display name) is stable.
        // Tag counts per window are tiny, so List.Contains beats set overhead anyway.
        public List<string> Tags { get; } = [];
    }

    private static readonly IReadOnlySet<string> EmptyTags = new HashSet<string>(StringComparer.Ordinal);

    private readonly Lock _lock = new();
    private readonly Dictionary<IntPtr, Entry> _windows = [];
    private readonly ILogger<WindowRegistry> _logger;

    public WindowRegistry(ILogger<WindowRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised after a window is registered. Payload is the fresh (tagless) snapshot.</summary>
    public event Action<ManagedWindowInfo>? WindowAppeared;

    /// <summary>Raised after a tag is added to or removed from a window. Payload reflects the post-change tag set.</summary>
    public event Action<ManagedWindowInfo>? WindowTagsChanged;

    /// <summary>Raised after a window is unregistered. Payload carries the final tag set the window had.</summary>
    public event Action<ManagedWindowInfo>? WindowClosed;

    /// <summary>
    /// Adds a window to the registry with an empty tag set and raises <see cref="WindowAppeared"/>.
    /// </summary>
    /// <returns><c>true</c> when the window was added; <c>false</c> when the hwnd is already registered (no event).</returns>
    public bool Register(IntPtr hwnd, string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);

        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (_windows.ContainsKey(hwnd))
            {
                LogAlreadyRegistered(hwnd.ToInt64());
                return false;
            }

            var entry = new Entry { ProcessName = processName };
            _windows.Add(hwnd, entry);
            snapshot = ToSnapshot(hwnd, entry);
        }

        LogRegistered(hwnd.ToInt64(), processName);
        WindowAppeared?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Removes a window (and its tags) from the registry and raises <see cref="WindowClosed"/>
    /// with the final tag set.
    /// </summary>
    /// <returns><c>true</c> when the window was removed; <c>false</c> when the hwnd was not registered (no event).</returns>
    public bool Unregister(IntPtr hwnd)
    {
        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (!_windows.Remove(hwnd, out var entry))
            {
                return false;
            }
            snapshot = ToSnapshot(hwnd, entry);
        }

        LogUnregistered(hwnd.ToInt64(), snapshot.ProcessName);
        WindowClosed?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Adds a tag to a registered window and raises <see cref="WindowTagsChanged"/>.
    /// Tags are case-sensitive; adding an already-present tag is a no-op.
    /// </summary>
    /// <returns><c>true</c> when the tag set changed; <c>false</c> for an unknown hwnd or a duplicate tag (no event).</returns>
    public bool AddTag(IntPtr hwnd, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (!_windows.TryGetValue(hwnd, out var entry))
            {
                LogTagForUnknownWindow(tag, hwnd.ToInt64());
                return false;
            }
            if (entry.Tags.Contains(tag, StringComparer.Ordinal))
            {
                return false;
            }

            entry.Tags.Add(tag);
            snapshot = ToSnapshot(hwnd, entry);
        }

        LogTagAdded(tag, hwnd.ToInt64());
        WindowTagsChanged?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Removes a tag from a registered window and raises <see cref="WindowTagsChanged"/>.
    /// </summary>
    /// <returns><c>true</c> when the tag set changed; <c>false</c> for an unknown hwnd or an absent tag (no event).</returns>
    public bool RemoveTag(IntPtr hwnd, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (!_windows.TryGetValue(hwnd, out var entry))
            {
                return false;
            }
            if (!entry.Tags.Remove(tag))
            {
                return false;
            }
            snapshot = ToSnapshot(hwnd, entry);
        }

        LogTagRemoved(tag, hwnd.ToInt64());
        WindowTagsChanged?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Snapshot of the window's current tags. Isolated — the returned set never mutates,
    /// even if tags change afterwards. Unknown hwnd yields an empty set.
    /// </summary>
    public IReadOnlySet<string> GetTags(IntPtr hwnd)
    {
        lock (_lock)
        {
            return _windows.TryGetValue(hwnd, out var entry)
                ? new HashSet<string>(entry.Tags, StringComparer.Ordinal)
                : EmptyTags;
        }
    }

    /// <summary>Case-sensitive tag membership check. <c>false</c> for unknown hwnds.</summary>
    public bool HasTag(IntPtr hwnd, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        lock (_lock)
        {
            return _windows.TryGetValue(hwnd, out var entry)
                   && entry.Tags.Contains(tag, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Atomic snapshot of every registered window with its tags. Isolated from later
    /// mutations — safe to iterate without holding any lock.
    /// </summary>
    public IReadOnlyList<ManagedWindowInfo> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<ManagedWindowInfo>(_windows.Count);
            foreach (var (hwnd, entry) in _windows)
            {
                result.Add(ToSnapshot(hwnd, entry));
            }
            return result;
        }
    }

    // Caller must hold _lock.
    private static ManagedWindowInfo ToSnapshot(IntPtr hwnd, Entry entry) =>
        new(hwnd, entry.ProcessName, new HashSet<string>(entry.Tags, StringComparer.Ordinal));

    [LoggerMessage(LogLevel.Information, "Window registered: hwnd=0x{Hwnd:X} process='{ProcessName}'")]
    partial void LogRegistered(long hwnd, string processName);

    [LoggerMessage(LogLevel.Warning, "Window hwnd=0x{Hwnd:X} is already registered — ignoring duplicate Register")]
    partial void LogAlreadyRegistered(long hwnd);

    [LoggerMessage(LogLevel.Information, "Window unregistered: hwnd=0x{Hwnd:X} process='{ProcessName}'")]
    partial void LogUnregistered(long hwnd, string processName);

    [LoggerMessage(LogLevel.Information, "Tag '{Tag}' added to hwnd=0x{Hwnd:X}")]
    partial void LogTagAdded(string tag, long hwnd);

    [LoggerMessage(LogLevel.Information, "Tag '{Tag}' removed from hwnd=0x{Hwnd:X}")]
    partial void LogTagRemoved(string tag, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Tag '{Tag}' targets unregistered hwnd=0x{Hwnd:X} — ignored")]
    partial void LogTagForUnknownWindow(string tag, long hwnd);
}
