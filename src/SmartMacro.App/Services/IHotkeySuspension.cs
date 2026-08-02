using SmartMacro.Hotkeys;

namespace SmartMacro.App.Services;

/// <summary>
/// Lets the macro editor switch global hotkeys off for as long as it is open.
///
/// Win32 <c>RegisterHotKey</c> swallows presses of a chord that is already registered:
/// the owning window gets a WM_HOTKEY and nobody else sees the key at all. So while the
/// hotkey-trigger picker is on screen, the very combinations the user most likely wants to
/// re-bind (the ones already bound to a macro) would never reach it. Suspending the
/// listener for the lifetime of the dialog is the only fix.
///
/// This is an interface — rather than a direct <see cref="HotkeyListener"/> reference —
/// so the editor VM can be tested headlessly and so stage 2's daemon split can drop in an
/// IPC-backed implementation (<c>SuspendHotkeys</c> / <c>ResumeHotkeys</c>) without the VM
/// noticing.
/// </summary>
public interface IHotkeySuspension
{
    /// <summary>Unregisters every chord. Idempotent.</summary>
    Task SuspendAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-registers from the current macro library. Idempotent.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Adapter over the in-process <see cref="HotkeyListener"/>.</summary>
public sealed class HotkeyListenerSuspension : IHotkeySuspension
{
    private readonly HotkeyListener _listener;

    public HotkeyListenerSuspension(HotkeyListener listener) => _listener = listener;

    public Task SuspendAsync(CancellationToken cancellationToken = default) =>
        _listener.SuspendAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        _listener.ResumeAsync(cancellationToken);
}
