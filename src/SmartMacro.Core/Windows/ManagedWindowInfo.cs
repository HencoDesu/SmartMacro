namespace SmartMacro.Windows;

/// <summary>
/// Immutable snapshot of one window tracked by <see cref="WindowRegistry"/>: the native
/// handle, the owning process name, and the tag set at the moment the snapshot was taken.
/// Tags are case-sensitive free-form strings and live only for the lifetime of the window
/// (hwnds are ephemeral, so nothing here is persisted).
/// </summary>
/// <param name="Hwnd">Native window handle. Identity key inside the registry.</param>
/// <param name="ProcessName">OS process name the window belongs to (as reported by ProcessMonitor).</param>
/// <param name="Tags">Snapshot of the window's tags. Never mutates — later registry changes produce new snapshots.</param>
public sealed record ManagedWindowInfo(IntPtr Hwnd, string ProcessName, IReadOnlySet<string> Tags);
