using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.GameWindows;

// Per-window facade combining input + capture. PW freezes background clients — both input
// and rendering pause — so any operation against a non-foreground window requires explicit
// activation first. The lifecycle is now caller-controlled via ActivateAsync/DeactivateAsync:
//
//   await window.ActivateAsync();
//   try
//   {
//       await window.PressKeyAsync(...);     // or any number of input calls
//       await window.PressChordAsync(...);
//       await window.ClickAsync(...);
//   }
//   finally
//   {
//       await window.DeactivateAsync();
//   }
//
// Sequences (chord + follow-up, combat rotations, etc.) all run inside ONE activation,
// which avoids subtle races where PW drops queued input on deactivation between calls.
// CaptureScreenshot is the one self-contained exception — it manages its own activation
// because it's used by sync UI paths.
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
    /// Posts a chord: <paramref name="modifier"/> down → <paramref name="key"/> down →
    /// hold → key up → modifier up. Used for PW UI shortcuts like Shift+1
    /// (select party member 1). Does NOT manage activation.
    /// </summary>
    Task PressChordAsync(VirtualKey modifier, VirtualKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a left-button click at the given client-area coordinates. Does NOT manage
    /// activation.
    /// </summary>
    Task ClickAsync(int x, int y, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a double left-button click at the given client-area coordinates. Does NOT
    /// manage activation.
    /// </summary>
    Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active capture: wakes the (potentially frozen) PW client via WM_ACTIVATEAPP so
    /// PrintWindow returns a fresh frame, then deactivates if not foreground. Self-
    /// contained activation lifecycle — meant for user-initiated one-shot captures
    /// (Label dialog) where freshness is critical and the caller isn't running an input
    /// session.
    /// </summary>
    byte[] CaptureScreenshot();

    /// <summary>
    /// Passive capture: no activation, just PrintWindow against whatever frame DWM has.
    /// Use for periodic polling (identification, future boss/quest checks) — running
    /// active capture every 2s on 9 windows was disrupting the user's manual window focus
    /// in dungeons (WM_ACTIVATEAPP traffic interfering with foreground input).
    /// Trade-off: a frozen background window may return a stale or partial frame; for
    /// identification that just means "try again next tick", not a correctness issue.
    /// </summary>
    byte[] CaptureScreenshotPassive();

    /// <summary>
    /// Replaces the game window's title-bar / taskbar icon with the contents of an image
    /// file (.ico via LoadImage, .png/.jpg via GDI+). Used to surface the character's
    /// class icon in the taskbar so the user can distinguish 9 PW windows at a glance.
    /// </summary>
    /// <returns><c>false</c> if the file couldn't be loaded (missing, corrupt, unsupported format).</returns>
    bool SetIconFromFile(string imagePath);
}
