using System.Runtime.InteropServices;

namespace PerfectWorldAgent.Native.Internal;

// Raw P/Invoke surface for user32.dll + the Win32 constants we use. Internal so the public
// API of this assembly stays at the abstraction level (IKeyboardInput / IMouseInput /
// INativeWindow) — callers never see User32 directly.
internal static partial class User32Native
{
    // ─── Window messages ───
    public const uint WM_ACTIVATEAPP = 0x001C;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_QUIT = 0x0012;

    // ─── Low-level hooks ───
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    // GetAsyncKeyState — high bit set ⇨ key currently down. Returned value is a short, we
    // mask explicitly because we want unambiguous "is held" semantics, not the low-bit
    // "was pressed since last call" history.
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;     // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    public const int MK_LBUTTON = 0x0001;

    public const uint MAPVK_VK_TO_VSC = 0x00;

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const uint MOD_NOREPEAT = 0x4000;

    public const uint PW_CLIENTONLY = 0x00000001;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    // ─── Functions with A/W variants need explicit W EntryPoint under LibraryImport ───

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    public static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW", SetLastError = true)]
    public static partial uint MapVirtualKey(uint uCode, uint uMapType);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    // ─── Single-variant functions ───

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    // ─── Low-level mouse hook ───
    // SetWindowsHookEx with WH_MOUSE_LL is system-wide; the callback fires for every mouse
    // event before any window sees it. Keep the callback short — long work blocks all
    // mouse input across the system.
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // SetWindowsHookEx itself doesn't have A/W variants in the same way — it's just one
    // entry point, and the delegate marshalling requires the older [DllImport] surface
    // (LibraryImport can't generate the function-pointer marshaller for a managed delegate
    // type).
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);
}
