namespace PerfectWorldAgent.Native;

// Sends a single key (down→hold→up) to a target window. Implementations differ in the
// underlying mechanism (PostMessage / SendMessage / SendInput) and trade focus requirements
// against compatibility with games that ignore window messages.
public interface IKeyboardInput
{
    Task SendKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken cancellationToken = default);
}
