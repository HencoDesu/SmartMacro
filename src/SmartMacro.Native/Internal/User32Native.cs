using System.Runtime.InteropServices;

namespace SmartMacro.Native.Internal;

// Голая P/Invoke-поверхность user32.dll плюс используемые нами константы Win32. Internal,
// чтобы публичный API этой сборки оставался на уровне абстракции (IKeyboardInput /
// IMouseInput / INativeWindow) — вызывающие никогда не видят User32 напрямую.
internal static partial class User32Native
{
    // ─── Оконные сообщения ───
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

    // Диапазон приватных сообщений. Shell_NotifyIcon доставляет свои уведомления о мыши
    // через callback-сообщение, которое выбирает владелец иконки, и WM_APP+n — как раз
    // документированный для этого диапазон.
    public const uint WM_APP = 0x8000;

    // ─── Низкоуровневые хуки ───
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    // GetAsyncKeyState — старший бит взведён ⇨ клавиша сейчас нажата. Возвращается short, и
    // маску мы накладываем явно, потому что нам нужна однозначная семантика «зажата ли», а
    // не история из младшего бита «нажимали ли с прошлого вызова».
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

    // WM_SETICON — заменяет иконку, которую окно показывает в заголовке и в своей записи на
    // панели задач. ICON_SMALL = 16x16 (заголовок, список задач). ICON_BIG = 32x32
    // (переключатель Alt-Tab, эскизы в task switcher). ICON_SMALL2 — то, что Windows 7+
    // использует для самой кнопки на панели задач; выставив все три, покрываем каждую
    // поверхность интерфейса.
    public const uint WM_SETICON = 0x0080;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;

    // LoadImage с IMAGE_ICON + LR_LOADFROMFILE читает .ico с диска и возвращает HICON.
    // cx/cy = 0 означает «взять размер по умолчанию из файла» (в многоразрешенческом .ico по
    // соглашению берётся самый крупный). LR_DEFAULTSIZE вместо этого берёт системный
    // маленький/большой размер по умолчанию. Без LR_SHARED HICON принадлежит нам и нам
    // полагалось бы звать DestroyIcon — но для кэша на всё время жизни процесса эта
    // бухгалтерия не окупается.
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const uint LR_DEFAULTSIZE = 0x0040;

    // ─── Окна только для сообщений, всплывающие меню (иконка в трее) ───

    // Окно с родителем HWND_MESSAGE никогда не показывается, не попадает в перечисления и не
    // получает ввод — оно существует исключительно ради собственной очереди сообщений.
    // Ровно то, что нужно иконке в трее.
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;

    // TPM_RETURNCMD заставляет TrackPopupMenuEx вернуть id выбранной команды, а не слать
    // WM_COMMAND, — так всё взаимодействие с меню умещается в один синхронный вызов.
    // TPM_NONOTIFY глушит болтовню из WM_ENTERMENULOOP/WM_INITMENU, которую мы всё равно
    // проигнорировали бы.
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint MF_BYCOMMAND = 0x0000;
    public const uint MF_BYPOSITION = 0x0400;

    // MAKEINTRESOURCE(32512) — стандартная иконка приложения, запасной вариант, когда
    // настроенного .ico-файла нет.
    public static readonly IntPtr IDI_APPLICATION = new(32512);

    // ─── Функциям с вариантами A/W под LibraryImport нужен явный EntryPoint на W ───

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

    // WNDCLASSEXW блиттируема (см. Structs.cs), поэтому сгенерированный stub передаёт её по
    // ссылке без маршалера. Возвращает ATOM класса, при неудаче — 0.
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

    // Регистрирует строку сообщения в общесистемной таблице; все, кто передал одну и ту же
    // строку, получают один и тот же id. Нужна, чтобы слушать широковещательное сообщение
    // оболочки «TaskbarCreated» — именно так владелец иконки в трее узнаёт, что Explorer
    // перезапустился и его иконки больше нет.
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    // ─── Функции без вариантов A/W ───

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

    // С TPM_RETURNCMD возвращаемое значение И ЕСТЬ id выбранной команды (0 = меню закрыли
    // без выбора).
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    // ─── Низкоуровневый хук мыши ───
    // SetWindowsHookEx с WH_MOUSE_LL работает на всю систему; колбэк срабатывает на каждое
    // событие мыши раньше, чем его увидит хоть одно окно. Колбэк должен быть коротким —
    // долгая работа в нём тормозит ввод мышью во всей системе.
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Оконная процедура для наших собственных окон только для сообщений. Передаётся в
    // RegisterClassEx сырым указателем на функцию через
    // Marshal.GetFunctionPointerForDelegate — вызывающий ОБЯЗАН удерживать экземпляр
    // делегата живым всё время, пока класс зарегистрирован, иначе GC соберёт thunk прямо
    // из-под Windows.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // У самой SetWindowsHookEx вариантов A/W в привычном смысле нет — это одна точка входа, а
    // маршалинг делегата требует старой поверхности [DllImport] (LibraryImport не умеет
    // генерировать маршалер указателя на функцию для управляемого типа-делегата).
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    // LoadImage — читает .ico с диска в HICON. Вариант EntryPoint на W, потому что в
    // Unicode-сборках путь широкосимвольный. При неудаче возвращает IntPtr.Zero (пути нет,
    // файл не иконка и т. п.); подробности даёт GetLastError.
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);
}
