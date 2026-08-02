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

    /// <summary>
    /// Sends a chord — modifier held while a main key is tapped. Sequence:
    /// modifier DOWN → key DOWN → key UP → modifier UP. Used for Shift+N / Ctrl+N
    /// party-member selection and similar PW UI shortcuts.
    /// </summary>
    Task SendChordAsync(IntPtr hwnd, VirtualKey modifier, VirtualKey key, CancellationToken cancellationToken = default);
}
