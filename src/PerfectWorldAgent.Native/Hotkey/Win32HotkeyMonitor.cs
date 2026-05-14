using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Native.Internal;

namespace PerfectWorldAgent.Native.Hotkey;

// Registers global hotkeys via Win32 RegisterHotKey and raises HotkeyPressed when one fires.
//
// RegisterHotKey delivers WM_HOTKEY only to the thread that registered the hotkey, so this
// component owns a dedicated background thread that:
//   1. Registers all provided bindings on itself
//   2. Runs a GetMessage loop, raising HotkeyPressed(id) on each WM_HOTKEY
//   3. Exits cleanly when StopAsync posts WM_QUIT to its message queue
//
// Event handlers run on this monitor's message-loop thread — subscribers should keep work
// short (or marshal to another context).
[SupportedOSPlatform("windows")]
public sealed partial class Win32HotkeyMonitor : IDisposable
{
    private readonly ILogger<Win32HotkeyMonitor> _logger;

    private Thread? _messageLoopThread;
    private uint _messageLoopThreadId;
    private TaskCompletionSource? _ready;
    private IReadOnlyList<HotkeyDescriptor>? _pendingBindings;

    public event Action<int>? HotkeyPressed;

    public Win32HotkeyMonitor(ILogger<Win32HotkeyMonitor> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(IReadOnlyList<HotkeyDescriptor> bindings, CancellationToken cancellationToken = default)
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
            Name = "PWAgent-HotkeyLoop",
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
            // Shutdown deadline expired — thread is daemonized (IsBackground=true), it'll be
            // killed when the process exits.
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

        var registeredIds = new List<int>();
        try
        {
            if (_pendingBindings is not null)
            {
                foreach (var binding in _pendingBindings)
                {
                    var mods = (uint)binding.Modifiers | User32Native.MOD_NOREPEAT;
                    if (User32Native.RegisterHotKey(IntPtr.Zero, binding.Id, mods, (uint)binding.Key))
                    {
                        registeredIds.Add(binding.Id);
                        LogHotkeyRegistered(binding.Modifiers, binding.Key, binding.Id);
                    }
                    else
                    {
                        LogHotkeyRegistrationFailed(binding.Modifiers, binding.Key, Marshal.GetLastWin32Error());
                    }
                }
            }

            LogStarted(registeredIds.Count);
            _ready?.TrySetResult();

            while (User32Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != User32Native.WM_HOTKEY)
                {
                    continue;
                }

                var id = msg.wParam.ToInt32();
                LogHotkeyPressed(id);
                try
                {
                    HotkeyPressed?.Invoke(id);
                }
                catch (Exception ex)
                {
                    LogSubscriberFailed(ex, id);
                }
            }
        }
        catch (Exception ex)
        {
            LogMessageLoopFailed(ex);
            _ready?.TrySetException(ex);
        }
        finally
        {
            foreach (var id in registeredIds)
            {
                User32Native.UnregisterHotKey(IntPtr.Zero, id);
            }
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Win32HotkeyMonitor started with {Count} hotkey(s) registered")]
    partial void LogStarted(int count);

    [LoggerMessage(LogLevel.Information, "Win32HotkeyMonitor stopped")]
    partial void LogStopped();

    [LoggerMessage(LogLevel.Information, "Hotkey registered: {Modifiers}+{Key} → id {Id}")]
    partial void LogHotkeyRegistered(HotkeyModifiers modifiers, VirtualKey key, int id);

    [LoggerMessage(LogLevel.Warning, "RegisterHotKey failed for {Modifiers}+{Key} (Win32 error {ErrorCode}) — already bound by another process?")]
    partial void LogHotkeyRegistrationFailed(HotkeyModifiers modifiers, VirtualKey key, int errorCode);

    [LoggerMessage(LogLevel.Debug, "Hotkey pressed: id {Id}")]
    partial void LogHotkeyPressed(int id);

    [LoggerMessage(LogLevel.Error, "HotkeyPressed subscriber threw for id {Id}")]
    partial void LogSubscriberFailed(Exception ex, int id);

    [LoggerMessage(LogLevel.Error, "Win32HotkeyMonitor message loop failed")]
    partial void LogMessageLoopFailed(Exception ex);

    #endregion
}
