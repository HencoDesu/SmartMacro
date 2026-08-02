using System.Runtime.InteropServices;

namespace SmartMacro.Native.Internal;

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
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_RBUTTONUP = 0x0205;

    // Private message range. Shell_NotifyIcon delivers its mouse notifications through a
    // callback message the owner picks, and WM_APP+n is the documented range for that.
    public const uint WM_APP = 0x8000;

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

    // WM_SETICON — replaces the icon shown in the window's title bar and taskbar entry.
    // ICON_SMALL = 16x16 (title bar, taskbar list). ICON_BIG = 32x32 (Alt-Tab switcher,
    // task switcher thumbnails). ICON_SMALL2 is what Windows 7+ uses for the taskbar
    // button itself; setting all three covers every UI surface.
    public const uint WM_SETICON = 0x0080;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;

    // LoadImage with IMAGE_ICON + LR_LOADFROMFILE reads a .ico file from disk and
    // returns an HICON. cx/cy = 0 means "use the file's default size" (multi-resolution
    // .ico picks the largest by convention). LR_DEFAULTSIZE picks the system-default
    // small/large size instead. Without LR_SHARED the HICON belongs to us and we'd need
    // DestroyIcon — but for process-lifetime cache that's not worth the bookkeeping.
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const uint LR_DEFAULTSIZE = 0x0040;

    // ─── Message-only windows, popup menus (tray icon) ───

    // A window parented to HWND_MESSAGE is never shown, never enumerated, and gets no
    // input — it exists purely to own a message queue. Exactly what a tray icon needs.
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;

    // TPM_RETURNCMD makes TrackPopupMenuEx return the chosen command id instead of posting
    // WM_COMMAND, which keeps the whole menu interaction inside one synchronous call.
    // TPM_NONOTIFY suppresses the WM_ENTERMENULOOP/WM_INITMENU chatter we'd ignore anyway.
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint MF_BYCOMMAND = 0x0000;
    public const uint MF_BYPOSITION = 0x0400;

    // MAKEINTRESOURCE(32512) — the stock application icon, used as the fallback when the
    // configured .ico file is missing.
    public static readonly IntPtr IDI_APPLICATION = new(32512);

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

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // WNDCLASSEXW is blittable (see Structs.cs) so the generated stub can pass it by ref
    // without a marshaller. Returns the class ATOM, 0 on failure.
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    public static partial IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    // Interns a message string into the system-wide message table; every caller passing the
    // same string gets the same id. Used to listen for the shell's "TaskbarCreated"
    // broadcast, which is how a tray owner learns Explorer restarted and its icon is gone.
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    // With TPM_RETURNCMD the return value IS the selected command id (0 = dismissed).
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    // ─── Low-level mouse hook ───
    // SetWindowsHookEx with WH_MOUSE_LL is system-wide; the callback fires for every mouse
    // event before any window sees it. Keep the callback short — long work blocks all
    // mouse input across the system.
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Window procedure for our own message-only windows. Handed to RegisterClassEx as a
    // raw function pointer via Marshal.GetFunctionPointerForDelegate — the caller MUST keep
    // the delegate instance rooted for as long as the class stays registered, otherwise the
    // GC collects the thunk out from under Windows.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    // LoadImage — reads .ico from disk into an HICON. EntryPoint W variant because the
    // path is wide-char in Unicode builds. Returns IntPtr.Zero on failure (path missing,
    // not an icon file, etc.); GetLastError gives detail.
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);
}
