using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.Core;

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

    // Cheap aliveness check — false once the underlying window has been destroyed
    // (game crashed / user closed). Used by CharacterAgent's poll loop to self-terminate
    // when the client is gone.
    bool IsAlive { get; }

    // Client-area size. Zero/zero typically means the window is minimised to tray, or
    // the process is a launcher that doesn't render a real game client. CharacterAgentFactory
    // uses this at agent creation to filter out un-capturable windows.
    (int Width, int Height) ClientSize { get; }

    // Send WM_ACTIVATEAPP(TRUE) to wake the (potentially frozen) PW client, then wait
    // the settle delay so the engine is ready to receive input. Caller pairs this with
    // DeactivateAsync in a try/finally for every input session.
    Task ActivateAsync(CancellationToken cancellationToken = default);

    // Wait the deactivation drain delay (lets PW's message pump finish processing any
    // posted input before going inactive — without this, queued WM_KEYDOWN/CLICK can
    // be dropped), then send WM_ACTIVATEAPP(FALSE) — unless this window is currently
    // the foreground (the user is actively interacting with it; don't yank focus from
    // their session).
    Task DeactivateAsync(CancellationToken cancellationToken = default);

    // Input methods do NOT manage activation — caller is responsible. Each method just
    // posts the appropriate Win32 messages and returns when the held duration is up.
    Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default);
    Task PressChordAsync(VirtualKey modifier, VirtualKey key, CancellationToken cancellationToken = default);
    Task ClickAsync(int x, int y, CancellationToken cancellationToken = default);
    Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default);

    // Active capture: wakes the (potentially frozen) PW client via WM_ACTIVATEAPP so
    // PrintWindow returns a fresh frame, then deactivates if not foreground. Self-
    // contained activation lifecycle — meant for user-initiated one-shot captures
    // (Label dialog) where freshness is critical and the caller isn't running an
    // input session.
    byte[] CaptureScreenshot();

    // Passive capture: no activation, just PrintWindow against whatever frame DWM has.
    // Use for periodic polling (identification, future boss/quest checks) — running
    // active capture every 2s on 9 windows was disrupting the user's manual window
    // focus in dungeons (WM_ACTIVATEAPP traffic interfering with foreground input).
    // Trade-off: a frozen background window may return a stale or partial frame; for
    // identification that just means "try again next tick", not a correctness issue.
    byte[] CaptureScreenshotPassive();

    // Replaces the game window's title-bar/taskbar icon with the contents of an image
    // file (.ico via LoadImage, .png/.jpg via GDI+). Returns false if the file couldn't
    // be loaded. Used to surface the character's class icon in the taskbar so the user
    // can distinguish 9 PW windows at a glance.
    bool SetIconFromFile(string imagePath);
}
