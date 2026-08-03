using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Window;

[SupportedOSPlatform("windows")]
public sealed class Win32NativeWindow : INativeWindow
{
    public Win32NativeWindow(IntPtr handle)
    {
        Handle = handle;
    }

    public IntPtr Handle { get; }

    public bool IsAlive => Handle != IntPtr.Zero && User32Native.IsWindow(Handle);

    public (int Width, int Height) GetClientSize()
    {
        if (!User32Native.GetClientRect(Handle, out var rect))
        {
            throw new InvalidOperationException(
                $"GetClientRect failed for hwnd 0x{Handle:X} (Win32 error {Marshal.GetLastWin32Error()})");
        }
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public (int X, int Y) ClientToScreen(int clientX, int clientY)
    {
        var point = new POINT { X = clientX, Y = clientY };
        User32Native.ClientToScreen(Handle, ref point);
        return (point.X, point.Y);
    }

    public (int X, int Y) ScreenToClient(int screenX, int screenY)
    {
        var point = new POINT { X = screenX, Y = screenY };
        User32Native.ScreenToClient(Handle, ref point);
        return (point.X, point.Y);
    }

    public bool BringToFront()
    {
        if (User32Native.SetForegroundWindow(Handle))
        {
            return true;
        }

        // Современная Windows блокирует SetForegroundWindow для процессов, которые не на
        // переднем плане. AttachThreadInput — канонический обходной приём.
        var targetThread = User32Native.GetWindowThreadProcessId(Handle, out _);
        var currentThread = Kernel32Native.GetCurrentThreadId();
        if (targetThread == 0 || targetThread == currentThread)
        {
            return false;
        }

        if (!User32Native.AttachThreadInput(currentThread, targetThread, true))
        {
            return false;
        }

        try
        {
            return User32Native.SetForegroundWindow(Handle);
        }
        finally
        {
            User32Native.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    public void SendActivationSignal(uint lParam)
    {
        // wParam=1 (TRUE) — «это окно активируется». lParam в документации описан как
        // «идентификатор потока, которому принадлежит активируемое/деактивируемое окно»;
        // магическое значение по умолчанию 0x91D8 перенято из заведомо рабочего стороннего
        // хелпера — движок PW, судя по всему, само значение игнорирует, но принимает
        // сообщение как сигнал к пробуждению.
        //
        // Используем SendMessage (синхронный): блокируемся, пока WndProc у PW не вернёт
        // управление. Это гарантирует, что PW обработал активацию ДО того, как мы начнём
        // отправлять ввод. Под нагрузкой (11 окон рассылают одновременно) варианты на
        // PostMessage встали бы в очередь наравне со всем остальным и могли бы обработаться
        // слишком поздно — KEYDOWN пришёл бы, пока PW всё ещё в придушенном фоновом
        // состоянии. Парная деактивация, наоборот, идёт через PostMessage, чтобы встать в
        // очередь ПОСЛЕ отправленного ввода и дать PW обработать нажатие в правильном
        // порядке: ACTIVATE(TRUE, синхронно) → KEYDOWN/UP (в очереди) → ACTIVATE(FALSE, в
        // очереди). Обоснование этой асимметрии см. в SendDeactivationSignal.
        User32Native.SendMessage(Handle, User32Native.WM_ACTIVATEAPP, (IntPtr)1, (IntPtr)lParam);
    }

    public void SendDeactivationSignal()
    {
        // wParam=0 (FALSE) — «это окно деактивируется». lParam по документации — id потока
        // окна, забирающего фокус; передаём 0, поскольку PW его игнорирует.
        //
        // PostMessage — чтобы деактивация встала в очередь ПОСЛЕ всех KEYDOWN/UP, которые мы
        // отправили как ввод; почему вся последовательность гоняется через очередь, см. в
        // SendActivationSignal. Задержка на прокачку очереди в GameWindow.DeactivateAsync
        // всё равно нужна: она даёт насосу сообщений PW достаточно реального времени, чтобы
        // разобрать очередь до нашего ухода.
        User32Native.PostMessage(Handle, User32Native.WM_ACTIVATEAPP, IntPtr.Zero, IntPtr.Zero);
    }

    // Снимает кадр через PrintWindow с PW_CLIENTONLY | PW_RENDERFULLCONTENT — второй флаг
    // критичен для окон на DirectX/DirectComposition (клиент игры именно такой): без него
    // многие такие окна отдают сплошь чёрную картинку.
    public byte[] CapturePng()
    {
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Window handle is null.");
        }

        var (width, height) = GetClientSize();
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"Window 0x{Handle:X} has zero-sized client area ({width}x{height}) — minimised or destroyed?");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!User32Native.PrintWindow(Handle, hdc, User32Native.PW_CLIENTONLY | User32Native.PW_RENDERFULLCONTENT))
            {
                throw new InvalidOperationException(
                    $"PrintWindow failed for hwnd 0x{Handle:X} (Win32 error {Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public bool SetIconFromFile(string icoPath)
    {
        var hicon = WindowIconCache.GetOrLoad(icoPath);
        if (hicon == IntPtr.Zero)
        {
            return false;
        }

        // Выставляем все три поверхности, чтобы иконка появилась и в заголовке окна, и в
        // списке задач, и на кнопке панели задач. SendMessage, а не PostMessage: PW
        // замораживает фоновые клиенты, и их очередь сообщений не разбирается, пока окно
        // что-нибудь не разбудит (клик пользователя, WM_ACTIVATEAPP). Post'нутый WM_SETICON
        // просто лежал бы в очереди неактивных окон, и после опознания нескольких агентов
        // иконку получили бы только те немногие, что оказались на переднем плане.
        // SendMessage же вынуждает синхронную диспетчеризацию в WndProc.
        User32Native.SendMessage(Handle, User32Native.WM_SETICON, (IntPtr)User32Native.ICON_SMALL, hicon);
        User32Native.SendMessage(Handle, User32Native.WM_SETICON, (IntPtr)User32Native.ICON_BIG, hicon);
        User32Native.SendMessage(Handle, User32Native.WM_SETICON, (IntPtr)User32Native.ICON_SMALL2, hicon);
        return true;
    }
}
