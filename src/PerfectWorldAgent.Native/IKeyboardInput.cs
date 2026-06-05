namespace PerfectWorldAgent.Native;

// Sends a single key (down→hold→up) to a target window. Implementations differ in the
// underlying mechanism (PostMessage / SendMessage / SendInput) and trade focus requirements
// against compatibility with games that ignore window messages.
public interface IKeyboardInput
{
    Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default);

    // Sends a chord — modifier held while a main key is tapped. Sequence:
    //   1. modifier DOWN
    //   2. key DOWN
    //   3. key UP
    //   4. modifier UP
    // Used for Shift+N / Ctrl+N party-member selection and similar PW UI shortcuts.
    Task SendChordAsync(IntPtr hwnd, VirtualKey modifier, VirtualKey key, CancellationToken cancellationToken = default);
}
