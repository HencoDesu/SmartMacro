using System.Runtime.InteropServices;

namespace SmartMacro.Native.Internal;

// Raw P/Invoke surface for shell32.dll — currently just the notification-area (tray) API.
internal static partial class Shell32Native
{
    // ─── Shell_NotifyIcon messages ───
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    // ─── Which NOTIFYICONDATA members are valid ───
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    // NOTIFYICONDATAW is blittable (fixed char buffers, see Structs.cs), so the generated
    // stub passes it straight through. Fails (returns false) if the shell isn't ready yet —
    // callers should treat that as "retry or give up", not as a fatal error.
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint dwMessage, in NOTIFYICONDATAW lpData);
}
