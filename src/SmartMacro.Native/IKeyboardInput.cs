namespace SmartMacro.Native;

// Sends a single key (down→hold→up) to a target window. Implementations differ in the
// underlying mechanism (PostMessage / SendMessage / SendInput) and trade focus requirements
// against compatibility with games that ignore window messages.
public interface IKeyboardInput
{
    /// <summary>
    /// Sends a single key as a <c>WM_KEYDOWN</c> → hold → <c>WM_KEYUP</c> pair to the
    /// given window handle.
    /// </summary>
    Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default);
}
