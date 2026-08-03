using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Tray;

// Иконка в области уведомлений («в трее»), построенная напрямую на Shell_NotifyIcon — без
// Avalonia, без WinForms, без какого бы то ни было UI-фреймворка. В этом весь смысл: демон —
// фоновый процесс без окон, и притащить целый тулкит ради одной иконки 16×16 стоило бы ему
// десятков мегабайт управляемой кучи плюс цикл отрисовки, которым он никогда не пользуется.
//
// Устройство повторяет Win32HotkeyMonitor, потому что ограничение то же: Shell_NotifyIcon
// шлёт свои уведомления о мыши в окно, а оконные сообщения доставляются только тому потоку,
// который это окно создал. Поэтому компонент владеет отдельным фоновым потоком, который:
//   1. Регистрирует приватный оконный класс и создаёт окно только для сообщений (родитель
//      HWND_MESSAGE — никогда не показывается, не попадает в перечисления, не получает фокус
//      ввода)
//   2. Собирает всплывающее меню и добавляет иконку (NIM_ADD)
//   3. Крутит цикл GetMessage, превращая callback-сообщение иконки в ItemClicked
//   4. Аккуратно выходит, когда Stop() кладёт WM_QUIT в его очередь сообщений, и в блоке
//      finally разбирает обратно иконку, меню, окно и класс
//
// ⚠ ItemClicked поднимается В ПОТОКЕ НАСОСА, изнутри собственного модального цикла меню.
// Обработчики обязаны возвращать управление немедленно — всё медленное (запуск процесса,
// остановка хоста, ввод-вывод) место в пуле потоков. Блокирующий обработчик подвешивает
// иконку в трее и, поскольку TrackPopupMenuEx всё ещё на стеке, может заклинить обработку
// ввода в самой оболочке.
[SupportedOSPlatform("windows")]
public sealed partial class Win32TrayIcon : IDisposable
{
    // Приватное callback-сообщение. Shell_NotifyIcon резервирует WM_APP..0xBFFF ровно под
    // это; вызвавшее его событие мыши приходит в младшем слове lParam.
    private const uint WM_TRAY_CALLBACK = User32Native.WM_APP + 1;

    // На экземпляр приходится одна иконка, поэтому константного id хватает, чтобы адресовать
    // её в NIM_MODIFY / NIM_DELETE.
    private const uint TrayIconId = 1;

    private const int StopTimeoutSeconds = 5;

    // Shell_NotifyIcon(NIM_ADD) падает, пока оболочка не принимает регистрации, — обычное
    // дело, когда демон стартует автоматически раньше explorer.exe. Вместо того чтобы
    // умирать, недолго повторяем: трей — единственный способ для пользователя выйти из
    // процесса без окон.
    private const int AddIconAttempts = 5;
    private const int AddIconRetryDelayMs = 500;

    private readonly ILogger<Win32TrayIcon> _logger;
    private readonly Lock _lifecycleLock = new();

    private Thread? _pumpThread;
    private uint _pumpThreadId;

    // Со старта принадлежат потоку насоса; трогаются только оттуда.
    private IntPtr _hwnd;
    private IntPtr _menu;
    private IntPtr _icon;
    private bool _ownsIcon;
    private IntPtr _classNamePtr;
    private string? _className;
    private User32Native.WndProc? _wndProc;
    private Dictionary<int, string> _commandToItemId = [];
    private int _defaultCommand;
    private uint _taskbarCreatedMessage;
    private string _activeTooltip = string.Empty;

    /// <summary>
    /// Поднимается с <see cref="TrayMenuItem.Id"/> выбранного пункта (или пункта по
    /// умолчанию — при двойном клике по иконке).
    /// </summary>
    /// <remarks>
    /// Вызывается в потоке насоса сообщений трея, пока модальный цикл меню ещё на стеке.
    /// Обработчики ОБЯЗАНЫ быстро возвращать управление — настоящую работу ставьте в очередь
    /// в другом месте. Исключения, вылетевшие из обработчика, ловятся и пишутся в лог, а не
    /// убивают насос.
    /// </remarks>
    public event Action<string>? ItemClicked;

    public Win32TrayIcon(ILogger<Win32TrayIcon> logger)
    {
        _logger = logger;
    }

    /// <summary>Работает ли сейчас поток насоса.</summary>
    public bool IsRunning => Volatile.Read(ref _pumpThread) is not null;

    /// <summary>
    /// Поднимает поток насоса, создаёт окно только для сообщений и показывает иконку.
    /// Блокируется, пока иконка не появится (или пока насос не упадёт — тогда его исключение
    /// перебрасывается сюда).
    /// </summary>
    /// <param name="tooltip">Текст всплывающей подсказки; обрезается до 127 символов — это предел Win32.</param>
    /// <param name="iconPath">
    /// Путь к файлу <c>.ico</c>. Если файла нет или он не читается, с предупреждением берётся
    /// стандартная иконка <c>IDI_APPLICATION</c>: сломанная иконка ни при каких условиях не
    /// должна лишать демона присутствия в трее, потому что это единственный способ его
    /// закрыть.
    /// </param>
    /// <param name="items">Пункты контекстного меню в порядке отображения.</param>
    /// <exception cref="InvalidOperationException">Уже запущено.</exception>
    public void Start(string tooltip, string iconPath, IReadOnlyList<TrayMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        TaskCompletionSource ready;
        lock (_lifecycleLock)
        {
            if (_pumpThread is not null)
            {
                throw new InvalidOperationException("Tray icon already started.");
            }

            ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Параметры запуска едут через замыкание, а не через поля: иначе Stop(),
            // гоняющийся с этим Start(), успел бы обнулить общий TCS до того, как насос его
            // прочитает, и строка ниже ждала бы вечно.
            var tooltipCopy = tooltip ?? string.Empty;
            var iconPathCopy = iconPath ?? string.Empty;
            _pumpThread = new Thread(() => MessageLoop(ready, tooltipCopy, iconPathCopy, items))
            {
                IsBackground = true,
                Name = "SmartMacro-TrayLoop",
            };
            // Область уведомлений оболочки — территория COM/OLE; насос в STA-апартаменте
            // для notify-иконки самый привычный (и самый безопасный) дом.
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();
        }

        // Насос всегда доводит этот TCS до завершения: результатом — как только иконка
        // добавлена, исключением — при неудаче, отменой из блока finally — если он всё же
        // размотался, не подав сигнала. Так что зависнуть на мёртвом потоке здесь нельзя.
        ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Кладёт насосу <c>WM_QUIT</c> и ждёт, пока тот всё разберёт (иконка снята, окно
    /// уничтожено, класс разрегистрирован). Вызывать можно повторно и из любого потока;
    /// второй вызов на остановленном объекте ничего не делает.
    /// </summary>
    public void Stop()
    {
        Thread? thread;
        uint threadId;
        lock (_lifecycleLock)
        {
            thread = _pumpThread;
            if (thread is null)
            {
                return;
            }
            threadId = _pumpThreadId;
            _pumpThread = null;
            _pumpThreadId = 0;
        }

        if (threadId != 0)
        {
            User32Native.PostThreadMessage(threadId, User32Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (!thread.Join(TimeSpan.FromSeconds(StopTimeoutSeconds)))
        {
            // Поток фоновый (IsBackground = true) — его подберёт выход из процесса, а
            // осиротевшие иконки оболочка сама убирает, когда владеющий ими процесс умирает.
            LogStopTimedOut(StopTimeoutSeconds);
            return;
        }

        LogStopped();
    }

    public void Dispose() => Stop();

    // ─── Поток насоса сообщений ───

    private void MessageLoop(
        TaskCompletionSource ready,
        string tooltip,
        string iconPath,
        IReadOnlyList<TrayMenuItem> items)
    {
        var threadId = Kernel32Native.GetCurrentThreadId();
        lock (_lifecycleLock)
        {
            // Stop() мог уже обогнать нас в гонке и обнулить _pumpThread. Опубликуй мы id
            // всё равно — более поздний Stop() отправил бы WM_QUIT потоку, который давно
            // занят другим; вместо этого оставляем ноль и разматываемся через finally ниже.
            if (_pumpThread is not null)
            {
                _pumpThreadId = threadId;
            }
        }

        try
        {
            CreateMessageWindow();
            _icon = LoadTrayIcon(iconPath);
            BuildMenu(items);
            _activeTooltip = tooltip;

            // Регистрируем до первого NIM_ADD, чтобы не пропустить широковещательное
            // сообщение, пока повторяем попытки на ещё поднимающейся оболочке.
            _taskbarCreatedMessage = User32Native.RegisterWindowMessage("TaskbarCreated");

            AddNotifyIconWithRetry();

            LogStarted(items.Count);
            ready.TrySetResult();

            while (User32Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                User32Native.TranslateMessage(in msg);
                User32Native.DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            LogPumpFailed(ex);
            ready.TrySetException(ex);
        }
        finally
        {
            // Страховка: гарантирует, что Start() не заблокируется навсегда, если насос
            // размотался, не подав сигнала. На уже завершённом TCS ничего не делает.
            ready.TrySetCanceled();
            Cleanup();
        }
    }

    private void CreateMessageWindow()
    {
        var hInstance = Kernel32Native.GetModuleHandle(null);

        // Имена классов глобальны в пределах процесса. Суффикс-GUID не даёт двум экземплярам
        // (или перезапуску, гоняющемуся с собственной уборкой) столкнуться на
        // ERROR_CLASS_ALREADY_EXISTS.
        _className = $"SmartMacroTray_{Guid.NewGuid():N}";
        _classNamePtr = Marshal.StringToHGlobalUni(_className);

        // Держим ссылку в поле всё время, пока класс зарегистрирован: Windows хранит сырой
        // указатель на thunk и о существовании GC не подозревает.
        _wndProc = WindowProc;

        var descriptor = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _classNamePtr,
        };

        if (User32Native.RegisterClassEx(in descriptor) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed for the tray window class.");
        }

        _hwnd = User32Native.CreateWindowEx(
            0,
            _className,
            "SmartMacro Tray",
            0,
            0, 0, 0, 0,
            User32Native.HWND_MESSAGE,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for the tray message window.");
        }
    }

    private IntPtr LoadTrayIcon(string iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            var handle = User32Native.LoadImage(
                IntPtr.Zero,
                iconPath,
                User32Native.IMAGE_ICON,
                0, 0,
                User32Native.LR_LOADFROMFILE | User32Native.LR_DEFAULTSIZE);

            if (handle != IntPtr.Zero)
            {
                _ownsIcon = true;
                return handle;
            }

            LogIconLoadFailed(iconPath, Marshal.GetLastWin32Error());
        }
        else
        {
            LogIconMissing(iconPath);
        }

        // Стандартные иконки — разделяемые системные ресурсы, DestroyIcon для них не
        // вызывают никогда.
        _ownsIcon = false;
        return User32Native.LoadIcon(IntPtr.Zero, User32Native.IDI_APPLICATION);
    }

    private void BuildMenu(IReadOnlyList<TrayMenuItem> items)
    {
        _menu = User32Native.CreatePopupMenu();
        if (_menu == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePopupMenu failed for the tray menu.");
        }

        // Идентификаторы команд начинаются с 1: TrackPopupMenuEx с TPM_RETURNCMD возвращает 0
        // в значении «закрыли, ничего не выбрав», так что 0 не может быть настоящим пунктом.
        var map = new Dictionary<int, string>(items.Count);
        var command = 1;
        _defaultCommand = 0;

        foreach (var item in items)
        {
            var id = command++;
            if (!User32Native.AppendMenu(_menu, User32Native.MF_STRING, (UIntPtr)id, item.Text))
            {
                LogMenuItemFailed(item.Text, Marshal.GetLastWin32Error());
                continue;
            }

            map[id] = item.Id;
            if (item.IsDefault && _defaultCommand == 0)
            {
                _defaultCommand = id;
            }
        }

        _commandToItemId = map;

        if (_defaultCommand != 0)
        {
            // fByPos = 0 ⇒ uItem — это id команды, а не индекс.
            User32Native.SetMenuDefaultItem(_menu, (uint)_defaultCommand, 0);
        }
    }

    private void AddNotifyIconWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            if (TryAddNotifyIcon())
            {
                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (attempt >= AddIconAttempts)
            {
                throw new Win32Exception(error, "Shell_NotifyIcon(NIM_ADD) failed — is the shell running?");
            }

            LogIconAddRetry(attempt, AddIconAttempts, error);
            Thread.Sleep(AddIconRetryDelayMs);
        }
    }

    private unsafe bool TryAddNotifyIcon()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = Shell32Native.NIF_ICON | Shell32Native.NIF_MESSAGE | Shell32Native.NIF_TIP,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon = _icon,
        };
        CopyNullTerminated(data.szTip, 128, _activeTooltip);

        return Shell32Native.Shell_NotifyIcon(Shell32Native.NIM_ADD, in data);
    }

    private unsafe void RemoveNotifyIcon()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayIconId,
        };

        if (!Shell32Native.Shell_NotifyIcon(Shell32Native.NIM_DELETE, in data))
        {
            LogIconRemovalFailed(Marshal.GetLastWin32Error());
        }
    }

    private static unsafe void CopyNullTerminated(char* destination, int capacity, string value)
    {
        var length = Math.Min(value.Length, capacity - 1);
        for (var i = 0; i < length; i++)
        {
            destination[i] = value[i];
        }
        destination[length] = '\0';
    }

    // Выполняется в потоке насоса, вызывается самой Windows. Ни в коем случае нельзя выпускать
    // исключение обратно в нативный код — это мгновенное убийство процесса, а не сбой,
    // который можно поймать.
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // Не константа времени компиляции (RegisterWindowMessage выдаёт значение в
            // рантайме), поэтому меткой switch быть не может. Перезапуск Explorer стирает все
            // иконки в области уведомлений; эта широковещательная рассылка — способ оболочки
            // сказать владельцам, чтобы добавили свои заново.
            if (msg != 0 && msg == _taskbarCreatedMessage)
            {
                LogTaskbarRecreated(TryAddNotifyIcon());
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_TRAY_CALLBACK:
                    // Исходное мышиное сообщение лежит в младшем слове lParam.
                    var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
                    switch (mouseMessage)
                    {
                        case User32Native.WM_RBUTTONUP:
                        case User32Native.WM_CONTEXTMENU:
                            ShowContextMenu();
                            return IntPtr.Zero;

                        case User32Native.WM_LBUTTONDBLCLK:
                            if (_defaultCommand != 0)
                            {
                                RaiseItemClicked(_defaultCommand);
                            }
                            return IntPtr.Zero;
                    }
                    break;

                case User32Native.WM_DESTROY:
                    User32Native.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            LogWindowProcFailed(ex, msg);
            return IntPtr.Zero;
        }

        return User32Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_menu == IntPtr.Zero)
        {
            return;
        }

        if (!User32Native.GetCursorPos(out var cursor))
        {
            return;
        }

        // KB135788, первая половина: без владения передним планом меню так и не получит клик,
        // который должен его закрыть, и остаётся висеть после того, как пользователь щёлкнул
        // в стороне.
        User32Native.SetForegroundWindow(_hwnd);

        var command = User32Native.TrackPopupMenuEx(
            _menu,
            User32Native.TPM_RETURNCMD | User32Native.TPM_RIGHTBUTTON | User32Native.TPM_NONOTIFY
                | User32Native.TPM_LEFTALIGN | User32Native.TPM_BOTTOMALIGN,
            cursor.X,
            cursor.Y,
            _hwnd,
            IntPtr.Zero);

        // KB135788, вторая половина: фиктивное сообщение, чтобы внутренний модальный цикл
        // меню заметил, что пора выходить.
        User32Native.PostMessage(_hwnd, User32Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (command > 0)
        {
            RaiseItemClicked(command);
        }
    }

    private void RaiseItemClicked(int command)
    {
        if (!_commandToItemId.TryGetValue(command, out var itemId))
        {
            LogUnknownCommand(command);
            return;
        }

        LogItemClicked(itemId);
        try
        {
            ItemClicked?.Invoke(itemId);
        }
        catch (Exception ex)
        {
            LogSubscriberFailed(ex, itemId);
        }
    }

    // Всё, что насос выделил, освобождается в обратном порядке. Выполняется в блоке finally
    // потока насоса, поэтому покрывает и падение на полпути к запуску (каждый шаг прикрыт
    // собственной проверкой хендла).
    private void Cleanup()
    {
        if (_hwnd != IntPtr.Zero)
        {
            RemoveNotifyIcon();
        }

        if (_menu != IntPtr.Zero)
        {
            User32Native.DestroyMenu(_menu);
            _menu = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            User32Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_className is not null)
        {
            User32Native.UnregisterClass(_className, Kernel32Native.GetModuleHandle(null));
            _className = null;
        }

        if (_classNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_classNamePtr);
            _classNamePtr = IntPtr.Zero;
        }

        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            User32Native.DestroyIcon(_icon);
        }
        _icon = IntPtr.Zero;
        _ownsIcon = false;

        _wndProc = null;
        _commandToItemId = [];
        _defaultCommand = 0;
        _taskbarCreatedMessage = 0;
        _activeTooltip = string.Empty;
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Tray icon started with {ItemCount} menu item(s)")]
    partial void LogStarted(int itemCount);

    [LoggerMessage(LogLevel.Information, "Tray icon stopped")]
    partial void LogStopped();

    [LoggerMessage(LogLevel.Warning, "Tray pump thread did not exit within {Seconds}s — leaving it to process teardown")]
    partial void LogStopTimedOut(int seconds);

    [LoggerMessage(LogLevel.Debug, "Tray menu item '{ItemId}' clicked")]
    partial void LogItemClicked(string itemId);

    [LoggerMessage(LogLevel.Warning, "Tray icon file '{IconPath}' not found — falling back to the stock application icon")]
    partial void LogIconMissing(string iconPath);

    [LoggerMessage(LogLevel.Warning, "LoadImage failed for tray icon '{IconPath}' (Win32 error {ErrorCode}) — falling back to the stock application icon")]
    partial void LogIconLoadFailed(string iconPath, int errorCode);

    [LoggerMessage(LogLevel.Warning, "AppendMenu failed for tray menu item '{Text}' (Win32 error {ErrorCode})")]
    partial void LogMenuItemFailed(string text, int errorCode);

    [LoggerMessage(LogLevel.Warning, "Shell_NotifyIcon(NIM_DELETE) failed (Win32 error {ErrorCode}) — the icon may linger until the shell refreshes")]
    partial void LogIconRemovalFailed(int errorCode);

    [LoggerMessage(LogLevel.Warning, "Shell_NotifyIcon(NIM_ADD) attempt {Attempt}/{Total} failed (Win32 error {ErrorCode}) — shell not ready, retrying")]
    partial void LogIconAddRetry(int attempt, int total, int errorCode);

    [LoggerMessage(LogLevel.Information, "Explorer restarted (TaskbarCreated) — tray icon re-added: {Succeeded}")]
    partial void LogTaskbarRecreated(bool succeeded);

    [LoggerMessage(LogLevel.Warning, "Tray menu returned unknown command {Command} — menu map out of sync?")]
    partial void LogUnknownCommand(int command);

    [LoggerMessage(LogLevel.Error, "ItemClicked subscriber threw for tray item '{ItemId}'")]
    partial void LogSubscriberFailed(Exception ex, string itemId);

    [LoggerMessage(LogLevel.Error, "Tray window procedure threw handling message 0x{Message:X4}")]
    partial void LogWindowProcFailed(Exception ex, uint message);

    [LoggerMessage(LogLevel.Error, "Tray message pump failed")]
    partial void LogPumpFailed(Exception ex);

    #endregion
}
