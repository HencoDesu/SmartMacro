using System.Runtime.InteropServices;

namespace SmartMacro.Native.Internal;

internal static partial class Kernel32Native
{
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    // GetModuleHandle(NULL) возвращает хендл самого EXE — именно его SetWindowsHookEx ждёт
    // в параметре hMod для низкоуровневого хука, поставленного из управляемого кода.
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? lpModuleName);
}
