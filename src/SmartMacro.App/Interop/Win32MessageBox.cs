using System.Runtime.InteropServices;

namespace SmartMacro.App.Interop;

/// <summary>
/// A blocking, native error box.
///
/// Avalonia has no built-in message dialog, and the two places the panel needs one are both
/// places an Avalonia window is a bad fit: the daemon-unreachable failure happens BEFORE
/// <c>AppBuilder.StartWithClassicDesktopLifetime</c> (there is no UI thread yet, no styles,
/// no window to own a dialog), and the daemon-died notice has to be the last thing shown
/// before the process exits. <c>MessageBoxW</c> pumps its own modal loop and needs neither.
/// </summary>
internal static partial class Win32MessageBox
{
    private const uint MbOk = 0x0000_0000;
    private const uint MbIconError = 0x0000_0010;
    private const uint MbSystemModal = 0x0000_1000;
    private const uint MbSetForeground = 0x0001_0000;
    private const uint MbTopMost = 0x0004_0000;

    /// <summary>
    /// Shows an error box and returns when the user dismisses it. Topmost and foreground —
    /// the game's clients are full-screen, and a dialog behind them would look like a hang.
    /// </summary>
    public static void Error(string caption, string text) =>
        MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbIconError | MbSystemModal | MbSetForeground | MbTopMost);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
