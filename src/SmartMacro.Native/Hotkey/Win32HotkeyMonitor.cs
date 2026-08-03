using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Hotkey;

// Регистрирует глобальные горячие клавиши через Win32 RegisterHotKey и поднимает
// HotkeyPressed при срабатывании.
//
// RegisterHotKey доставляет WM_HOTKEY только тому потоку, который зарегистрировал горячую
// клавишу, поэтому компонент владеет отдельным фоновым потоком, который:
//   1. Регистрирует на себе все переданные привязки
//   2. Крутит цикл GetMessage, поднимая HotkeyPressed(id) на каждом WM_HOTKEY
//   3. Аккуратно выходит, когда StopAsync кладёт WM_QUIT в его очередь сообщений
//
// Обработчики события выполняются в потоке цикла сообщений этого монитора — подписчикам
// стоит держать работу короткой (или перебрасывать её в другой контекст).
[SupportedOSPlatform("windows")]
public sealed partial class Win32HotkeyMonitor : IDisposable
{
    private readonly ILogger<Win32HotkeyMonitor> _logger;

    private Thread? _messageLoopThread;
    private uint _messageLoopThreadId;
    private TaskCompletionSource? _ready;
    private IReadOnlyList<HotkeyDescriptor>? _pendingBindings;
    private volatile IReadOnlyList<HotkeyDescriptor> _rejected = [];

    /// <summary>
    /// Поднимается в потоке цикла сообщений, когда срабатывает любая зарегистрированная
    /// горячая клавиша. Подписчики получают <c>Id</c> привязки (то самое значение, которое
    /// передали при регистрации) и сами отвечают за то, чтобы сопоставить его со смыслом.
    /// </summary>
    public event Action<int>? HotkeyPressed;

    public Win32HotkeyMonitor(ILogger<Win32HotkeyMonitor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Привязки, которые последний <see cref="StartAsync"/> зарегистрировать НЕ смог,
    /// потому что этот аккорд у Windows уже занят — другим приложением, другим нашим окном
    /// или это комбинация, зарезервированная оболочкой (Win+L и ей подобные).
    ///
    /// Выставлено наружу, потому что иначе такая неудача для пользователя абсолютно
    /// беззвучна: интерфейс показывает привязанную горячую клавишу, а клавиша просто
    /// никогда ничего не делает. Список пишется в потоке цикла сообщений до сигнала
    /// готовности, поэтому к моменту завершения задачи <see cref="StartAsync"/> он уже
    /// окончательный; <see cref="StopAsync"/> его НЕ очищает, так что приостановленный
    /// слушатель по-прежнему может рассказать, что было не так в прошлый запуск.
    /// </summary>
    public IReadOnlyList<HotkeyDescriptor> RejectedBindings => _rejected;

    /// <summary>
    /// Поднимает отдельный поток с циклом сообщений, регистрирует каждую привязку через
    /// <c>RegisterHotKey</c> и возвращает управление, когда поток сообщает о готовности
    /// принимать события горячих клавиш. Ожидание сигнала готовности можно прервать через
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Монитор уже запущен.</exception>
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
            Name = "SmartMacro-HotkeyLoop",
        };
        _messageLoopThread.Start();

        return _ready.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Сигнализирует потоку цикла сообщений о выходе (кладёт <c>WM_QUIT</c>) и ждёт, пока он
    /// доработает. Зарегистрированные горячие клавиши снимаются в блоке <c>finally</c> этого
    /// потока. Вызывать на уже остановленном мониторе безопасно.
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
            // Срок на завершение вышел — поток фоновый (IsBackground=true), его прибьёт при
            // выходе из процесса.
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
        var rejected = new List<HotkeyDescriptor>();
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
                        rejected.Add(binding);
                        LogHotkeyRegistrationFailed(binding.Modifiers, binding.Key, Marshal.GetLastWin32Error());
                    }
                }
            }

            // Публикуем до сигнала готовности, чтобы вызвавший StartAsync мог прочитать уже
            // устоявшийся список ровно в тот момент, когда его задача завершится.
            _rejected = rejected;
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

    [LoggerMessage(LogLevel.Information, "Win32HotkeyMonitor запущен, зарегистрировано горячих клавиш: {Count}")]
    partial void LogStarted(int count);

    [LoggerMessage(LogLevel.Information, "Win32HotkeyMonitor остановлен")]
    partial void LogStopped();

    [LoggerMessage(LogLevel.Information, "Горячая клавиша зарегистрирована: {Modifiers}+{Key} → id {Id}")]
    partial void LogHotkeyRegistered(HotkeyModifiers modifiers, VirtualKey key, int id);

    [LoggerMessage(LogLevel.Warning,
        "RegisterHotKey отказал для {Modifiers}+{Key} (ошибка Win32 {ErrorCode}) — аккорд занят другим процессом?")]
    partial void LogHotkeyRegistrationFailed(HotkeyModifiers modifiers, VirtualKey key, int errorCode);

    [LoggerMessage(LogLevel.Debug, "Нажата горячая клавиша: id {Id}")]
    partial void LogHotkeyPressed(int id);

    [LoggerMessage(LogLevel.Error, "Подписчик HotkeyPressed бросил исключение на id {Id}")]
    partial void LogSubscriberFailed(Exception ex, int id);

    [LoggerMessage(LogLevel.Error, "Цикл сообщений Win32HotkeyMonitor упал")]
    partial void LogMessageLoopFailed(Exception ex);

    #endregion
}
