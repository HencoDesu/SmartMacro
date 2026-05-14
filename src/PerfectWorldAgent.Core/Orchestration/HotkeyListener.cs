using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PerfectWorldAgent.Native.Hotkey;

namespace PerfectWorldAgent.Orchestration;

// Thin adapter on top of Native.Hotkey monitors: splits HotkeyConfigStore bindings into
// keyboard and mouse subsets, assigns globally-unique numeric ids, hands each subset to
// the right monitor (Win32HotkeyMonitor for keyboard via RegisterHotKey, Win32MouseHookMonitor
// for mouse via WH_MOUSE_LL), and translates the monitors' id-based HotkeyPressed events
// back into trigger-based ones consumers care about.
//
// Runtime re-registration (Settings dialog save): the store raises BindingsChanged. We
// stop both monitors, rebuild descriptor sets + id→trigger map, start both monitors again.
// Suspend/Resume mirror this for the duration of the Settings dialog so already-bound
// inputs (F21, Mouse4, etc.) reach the dialog instead of firing their triggers.
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
    private bool _started;
    private bool _suspended;

    public event Action<OrchestratorTrigger>? HotkeyPressed;

    // Test-only hook — events can't be invoked from outside the declaring class even with
    // InternalsVisibleTo, so we expose an internal raise helper for unit tests.
    internal void RaiseHotkeyPressed(OrchestratorTrigger trigger) => HotkeyPressed?.Invoke(trigger);

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

        BuildDescriptors(_configStore.Bindings);
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

    // Temporarily unregister all global hotkeys (keyboard AND mouse). Use case: Settings
    // dialog is open and the user wants to rebind one of the already-bound inputs — without
    // this, RegisterHotKey / WH_MOUSE_LL intercept the press and the dialog never sees the
    // KeyDown / PointerPressed event. Pair with ResumeAsync.
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
                BuildDescriptors(_configStore.Bindings);
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
        // Snapshot the dictionary reference — a re-registration may swap _idToTrigger
        // out underneath us. The reference itself is a single aligned word read, so this
        // is atomic and gives us a consistent view for the lookup.
        var map = _idToTrigger;
        if (map.TryGetValue(id, out var trigger))
        {
            LogHotkeyTrigger(trigger);
            HotkeyPressed?.Invoke(trigger);
        }
        else
        {
            LogUnknownHotkeyId(id);
        }
    }

    private void OnBindingsChanged(IReadOnlyList<HotkeyBinding> bindings)
    {
        _ = RestartAsync(bindings, CancellationToken.None);
    }

    private async Task RestartAsync(IReadOnlyList<HotkeyBinding> bindings, CancellationToken cancellationToken)
    {
        await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_suspended)
            {
                return;
            }

            if (!_started)
            {
                BuildDescriptors(bindings);
                return;
            }

            LogReregisterStart(bindings.Count);
            await _keyboardMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            await _mouseMonitor.StopAsync(cancellationToken).ConfigureAwait(false);
            BuildDescriptors(bindings);
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

    private void BuildDescriptors(IReadOnlyList<HotkeyBinding> bindings)
    {
        var keyboard = new List<HotkeyDescriptor>(bindings.Count);
        var mouse = new List<MouseHookBinding>(bindings.Count);
        var map = new Dictionary<int, OrchestratorTrigger>(bindings.Count);
        var nextId = 1;
        foreach (var binding in bindings)
        {
            var id = nextId++;
            if (binding.IsMouse)
            {
                mouse.Add(new MouseHookBinding(id, binding.Modifiers, binding.MouseButton));
            }
            else if (binding.IsKeyboard)
            {
                keyboard.Add(new HotkeyDescriptor(id, binding.Modifiers, binding.Key));
            }
            else
            {
                // Malformed (neither key nor mouse set) — skip but don't crash; the
                // settings dialog already rejects this on Save, so reaching here implies
                // a hand-edited hotkeys.json.
                LogMalformedBinding(binding.Trigger);
                continue;
            }
            map[id] = binding.Trigger;
        }
        _keyboardDescriptors = keyboard;
        _mouseDescriptors = mouse;
        _idToTrigger = map;
    }

    [LoggerMessage(LogLevel.Debug, "Hotkey trigger forwarded: {Trigger}")]
    partial void LogHotkeyTrigger(OrchestratorTrigger trigger);

    [LoggerMessage(LogLevel.Warning, "Received hotkey for unknown id {Id} — binding map out of sync?")]
    partial void LogUnknownHotkeyId(int id);

    [LoggerMessage(LogLevel.Information, "Re-registering {Count} hotkey binding(s) after settings update")]
    partial void LogReregisterStart(int count);

    [LoggerMessage(LogLevel.Information, "Re-registration done — {KeyboardCount} keyboard + {MouseCount} mouse binding(s) now active")]
    partial void LogReregisterDone(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Error, "Failed to re-register hotkeys after settings update")]
    partial void LogReregisterFailed(Exception ex);

    [LoggerMessage(LogLevel.Information, "Hotkey listener suspended — all global hotkeys unregistered (Settings dialog)")]
    partial void LogSuspended();

    [LoggerMessage(LogLevel.Information, "Hotkey listener resumed — {KeyboardCount} keyboard + {MouseCount} mouse binding(s) re-registered")]
    partial void LogResumed(int keyboardCount, int mouseCount);

    [LoggerMessage(LogLevel.Warning, "Skipping malformed hotkey binding for {Trigger} — neither Key nor MouseButton set")]
    partial void LogMalformedBinding(OrchestratorTrigger trigger);
}
