namespace PerfectWorldAgent.Native;

// Sends mouse interactions at client-area coordinates of a target window. Same trade-off
// space as IKeyboardInput: different implementations for different focus/compatibility needs.
public interface IMouseInput
{
    Task ClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default);
    Task DoubleClickAsync(IntPtr hwnd, int x, int y, CancellationToken cancellationToken = default);
}
