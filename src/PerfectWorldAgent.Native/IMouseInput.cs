namespace PerfectWorldAgent.Native;

// Sends mouse interactions at client-area coordinates of a target window. Same trade-off
// space as IKeyboardInput: different implementations for different focus/compatibility needs.
public interface IMouseInput
{
    /// <summary>
    /// Posts a left-button click at the given client-area coordinates.
    /// </summary>
    Task ClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a double left-button click at the given client-area coordinates.
    /// </summary>
    Task DoubleClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default);
}
