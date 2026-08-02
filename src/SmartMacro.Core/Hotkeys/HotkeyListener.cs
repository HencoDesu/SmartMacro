using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native.Hotkey;

namespace SmartMacro.Hotkeys;

/// <summary>
/// Turns global keyboard/mouse chords into macro names.
///
/// The binding set is derived entirely from the macro library: every
/// <see cref="HotkeyTrigger"/> of every graph becomes one registration whose payload is
/// that graph's NAME. There is no separate hotkey config file any more — a macro's
/// hotkey lives in the macro, so binding and behavior can never drift apart, and the
/// listener simply re-registers whenever the library changes.
///
/// Registration itself is delegated to the two Win32 monitors (RegisterHotKey for
/// keyboard chords, WH_MOUSE_LL for mouse chords); this class only owns the id → macro
/// mapping and the start/stop/suspend lifecycle.
/// </summary>
public sealed partial class HotkeyListener : IHostedService, IDisposable
{
    private readonly Win32HotkeyMonitor _keyboardMonitor;
    private readonly Win32MouseHookMonitor _mouseMonitor;
    private readonly MacroGraphStore _macros;
    private readonly ILogger<HotkeyListener> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private IReadOnlyList<HotkeyDescriptor> _keyboardDescriptors = [];
    private IReadOnlyList<MouseHookBinding> _mouseDescriptors = [];
    private Dictionary<int, string> _idToMacro = [];
    private bool _started;
    private bool _suspended;

    /// <summary>Raised with the macro NAME whose hotkey trigger just fired.</summary>
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
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        await _keyboardMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
        await _mouseMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unregisters every chord. A hotkey-picker UI needs this: Win32 RegisterHotKey
    /// swallows presses of already-bound combos, so a bound key would otherwise be
    /// impossible to re-bind. No caller until W0.3 restores the picker — kept because the
    /// constraint it works around is a property of Win32, not of the deleted dialog.
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

    /// <summary>Re-registers from the CURRENT library state (which may have changed while suspended).</summary>
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
                LogResumed(_keyboardDescriptors.Count, _mouseDescriptors.Count);
            }
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>
    /// Current id → macro-name map. Together with <see cref="KeyboardBindings"/> /
    /// <see cref="MouseBindings"/> this is the full picture of what is registered right
    /// now. Exposed for diagnostics and tests.
    /// </summary>
    public IReadOnlyDictionary<int, string> Bindings => _idToMacro;

    /// <summary>Keyboard chords currently registered (or about to be, if not started yet).</summary>
    public IReadOnlyList<HotkeyDescriptor> KeyboardBindings => _keyboardDescriptors;

    /// <summary>Mouse chords currently registered (or about to be, if not started yet).</summary>
    public IReadOnlyList<MouseHookBinding> MouseBindings => _mouseDescriptors;

    public void Dispose()
    {
        _keyboardMonitor.HotkeyPressed -= OnMonitorHotkeyPressed;
        _mouseMonitor.HotkeyPressed -= OnMonitorHotkeyPressed;
        _macros.MacrosChanged -= OnMacrosChanged;
        _restartLock.Dispose();
    }

    private void OnMonitorHotkeyPressed(int id)
    {
        // Snapshot the dictionary reference — re-registration swaps it out atomically.
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
                // ResumeAsync rebuilds from the live library, so we'd only do the work twice.
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

    // One registration per HotkeyTrigger across the whole library. Ids are positional and
    // regenerated on every rebuild — they're only ever meaningful between one
    // BuildDescriptors call and the next.
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

    [LoggerMessage(LogLevel.Debug, "Macro hotkey fired: '{Macro}'")]
    partial void LogMacroHotkey(string macro);

    [LoggerMessage(LogLevel.Warning, "Received hotkey for unknown id {Id} — binding map out of sync?")]
    partial void LogUnknownHotkeyId(int id);

    [LoggerMessage(LogLevel.Information, "Re-registering hotkeys for {MacroCount} macro(s) after a library change")]
    partial void LogReregisterStart(int macroCount);

    [LoggerMessage(LogLevel.Information, "Re-registration done — {KeyboardCount} keyboard + {MouseCount} mouse binding(s) now active")]
    partial void LogReregisterDone(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Error, "Failed to re-register hotkeys after a library change")]
    partial void LogReregisterFailed(Exception ex);

    [LoggerMessage(LogLevel.Information, "Hotkey listener suspended — all global hotkeys unregistered (rebinding UI open)")]
    partial void LogSuspended();

    [LoggerMessage(LogLevel.Information, "Hotkey listener resumed — {KeyboardCount} keyboard + {MouseCount} mouse binding(s) re-registered")]
    partial void LogResumed(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Warning, "Macro '{Macro}' has a hotkey trigger with neither Key nor MouseButton set — skipping it")]
    partial void LogMalformedTrigger(string macro);
}
