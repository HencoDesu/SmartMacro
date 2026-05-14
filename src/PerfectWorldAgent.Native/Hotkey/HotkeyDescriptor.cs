namespace PerfectWorldAgent.Native.Hotkey;

// A single hotkey binding the monitor will register. Id is the value the consumer cares
// about — the monitor passes it back on HotkeyPressed so the consumer can resolve it to
// whatever meaning they attach to the id (e.g., an orchestrator trigger).
public sealed record HotkeyDescriptor(int Id, HotkeyModifiers Modifiers, VirtualKey Key);
