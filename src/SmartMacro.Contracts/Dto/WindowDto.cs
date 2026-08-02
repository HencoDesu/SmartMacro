namespace SmartMacro.Contracts.Dto;

/// <summary>
/// Wire form of one tracked window. Mirror of Core's <c>ManagedWindowInfo</c>, minus the
/// things that don't cross a process boundary well: the handle is a <see cref="long"/>
/// rather than an <c>IntPtr</c> (stable width on the wire, no pointer semantics implied),
/// and the tag set is an ordered list rather than a set (JSON has no set).
/// </summary>
/// <param name="Hwnd">Native window handle. Identity key — the UI echoes it back in AddTag/RemoveTag.</param>
/// <param name="ProcessName">OS process name the window belongs to.</param>
/// <param name="Tags">The window's tags at the moment the snapshot was taken.</param>
public sealed record WindowDto(long Hwnd, string ProcessName, IReadOnlyList<string> Tags);
