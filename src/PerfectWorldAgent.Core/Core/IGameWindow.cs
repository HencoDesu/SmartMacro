using PerfectWorldAgent.Native;

namespace PerfectWorldAgent.Core;

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

    Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default);

    Task ClickAsync(int x, int y, CancellationToken cancellationToken = default);
    Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default);

    byte[] CaptureScreenshot();
}
