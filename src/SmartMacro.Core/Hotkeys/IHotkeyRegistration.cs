namespace SmartMacro.Hotkeys;

/// <summary>
/// Switching the daemon's global chords off and back on. Implemented by
/// <see cref="HotkeyListener"/>.
///
/// The reason this is a protocol-visible operation at all: Win32 <c>RegisterHotKey</c>
/// swallows presses of an already-registered chord — the owning window gets the WM_HOTKEY
/// and nobody else sees the key. So while the editor's hotkey picker is on screen in the
/// UI process, the very combinations the user most likely wants to re-bind would never
/// reach it. The UI brackets the picker with <c>SuspendHotkeys</c> / <c>ResumeHotkeys</c>.
///
/// Declared as an interface for the same test-seam reason as <c>IMacroRunner</c>: a real
/// <see cref="HotkeyListener"/> owns two Win32 monitors.
/// </summary>
public interface IHotkeyRegistration
{
    /// <summary>Unregisters every chord. Idempotent.</summary>
    Task SuspendAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-registers from the current macro library. Idempotent.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);
}
