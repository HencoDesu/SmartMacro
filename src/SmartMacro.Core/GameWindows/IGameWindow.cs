using SmartMacro.Native;

namespace SmartMacro.GameWindows;

// Per-window facade combining input + capture. PW freezes background clients — both input
// and rendering pause — so any operation against a non-foreground window requires explicit
// activation first. The lifecycle is now caller-controlled via ActivateAsync/DeactivateAsync:
//
//   await window.ActivateAsync();
//   try
//   {
//       await window.PressKeyAsync(...);     // or any number of input calls
//       await window.ClickAsync(...);
//   }
//   finally
//   {
//       await window.DeactivateAsync();
//   }
//
// Sequences run inside ONE activation, which avoids subtle races where PW drops queued
// input on deactivation between calls. CaptureScreenshot is the one self-contained
// exception — it manages its own activation because it's used by sync UI paths.
//
// There is deliberately no chord (Shift+N) primitive: PW reads modifier state through
// GetKeyState, which a cross-thread SendMessage never updates, so an injected chord
// arrives as the bare key. The macro-level answer is a ClickNode on the UI the chord
// would have reached (see the pw-assist example, which clicks party slot 1).
public interface IGameWindow
{
    IntPtr Handle { get; }

    /// <summary>
    /// Cheap aliveness check — <c>false</c> once the underlying window has been destroyed
    /// (game crashed / user closed). Used by CharacterAgent's poll loop to self-terminate
    /// when the client is gone.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// Client-area size in pixels. <c>(0, 0)</c> typically means the window is minimised to
    /// tray, or the process is a launcher that doesn't render a real game client.
    /// CharacterAgentFactory uses this at agent creation to filter out un-capturable windows.
    /// </summary>
    (int Width, int Height) ClientSize { get; }

    /// <summary>
    /// Sends <c>WM_ACTIVATEAPP(TRUE)</c> to wake the (potentially frozen) PW client, then
    /// waits the settle delay so the engine is ready to receive input. Caller pairs this
    /// with <see cref="DeactivateAsync"/> in a try/finally for every input session.
    /// </summary>
    Task ActivateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits the deactivation drain delay (lets PW's message pump finish processing any
    /// posted input before going inactive — without this, queued WM_KEYDOWN/CLICK can be
    /// dropped), then sends <c>WM_ACTIVATEAPP(FALSE)</c> — unless this window is currently
    /// the foreground (the user is actively interacting with it; don't yank focus).
    /// </summary>
    Task DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a single key down/up pair. Does NOT manage activation — caller is responsible
    /// for wrapping in <see cref="ActivateAsync"/>/<see cref="DeactivateAsync"/>.
    /// </summary>
    Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a left-button click at the given client-area point. Does NOT manage activation.
    /// </summary>
    Task ClickAsync(ScreenPoint point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a double left-button click at the given client-area point. Does NOT manage
    /// activation.
    /// </summary>
    Task DoubleClickAsync(ScreenPoint point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active capture: wakes the (potentially frozen) PW client via WM_ACTIVATEAPP so
    /// PrintWindow returns a fresh frame, then deactivates if not foreground. Self-
    /// contained activation lifecycle — meant for user-initiated one-shot captures
    /// (Label dialog) where freshness is critical and the caller isn't running an input
    /// session.
    /// </summary>
    byte[] CaptureScreenshot();

    /// <summary>
    /// Replaces the game window's title-bar / taskbar icon with the contents of an image
    /// file (.ico via LoadImage, .png/.jpg via GDI+). Used to surface the character's
    /// class icon in the taskbar so the user can distinguish 9 PW windows at a glance.
    /// </summary>
    /// <returns><c>false</c> if the file couldn't be loaded (missing, corrupt, unsupported format).</returns>
    bool SetIconFromFile(string imagePath);

    /// <summary>
    /// Single capture + single template match. The one-shot sibling of
    /// <see cref="WaitForElementAsync"/>, backing <c>FindElementNode</c>.
    /// </summary>
    /// <param name="elementTemplate">PNG-encoded image of the UI element to find.</param>
    /// <param name="position">Client-space crop region. Empty (Width=0 or Height=0) means search the whole client area.</param>
    /// <returns>Client-space CENTER of the match, or <c>null</c> when nothing scored above threshold (or the capture failed).</returns>
    Task<ScreenPoint?> FindElementAsync(byte[] elementTemplate, ScreenRect position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls the window until <paramref name="elementTemplate"/> matches inside
    /// <paramref name="position"/>, or until <paramref name="waitDuration"/> elapses.
    /// Internal poll cadence and match threshold come from window options (configurable
    /// via "Vision:Window" in appsettings.json).
    /// </summary>
    /// <param name="elementTemplate">PNG-encoded image of the UI element to find.</param>
    /// <param name="position">Client-space crop region. Empty (Width=0 or Height=0) means search the whole client area.</param>
    /// <param name="waitDuration">Overall budget — give up once exhausted.</param>
    /// <returns>
    /// Client-space CENTER of the match the first time it scores above threshold; <c>null</c>
    /// on timeout. The center is what lets a macro "find the button anywhere, then click it"
    /// via <c>FoundPointVar</c>.
    /// </returns>
    Task<ScreenPoint?> WaitForElementAsync(byte[] elementTemplate, ScreenRect position, TimeSpan waitDuration, CancellationToken cancellationToken = default);
}
