using System.Runtime.InteropServices;

namespace SmartMacro.Native.Internal;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int pt_x;
    public int pt_y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public uint type;
    public InputUnion U;

    public static int Size => Marshal.SizeOf<INPUT>();
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public UIntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public UIntPtr dwExtraInfo;
}

// Полезная нагрузка LPARAM для колбэков хука WH_MOUSE_LL. Для сообщений WM_XBUTTONDOWN/UP
// в старшем слове mouseData лежит номер XButton (1 = XButton1, 2 = XButton2).
[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public UIntPtr dwExtraInfo;
}

// Данные для регистрации оконного класса через RegisterClassExW. Намеренно блиттируемая:
// оба строковых поля хранятся как сырые IntPtr (Marshal.StringToHGlobalUni), а lpfnWndProc —
// как указатель на функцию, поэтому вся структура проходит через сгенерированный
// LibraryImport stub без собственного маршалера.
[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public IntPtr lpszMenuName;
    public IntPtr lpszClassName;
    public IntPtr hIconSm;
}

// Полезная нагрузка Shell_NotifyIconW. Три текстовых поля в нативном объявлении — это
// встроенные массивы WCHAR, поэтому они смоделированы буферами `fixed char`, а не строками
// с [MarshalAs(ByValTStr)]: LibraryImport принимает только блиттируемые типы, и так размер
// и раскладка структуры остаются точь-в-точь как у нативной (976 байт на x64 — именно это
// значение cbSize обязан сообщать начиная с Vista).
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public IntPtr hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersionOrTimeout;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}
