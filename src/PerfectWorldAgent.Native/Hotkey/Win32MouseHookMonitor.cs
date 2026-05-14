using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Native.Internal;

namespace PerfectWorldAgent.Native.Hotkey;

// Mirror of Win32HotkeyMonitor for mouse buttons, using a WH_MOUSE_LL low-level hook
// instead of RegisterHotKey (which is keyboard-only).
//
// The hook is system-wide: the callback fires for EVERY mouse event before any window
// sees it. Two consequences:
//   1. The hook callback must finish fast — slow callbacks throttle all mouse input
//      across the entire system. We just check the binding table and Invoke the event
//      (which is itself fast since subscribers post to channels rather than block).
//   2. The hook only fires on a thread that's actively pumping messages. So we own a
//      dedicated background thread that installs the hook on itself and runs GetMessage.
//      Identical to Win32HotkeyMonitor's model — keeps shutdown ergonomics (PostThreadMessage
//      WM_QUIT) consistent.
//
// We don't suppress the original mouse event (return CallNextHookEx, not 1). So pressing
// Mouse4 still does whatever Mouse4 normally does — browser back, etc. The user typically
// has Mouse4/5 unbound for their game-action, so the only effect is our broadcast firing.
[SupportedOSPlatform("windows")]
public sealed partial class Win32MouseHookMonitor : IDisposable
{
    private readonly ILogger<Win32MouseHookMonitor> _logger;

    private Thread? _messageLoopThread;
    private uint _messageLoopThreadId;
    private TaskCompletionSource? _ready;
    private IReadOnlyList<MouseHookBinding>? _pendingBindings;
    private MouseHookBinding[] _activeBindings = Array.Empty<MouseHookBinding>();
    private IntPtr _hookHandle;

    // Strong reference holder for the unmanaged callback. SetWindowsHookEx stores a raw
    // function pointer; if the delegate gets GC'd, the next event calls into freed memory
    // and the whole process crashes. Keep this as long as the hook is installed.
    private User32Native.HookProc? _hookDelegate;

    public event Action<int>? HotkeyPressed;

    public Win32MouseHookMonitor(ILogger<Win32MouseHookMonitor> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(IReadOnlyList<MouseHookBinding> bindings, CancellationToken cancellationToken = default)
    {
        if (_messageLoopThread is not null)
        {
            throw new InvalidOperationException("Monitor already started.");
        }

        _pendingBindings = bindings;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _messageLoopThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "PWAgent-MouseHookLoop",
        };
        _messageLoopThread.Start();

        return _ready.Task.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_messageLoopThread is null)
        {
            return;
        }

        if (_messageLoopThreadId != 0)
        {
            User32Native.PostThreadMessage(_messageLoopThreadId, User32Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        try
        {
            await Task.Run(() => _messageLoopThread.Join(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown deadline expired — daemon thread, killed at process exit.
        }
        finally
        {
            _messageLoopThread = null;
            _messageLoopThreadId = 0;
            _ready = null;
            _pendingBindings = null;
            LogStopped();
        }
    }

    public void Dispose()
    {
        if (_messageLoopThreadId != 0)
        {
            User32Native.PostThreadMessage(_messageLoopThreadId, User32Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void MessageLoop()
    {
        _messageLoopThreadId = Kernel32Native.GetCurrentThreadId();
        _activeBindings = _pendingBindings?.ToArray() ?? Array.Empty<MouseHookBinding>();

        // Keep the delegate alive for the lifetime of the hook (see field comment).
        _hookDelegate = MouseProc;
        try
        {
            var hMod = Kernel32Native.GetModuleHandle(null);
            _hookHandle = User32Native.SetWindowsHookEx(User32Native.WH_MOUSE_LL, _hookDelegate, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                LogHookInstallFailed(err);
                _ready?.TrySetException(new InvalidOperationException(
                    $"SetWindowsHookEx(WH_MOUSE_LL) failed with Win32 error {err}"));
                return;
            }

            LogStarted(_activeBindings.Length);
            _ready?.TrySetResult();

            // GetMessage blocks until WM_QUIT — but the hook callback fires on this
            // thread during message dispatch, so a pump is required even if we never
            // post any messages of our own.
            while (User32Native.GetMessage(out var _, IntPtr.Zero, 0, 0) > 0)
            {
                // No-op — the hook callback is invoked synchronously by the OS during
                // GetMessage / PeekMessage cycles, not via a message we'd handle here.
            }
        }
        catch (Exception ex)
        {
            LogMessageLoopFailed(ex);
            _ready?.TrySetException(ex);
        }
        finally
        {
            if (_hookHandle != IntPtr.Zero)
            {
                User32Native.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            _hookDelegate = null;
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 ⇒ docs require we don't process and just chain.
        if (nCode != User32Native.HC_ACTION)
        {
            return User32Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // We care about Middle and XButton down-events. Left/Right are deliberately
        // ignored — see MouseButton.cs comment.
        var message = (uint)wParam.ToInt32();
        MouseButton button;
        try
        {
            if (message == User32Native.WM_MBUTTONDOWN)
            {
                button = MouseButton.Middle;
            }
            else if (message == User32Native.WM_XBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // XButton id is in the high word of mouseData. Cast through ushort to
                // strip the sign and align with MouseButton's underlying type.
                var xButton = (ushort)((data.mouseData >> 16) & 0xFFFF);
                button = xButton switch
                {
                    0x0001 => MouseButton.XButton1,
                    0x0002 => MouseButton.XButton2,
                    _ => MouseButton.None,
                };
            }
            else
            {
                return User32Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (button == MouseButton.None)
            {
                return User32Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            var mods = ReadModifierState();
            foreach (var binding in _activeBindings)
            {
                if (binding.Button == button && binding.Modifiers == mods)
                {
                    LogMousePressed(button, mods, binding.Id);
                    try
                    {
                        HotkeyPressed?.Invoke(binding.Id);
                    }
                    catch (Exception ex)
                    {
                        LogSubscriberFailed(ex, binding.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception escape the hook callback — that kills the whole
            // process. Log and pass through.
            LogHookCallbackFailed(ex);
        }

        return User32Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static HotkeyModifiers ReadModifierState()
    {
        var mods = HotkeyModifiers.None;
        if (IsDown(User32Native.VK_CONTROL)) mods |= HotkeyModifiers.Control;
        if (IsDown(User32Native.VK_SHIFT)) mods |= HotkeyModifiers.Shift;
        if (IsDown(User32Native.VK_MENU)) mods |= HotkeyModifiers.Alt;
        if (IsDown(User32Native.VK_LWIN) || IsDown(User32Native.VK_RWIN)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    private static bool IsDown(int vk) => (User32Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    #region Logging

    [LoggerMessage(LogLevel.Information, "Win32MouseHookMonitor started — {Count} mouse binding(s) active")]
    partial void LogStarted(int count);

    [LoggerMessage(LogLevel.Information, "Win32MouseHookMonitor stopped")]
    partial void LogStopped();

    [LoggerMessage(LogLevel.Error, "SetWindowsHookEx(WH_MOUSE_LL) failed with Win32 error {ErrorCode}")]
    partial void LogHookInstallFailed(int errorCode);

    [LoggerMessage(LogLevel.Debug, "Mouse hotkey matched: {Button} (mods {Modifiers}) → id {Id}")]
    partial void LogMousePressed(MouseButton button, HotkeyModifiers modifiers, int id);

    [LoggerMessage(LogLevel.Error, "MouseHook subscriber threw for id {Id}")]
    partial void LogSubscriberFailed(Exception ex, int id);

    [LoggerMessage(LogLevel.Error, "MouseHook callback failed; passing through to next hook")]
    partial void LogHookCallbackFailed(Exception ex);

    [LoggerMessage(LogLevel.Error, "Win32MouseHookMonitor message loop failed")]
    partial void LogMessageLoopFailed(Exception ex);

    #endregion
}
