using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Tray;

// A notification-area ("tray") icon built straight on Shell_NotifyIcon — no Avalonia, no
// WinForms, no UI framework of any kind. That's the whole point: the daemon is a windowless
// background process, and pulling in a toolkit just to draw one 16×16 icon would cost it
// tens of megabytes of managed heap plus a render loop it never uses.
//
// Structure mirrors Win32HotkeyMonitor, because the constraint is the same: Shell_NotifyIcon
// posts its mouse notifications to a window, and window messages are delivered only to the
// thread that created the window. So this component owns a dedicated background thread that:
//   1. Registers a private window class and creates a message-only window (HWND_MESSAGE
//      parent — never shown, never enumerated, never given input focus)
//   2. Builds the popup menu and adds the icon (NIM_ADD)
//   3. Runs a GetMessage loop, translating the icon's callback message into ItemClicked
//   4. Exits cleanly when Stop() posts WM_QUIT to its message queue, tearing the icon,
//      menu, window and class back down in its finally block
//
// ⚠ ItemClicked is raised ON THE PUMP THREAD, from inside the menu's own modal loop.
// Handlers must return immediately — anything slow (process launch, host shutdown, I/O)
// belongs on the thread pool. A blocking handler freezes the tray icon and, because
// TrackPopupMenuEx is still on the stack, can wedge the shell's input processing.
[SupportedOSPlatform("windows")]
public sealed partial class Win32TrayIcon : IDisposable
{
    // Private callback message. Shell_NotifyIcon reserves WM_APP..0xBFFF for exactly this;
    // the mouse event that triggered it arrives in the low word of lParam.
    private const uint WM_TRAY_CALLBACK = User32Native.WM_APP + 1;

    // Single icon per instance, so a constant id is enough to address it in NIM_MODIFY /
    // NIM_DELETE.
    private const uint TrayIconId = 1;

    private const int StopTimeoutSeconds = 5;

    // Shell_NotifyIcon(NIM_ADD) fails while the shell isn't accepting registrations — the
    // normal case when the daemon autostarts ahead of explorer.exe. Retry briefly rather
    // than dying: the tray is the only way for a user to quit a windowless process.
    private const int AddIconAttempts = 5;
    private const int AddIconRetryDelayMs = 500;

    private readonly ILogger<Win32TrayIcon> _logger;
    private readonly Lock _lifecycleLock = new();

    private Thread? _pumpThread;
    private uint _pumpThreadId;

    // Owned by the pump thread once it starts; only touched from there.
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
    /// Raised with the <see cref="TrayMenuItem.Id"/> of the chosen entry (or of the default
    /// entry on a double-click of the icon).
    /// </summary>
    /// <remarks>
    /// Invoked on the tray's message-pump thread while the menu's modal loop is still on the
    /// stack. Handlers MUST return promptly — queue real work elsewhere. Exceptions escaping
    /// a handler are caught and logged rather than killing the pump.
    /// </remarks>
    public event Action<string>? ItemClicked;

    public Win32TrayIcon(ILogger<Win32TrayIcon> logger)
    {
        _logger = logger;
    }

    /// <summary>Whether the pump thread is currently running.</summary>
    public bool IsRunning => Volatile.Read(ref _pumpThread) is not null;

    /// <summary>
    /// Spawns the pump thread, creates the message-only window and shows the icon. Blocks
    /// until the icon is live (or the pump failed, in which case its exception is rethrown
    /// here).
    /// </summary>
    /// <param name="tooltip">Hover text; truncated to 127 characters, the Win32 limit.</param>
    /// <param name="iconPath">
    /// Path to a <c>.ico</c> file. Missing or unreadable files fall back to the stock
    /// <c>IDI_APPLICATION</c> icon with a warning — a broken icon must never stop the daemon
    /// from having a tray presence, since that's the only way to quit it.
    /// </param>
    /// <param name="items">Context-menu entries, in display order.</param>
    /// <exception cref="InvalidOperationException">Already started.</exception>
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

            // Start parameters travel through the closure rather than through fields: a
            // Stop() racing this Start() would otherwise be able to null the shared TCS
            // before the pump ever read it, leaving the line below waiting forever.
            var tooltipCopy = tooltip ?? string.Empty;
            var iconPathCopy = iconPath ?? string.Empty;
            _pumpThread = new Thread(() => MessageLoop(ready, tooltipCopy, iconPathCopy, items))
            {
                IsBackground = true,
                Name = "SmartMacro-TrayLoop",
            };
            // The shell's notification area is COM/OLE territory; an STA pump is the
            // conventional (and safest) home for a notify icon.
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();
        }

        // The pump always completes this: with a result once the icon is added, with an
        // exception on failure, or cancelled by its finally block if it somehow unwound
        // before signalling. So this can't hang on a dead thread.
        ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Posts <c>WM_QUIT</c> to the pump and waits for it to tear everything down (icon
    /// removed, window destroyed, class unregistered). Safe to call repeatedly and from any
    /// thread; a second call while stopped is a no-op.
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
            // Daemonised thread (IsBackground = true) — process exit reaps it, and the shell
            // drops orphaned icons when their owning process dies.
            LogStopTimedOut(StopTimeoutSeconds);
            return;
        }

        LogStopped();
    }

    public void Dispose() => Stop();

    // ─── Pump thread ───

    private void MessageLoop(
        TaskCompletionSource ready,
        string tooltip,
        string iconPath,
        IReadOnlyList<TrayMenuItem> items)
    {
        var threadId = Kernel32Native.GetCurrentThreadId();
        lock (_lifecycleLock)
        {
            // Stop() may already have raced past us and cleared _pumpThread. Publishing the
            // id anyway would let a later Stop() post WM_QUIT to a thread that has moved on;
            // instead we leave it zero and unwind through the finally below.
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

            // Registered before the first NIM_ADD so we can't miss the broadcast while
            // retrying against a shell that's still coming up.
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
            // Backstop: guarantees Start() never blocks forever if the pump unwound before
            // signalling. No-op once the TCS is already completed.
            ready.TrySetCanceled();
            Cleanup();
        }
    }

    private void CreateMessageWindow()
    {
        var hInstance = Kernel32Native.GetModuleHandle(null);

        // Class names are process-global. A GUID suffix keeps two instances (or a restart
        // racing its own cleanup) from colliding on ERROR_CLASS_ALREADY_EXISTS.
        _className = $"SmartMacroTray_{Guid.NewGuid():N}";
        _classNamePtr = Marshal.StringToHGlobalUni(_className);

        // Rooted in a field for the lifetime of the class registration — Windows holds a
        // raw thunk pointer and knows nothing about the GC.
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

        // Stock icons are shared system resources — never DestroyIcon them.
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

        // Command ids are 1-based: TrackPopupMenuEx with TPM_RETURNCMD reports 0 for
        // "dismissed without choosing", so 0 can't be a real item.
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
            // fByPos = 0 ⇒ uItem is a command id, not an index.
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

    // Runs on the pump thread, called by Windows. Must never let an exception cross back
    // into native code — that's an immediate process kill, not a catchable failure.
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // Not a compile-time constant (RegisterWindowMessage assigns it at runtime), so
            // it can't be a switch label. Explorer restarting wipes every icon in the
            // notification area; this broadcast is the shell telling owners to re-add theirs.
            if (msg != 0 && msg == _taskbarCreatedMessage)
            {
                LogTaskbarRecreated(TryAddNotifyIcon());
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_TRAY_CALLBACK:
                    // The originating mouse message lives in the low word of lParam.
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

        // KB135788, half one: without foreground ownership the menu never receives the
        // click that should dismiss it and hangs around after the user clicks away.
        User32Native.SetForegroundWindow(_hwnd);

        var command = User32Native.TrackPopupMenuEx(
            _menu,
            User32Native.TPM_RETURNCMD | User32Native.TPM_RIGHTBUTTON | User32Native.TPM_NONOTIFY
                | User32Native.TPM_LEFTALIGN | User32Native.TPM_BOTTOMALIGN,
            cursor.X,
            cursor.Y,
            _hwnd,
            IntPtr.Zero);

        // KB135788, half two: a dummy message so the menu's internal modal loop notices it
        // should exit.
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

    // Everything the pump allocated, released in reverse order. Runs in the pump thread's
    // finally block, so it also covers a mid-startup failure (each step is guarded by its
    // own handle check).
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
