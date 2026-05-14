using System.Runtime.InteropServices;

namespace PerfectWorldAgent.Native.Internal;

internal static partial class Kernel32Native
{
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    // GetModuleHandle(NULL) returns the handle of the EXE itself — what SetWindowsHookEx
    // wants for the hMod parameter of a low-level hook installed from managed code.
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? lpModuleName);
}
