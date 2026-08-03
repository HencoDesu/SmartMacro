using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native.Hotkey;

namespace SmartMacro.Hotkeys;

/// <summary>
/// Превращает глобальные аккорды клавиатуры и мыши в имена макросов.
///
/// Набор привязок целиком выводится из библиотеки макросов: каждый
/// <see cref="HotkeyTrigger"/> каждого графа становится одной регистрацией, нагрузка которой —
/// ИМЯ этого графа. Отдельного файла с настройками хоткеев больше нет: хоткей макроса живёт в
/// самом макросе, так что привязка и поведение не могут разъехаться, а слушателю остаётся
/// просто перерегистрироваться всякий раз, когда библиотека меняется.
///
/// Сама регистрация делегирована двум Win32-мониторам (RegisterHotKey для аккордов клавиатуры,
/// WH_MOUSE_LL для аккордов мыши); этот класс владеет только отображением «id → макрос» и
/// жизненным циклом «старт / стоп / приостановка».
/// </summary>
public sealed partial class HotkeyListener : IHostedService, IHotkeyRegistration, IDisposable
{
    private readonly Win32HotkeyMonitor _keyboardMonitor;
    private readonly Win32MouseHookMonitor _mouseMonitor;
    private readonly MacroGraphStore _macros;
    private readonly ILogger<HotkeyListener> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private IReadOnlyList<HotkeyDescriptor> _keyboardDescriptors = [];
    private IReadOnlyList<MouseHookBinding> _mouseDescriptors = [];
    private Dictionary<int, string> _idToMacro = [];
    private volatile IReadOnlyList<HotkeyFailureDto> _failures = [];
    private bool _started;
    private bool _suspended;

    /// <summary>Поднимается с ИМЕНЕМ макроса, чей хоткей-триггер только что сработал.</summary>
    public event Action<string>? MacroTriggered;

    public HotkeyListener(
        MacroGraphStore macros,
        Win32HotkeyMonitor keyboardMonitor,
        Win32MouseHookMonitor mouseMonitor,
        ILogger<HotkeyListener> logger)
    {
        _keyboardMonitor = keyboardMonitor;
        _mouseMonitor = mouseMonitor;
        _macros = macros;
        _logger = logger;

        BuildDescriptors(_macros.All);
        _keyboardMonitor.HotkeyPressed += OnMonitorHotkeyPressed;
        _mouseMonitor.HotkeyPressed += OnMonitorHotkeyPressed;
        _macros.MacrosChanged += OnMacrosChanged;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _keyboardMonitor.StartAsync(_keyboardDescriptors, cancellationToken).ConfigureAwait(false);
        await _mouseMonitor.StartAsync(_mouseDescriptors, cancellationToken).ConfigureAwait(false);
        _started = true;
        CollectFailures();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        await _keyboardMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
        await _mouseMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Снимает регистрацию со всех аккордов. Интерфейсу выбора хоткея без этого не обойтись:
    /// Win32 RegisterHotKey проглатывает нажатия уже привязанных сочетаний, так что привязанную
    /// клавишу иначе было бы невозможно переназначить. Вызывается из процесса UI по IPC
    /// (<c>SuspendHotkeys</c>).
    /// </summary>
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_suspended)
            {
                return;
            }

            _suspended = true;
            if (_started)
            {
                LogSuspended();
                await _keyboardMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
                await _mouseMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>Регистрирует заново по ТЕКУЩЕМУ состоянию библиотеки (пока висела приостановка, оно могло измениться).</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_suspended)
            {
                return;
            }

            _suspended = false;
            if (_started)
            {
                BuildDescriptors(_macros.All);
                await _keyboardMonitor.StartAsync(_keyboardDescriptors, cancellationToken).ConfigureAwait(false);
                await _mouseMonitor.StartAsync(_mouseDescriptors, cancellationToken).ConfigureAwait(false);
                CollectFailures();
                LogResumed(_keyboardDescriptors.Count, _mouseDescriptors.Count);
            }
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>
    /// Текущее отображение «id → имя макроса». Вместе с <see cref="KeyboardBindings"/> и
    /// <see cref="MouseBindings"/> это полная картина того, что зарегистрировано прямо сейчас.
    /// Открыто наружу ради диагностики и тестов.
    /// </summary>
    public IReadOnlyDictionary<int, string> Bindings => _idToMacro;

    /// <summary>Аккорды клавиатуры, зарегистрированные сейчас (или вот-вот, если старта ещё не было).</summary>
    public IReadOnlyList<HotkeyDescriptor> KeyboardBindings => _keyboardDescriptors;

    /// <summary>Аккорды мыши, зарегистрированные сейчас (или вот-вот, если старта ещё не было).</summary>
    public IReadOnlyList<MouseHookBinding> MouseBindings => _mouseDescriptors;

    /// <inheritdoc />
    public IReadOnlyList<HotkeyFailureDto> Failures => _failures;

    public void Dispose()
    {
        _keyboardMonitor.HotkeyPressed -= OnMonitorHotkeyPressed;
        _mouseMonitor.HotkeyPressed -= OnMonitorHotkeyPressed;
        _macros.MacrosChanged -= OnMacrosChanged;
        _restartLock.Dispose();
    }

    private void OnMonitorHotkeyPressed(int id)
    {
        // Снимаем ссылку на словарь — перерегистрация подменяет его целиком и атомарно.
        var map = _idToMacro;
        if (map.TryGetValue(id, out var macroName))
        {
            LogMacroHotkey(macroName);
            MacroTriggered?.Invoke(macroName);
        }
        else
        {
            LogUnknownHotkeyId(id);
        }
    }

    private void OnMacrosChanged(IReadOnlyList<MacroGraph> macros)
    {
        _ = RestartAsync(macros, CancellationToken.None);
    }

    private async Task RestartAsync(IReadOnlyList<MacroGraph> macros, CancellationToken cancellationToken)
    {
        await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_suspended)
            {
                // ResumeAsync всё равно пересоберёт всё по живой библиотеке — так что сейчас мы
                // сделали бы ту же работу дважды.
                return;
            }

            if (!_started)
            {
                BuildDescriptors(macros);
                return;
            }

            LogReregisterStart(macros.Count);
            await _keyboardMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            await _mouseMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            BuildDescriptors(macros);
            await _keyboardMonitor.StartAsync(_keyboardDescriptors, cancellationToken).ConfigureAwait(false);
            await _mouseMonitor.StartAsync(_mouseDescriptors, cancellationToken).ConfigureAwait(false);
            CollectFailures();
            LogReregisterDone(_keyboardDescriptors.Count, _mouseDescriptors.Count);
        }
        catch (Exception ex)
        {
            LogReregisterFailed(ex);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    // По одной регистрации на каждый HotkeyTrigger во всей библиотеке. Идентификаторы
    // позиционные и генерируются заново при каждой пересборке — они осмысленны только в
    // промежутке между одним вызовом BuildDescriptors и следующим.
    private void BuildDescriptors(IReadOnlyList<MacroGraph> macros)
    {
        var keyboard = new List<HotkeyDescriptor>();
        var mouse = new List<MouseHookBinding>();
        var map = new Dictionary<int, string>();
        var nextId = 1;

        foreach (var macro in macros)
        {
            foreach (var trigger in macro.Triggers.OfType<HotkeyTrigger>())
            {
                if (trigger.IsMouse)
                {
                    var id = nextId++;
                    mouse.Add(new MouseHookBinding(id, trigger.Modifiers, trigger.MouseButton));
                    map[id] = macro.Name;
                }
                else if (trigger.IsKeyboard)
                {
                    var id = nextId++;
                    keyboard.Add(new HotkeyDescriptor(id, trigger.Modifiers, trigger.Key));
                    map[id] = macro.Name;
                }
                else
                {
                    LogMalformedTrigger(macro.Name);
                }
            }
        }

        _keyboardDescriptors = keyboard;
        _mouseDescriptors = mouse;
        _idToMacro = map;
    }

    /// <summary>
    /// Превращает отвергнутые дескрипторы клавиатурного монитора обратно в «аккорд макроса X
    /// так и не взлетел». Вызывается после каждой попытки регистрации — и только тогда. Список
    /// обязан пережить приостановку: панель читает его ровно в то время, когда редактор (а
    /// значит, и приостановка) на экране.
    ///
    /// Идентификаторы здесь — те самые, что только что раздал <see cref="BuildDescriptors"/>,
    /// так что поиск не может устареть: обе стороны генерируются заново вместе. Аккорд, за
    /// id которого почему-то не нашлось макроса, выбрасывается, а не докладывается с пустым
    /// именем.
    /// </summary>
    private void CollectFailures()
    {
        var rejected = _keyboardMonitor.RejectedBindings;
        if (rejected.Count == 0)
        {
            _failures = [];
            return;
        }

        var map = _idToMacro;
        var failures = new List<HotkeyFailureDto>(rejected.Count);
        foreach (var descriptor in rejected)
        {
            if (map.TryGetValue(descriptor.Id, out var macro))
            {
                failures.Add(new HotkeyFailureDto(macro, descriptor.Modifiers, descriptor.Key));
            }
        }

        _failures = failures;
        if (failures.Count > 0)
        {
            LogRegistrationFailures(failures.Count);
        }
    }

    [LoggerMessage(LogLevel.Debug, "Сработал хоткей макроса: '{Macro}'")]
    partial void LogMacroHotkey(string macro);

    [LoggerMessage(LogLevel.Warning, "Пришёл хоткей с неизвестным id {Id} — таблица привязок рассинхронизирована?")]
    partial void LogUnknownHotkeyId(int id);

    [LoggerMessage(LogLevel.Information, "Перерегистрируем хоткеи после изменения библиотеки, макросов: {MacroCount}")]
    partial void LogReregisterStart(int macroCount);

    [LoggerMessage(LogLevel.Information,
        "Перерегистрация закончена — активно привязок: клавиатурных {KeyboardCount} + мышиных {MouseCount}")]
    partial void LogReregisterDone(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Error, "Не удалось перерегистрировать хоткеи после изменения библиотеки")]
    partial void LogReregisterFailed(Exception ex);

    [LoggerMessage(LogLevel.Information,
        "Слушатель хоткеев приостановлен — все глобальные сочетания сняты (открыт редактор привязок)")]
    partial void LogSuspended();

    [LoggerMessage(LogLevel.Information,
        "Слушатель хоткеев возобновлён — заново зарегистрировано привязок: клавиатурных {KeyboardCount} + мышиных {MouseCount}")]
    partial void LogResumed(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Warning,
        "У макроса '{Macro}' хоткей-триггер без Key и без MouseButton — пропускаем")]
    partial void LogMalformedTrigger(string macro);

    [LoggerMessage(LogLevel.Warning,
        "Не удалось зарегистрировать сочетаний: {Count} — их занимает другое приложение; панель покажет их через GetHotkeyFailures")]
    partial void LogRegistrationFailures(int count);
}
