using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Hotkey;

// Зеркало Win32HotkeyMonitor для кнопок мыши: вместо RegisterHotKey (который умеет только
// клавиатуру) используется низкоуровневый хук WH_MOUSE_LL.
//
// Хук общесистемный: колбэк срабатывает на КАЖДОЕ событие мыши раньше, чем его увидит хоть
// одно окно. Отсюда два следствия:
//   1. Колбэк обязан отрабатывать быстро — медленный колбэк придушивает ввод мышью во всей
//      системе. Мы всего лишь проверяем таблицу привязок и поднимаем событие (само по себе
//      это тоже быстро: подписчики пишут в каналы, а не блокируются).
//   2. Хук срабатывает только в потоке, который активно прокачивает сообщения. Поэтому мы
//      владеем отдельным фоновым потоком, который ставит хук на себя и крутит GetMessage.
//      Модель ровно та же, что у Win32HotkeyMonitor, — так эргономика завершения
//      (PostThreadMessage WM_QUIT) остаётся единообразной.
//
// Исходное событие мыши мы не подавляем (возвращаем CallNextHookEx, а не 1). То есть нажатие
// Mouse4 по-прежнему делает то, что Mouse4 обычно делает, — «назад» в браузере и так далее.
// Обычно у пользователя Mouse4/5 под игровое действие ни к чему не привязаны, так что
// единственный эффект — срабатывание нашей рассылки.
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

    // Держатель сильной ссылки на неуправляемый колбэк. SetWindowsHookEx хранит сырой
    // указатель на функцию; если делегат соберёт GC, следующее событие вызовет освобождённую
    // память и уронит весь процесс. Держим ссылку всё время, пока хук установлен.
    private User32Native.HookProc? _hookDelegate;

    /// <summary>
    /// Поднимается в потоке цикла сообщений хука, когда срабатывает зарегистрированная
    /// мышиная привязка. Подписчики получают <c>Id</c> привязки и сами сопоставляют его со
    /// своим смыслом.
    /// </summary>
    public event Action<int>? HotkeyPressed;

    public Win32MouseHookMonitor(ILogger<Win32MouseHookMonitor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Поднимает отдельный поток с циклом сообщений, ставит на нём низкоуровневый мышиный
    /// хук <c>WH_MOUSE_LL</c> и возвращает управление, когда поток сообщает о готовности.
    /// Сам хук общесистемный; цикл сообщений нужен только потому, что Windows вызывает
    /// колбэк хука во время диспетчеризации сообщений в потоке, который его поставил.
    /// </summary>
    /// <exception cref="InvalidOperationException">Монитор уже запущен.</exception>
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
            Name = "SmartMacro-MouseHookLoop",
        };
        _messageLoopThread.Start();

        return _ready.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Сигнализирует потоку цикла сообщений о выходе (<c>WM_QUIT</c>) и ждёт, пока тот снимет
    /// хук в своём блоке <c>finally</c>. Вызывать на уже остановленном мониторе безопасно.
    /// </summary>
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
            // Срок на завершение вышел — поток фоновый, его прибьёт при выходе из процесса.
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

        // Держим делегат живым всё время жизни хука (см. комментарий у поля).
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

            // GetMessage блокируется до WM_QUIT — но колбэк хука срабатывает в этом же
            // потоке во время диспетчеризации сообщений, поэтому насос нужен, даже если мы
            // сами не отправляем ни одного сообщения.
            while (User32Native.GetMessage(out var _, IntPtr.Zero, 0, 0) > 0)
            {
                // Пусто — колбэк хука ОС вызывает синхронно в циклах GetMessage / PeekMessage,
                // а не через сообщение, которое мы бы здесь обработали.
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
        // nCode < 0 ⇒ документация требует ничего не обрабатывать и просто передать дальше
        // по цепочке.
        if (nCode != User32Native.HC_ACTION)
        {
            return User32Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // Нас интересуют события нажатия средней кнопки и XButton. Левая и правая
        // игнорируются намеренно — см. комментарий в MouseButton.cs.
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
                // Номер XButton лежит в старшем слове mouseData. Приводим через ushort,
                // чтобы срезать знак и совпасть с базовым типом MouseButton.
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
            // Ни в коем случае не выпускаем исключение из колбэка хука — это убивает весь
            // процесс. Пишем в лог и пропускаем событие дальше.
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
