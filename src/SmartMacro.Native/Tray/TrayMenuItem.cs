namespace SmartMacro.Native.Tray;

/// <summary>
/// One entry of the tray icon's context menu.
/// </summary>
/// <param name="Id">
/// Caller-chosen stable identifier echoed back through
/// <see cref="Win32TrayIcon.ItemClicked"/>. Win32 command ids are positional and get
/// regenerated on every <see cref="Win32TrayIcon.Start"/>, so subscribers switch on this
/// instead.
/// </param>
/// <param name="Text">Label shown in the menu. May contain any Unicode (the whole path is UTF-16).</param>
/// <param name="IsDefault">
/// Marks the item as the menu's default (rendered bold) and makes it the action taken on a
/// double-click of the icon. At most one item should set this; if several do, the first
/// wins.
/// </param>
public sealed record TrayMenuItem(string Id, string Text, bool IsDefault = false);
