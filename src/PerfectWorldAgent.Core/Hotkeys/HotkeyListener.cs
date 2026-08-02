using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Native.Hotkey;
using PerfectWorldAgent.Orchestration;

namespace PerfectWorldAgent.Hotkeys;

// Thin adapter on top of Native.Hotkey monitors: combines BOTH trigger bindings and
// macro bindings into a single id-based registration with the Win32 monitors. On a
// fired id we look it up in two maps:
//   * id → OrchestratorTrigger → HotkeyPressed event
//   * id → macro name          → MacroHotkeyPressed event
public sealed partial class HotkeyListener : IHostedService, IDisposable
{
    private readonly Win32HotkeyMonitor _keyboardMonitor;
    private readonly Win32MouseHookMonitor _mouseMonitor;
    private readonly HotkeyConfigStore _configStore;
    private readonly ILogger<HotkeyListener> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private IReadOnlyList<HotkeyDescriptor> _keyboardDescriptors = Array.Empty<HotkeyDescriptor>();
    private IReadOnlyList<MouseHookBinding> _mouseDescriptors = Array.Empty<MouseHookBinding>();
    private Dictionary<int, OrchestratorTrigger> _idToTrigger = new();
    private Dictionary<int, string> _idToMacro = new();
    private bool _started;
    private bool _suspended;

    public event Action<OrchestratorTrigger>? HotkeyPressed;
    public event Action<string>? MacroHotkeyPressed;

    public HotkeyListener(
        HotkeyConfigStore configStore,
        Win32HotkeyMonitor keyboardMonitor,
        Win32MouseHookMonitor mouseMonitor,
        ILogger<HotkeyListener> logger)
    {
        _keyboardMonitor = keyboardMonitor;
        _mouseMonitor = mouseMonitor;
        _configStore = configStore;
        _logger = logger;

        BuildDescriptors(_configStore.Bindings, _configStore.MacroBindings);
        _keyboardMonitor.HotkeyPressed += OnMonitorHotkeyPressed;
        _mouseMonitor.HotkeyPressed += OnMonitorHotkeyPressed;
        _configStore.BindingsChanged += OnBindingsChanged;
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

    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_suspended) return;
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

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_suspended) return;
            _suspended = false;
            if (_started)
            {
                BuildDescriptors(_configStore.Bindings, _configStore.MacroBindings);
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

    public void Dispose()
    {
        _keyboardMonitor.HotkeyPressed -= OnMonitorHotkeyPressed;
        _mouseMonitor.HotkeyPressed -= OnMonitorHotkeyPressed;
        _configStore.BindingsChanged -= OnBindingsChanged;
        _restartLock.Dispose();
    }

    private void OnMonitorHotkeyPressed(int id)
    {
        // Snapshot dict references — re-registration may swap them out atomically.
        var triggerMap = _idToTrigger;
        var macroMap = _idToMacro;
        if (triggerMap.TryGetValue(id, out var trigger))
        {
            LogHotkeyTrigger(trigger);
            HotkeyPressed?.Invoke(trigger);
        }
        else if (macroMap.TryGetValue(id, out var macroName))
        {
            LogMacroHotkey(macroName);
            MacroHotkeyPressed?.Invoke(macroName);
        }
        else
        {
            LogUnknownHotkeyId(id);
        }
    }

    private void OnBindingsChanged(IReadOnlyList<HotkeyBinding> bindings, IReadOnlyList<MacroHotkeyBinding> macroBindings)
    {
        _ = RestartAsync(bindings, macroBindings, CancellationToken.None);
    }

    private async Task RestartAsync(
        IReadOnlyList<HotkeyBinding> bindings,
        IReadOnlyList<MacroHotkeyBinding> macroBindings,
        CancellationToken cancellationToken)
    {
        await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_suspended) return;

            if (!_started)
            {
                BuildDescriptors(bindings, macroBindings);
                return;
            }

            LogReregisterStart(bindings.Count, macroBindings.Count);
            await _keyboardMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            await _mouseMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            BuildDescriptors(bindings, macroBindings);
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

    private void BuildDescriptors(
        IReadOnlyList<HotkeyBinding> bindings,
        IReadOnlyList<MacroHotkeyBinding> macroBindings)
    {
        var keyboard = new List<HotkeyDescriptor>(bindings.Count + macroBindings.Count);
        var mouse = new List<MouseHookBinding>(bindings.Count + macroBindings.Count);
        var triggerMap = new Dictionary<int, OrchestratorTrigger>(bindings.Count);
        var macroMap = new Dictionary<int, string>(macroBindings.Count);
        var nextId = 1;

        foreach (var binding in bindings)
        {
            var id = nextId++;
            if (binding.IsMouse)
            {
                mouse.Add(new MouseHookBinding(id, binding.Modifiers, binding.MouseButton));
                triggerMap[id] = binding.Trigger;
            }
            else if (binding.IsKeyboard)
            {
                keyboard.Add(new HotkeyDescriptor(id, binding.Modifiers, binding.Key));
                triggerMap[id] = binding.Trigger;
            }
            else
            {
                LogMalformedBinding(binding.Trigger.ToString());
            }
        }

        foreach (var binding in macroBindings)
        {
            var id = nextId++;
            if (binding.IsMouse)
            {
                mouse.Add(new MouseHookBinding(id, binding.Modifiers, binding.MouseButton));
                macroMap[id] = binding.MacroName;
            }
            else if (binding.IsKeyboard)
            {
                keyboard.Add(new HotkeyDescriptor(id, binding.Modifiers, binding.Key));
                macroMap[id] = binding.MacroName;
            }
            else
            {
                LogMalformedBinding($"macro:{binding.MacroName}");
            }
        }

        _keyboardDescriptors = keyboard;
        _mouseDescriptors = mouse;
        _idToTrigger = triggerMap;
        _idToMacro = macroMap;
    }

    [LoggerMessage(LogLevel.Debug, "Hotkey trigger forwarded: {Trigger}")]
    partial void LogHotkeyTrigger(OrchestratorTrigger trigger);

    [LoggerMessage(LogLevel.Debug, "Macro hotkey forwarded: '{Macro}'")]
    partial void LogMacroHotkey(string macro);

    [LoggerMessage(LogLevel.Warning, "Received hotkey for unknown id {Id} — binding map out of sync?")]
    partial void LogUnknownHotkeyId(int id);

    [LoggerMessage(LogLevel.Information, "Re-registering {TriggerCount} trigger + {MacroCount} macro hotkey binding(s) after settings update")]
    partial void LogReregisterStart(int triggerCount, int macroCount);

    [LoggerMessage(LogLevel.Information, "Re-registration done — {KeyboardCount} keyboard + {MouseCount} mouse binding(s) now active")]
    partial void LogReregisterDone(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Error, "Failed to re-register hotkeys after settings update")]
    partial void LogReregisterFailed(Exception ex);

    [LoggerMessage(LogLevel.Information, "Hotkey listener suspended — all global hotkeys unregistered (Settings dialog)")]
    partial void LogSuspended();

    [LoggerMessage(LogLevel.Information, "Hotkey listener resumed — {KeyboardCount} keyboard + {MouseCount} mouse binding(s) re-registered")]
    partial void LogResumed(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Warning, "Skipping malformed hotkey binding for {Source} — neither Key nor MouseButton set")]
    partial void LogMalformedBinding(string source);
}
