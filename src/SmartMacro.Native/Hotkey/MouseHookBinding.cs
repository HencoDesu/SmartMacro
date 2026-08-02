namespace SmartMacro.Native.Hotkey;

// Mirror of HotkeyDescriptor for mouse bindings: Id is the value Win32MouseHookMonitor
// hands back on a match so the consumer can resolve it to its own meaning. Modifiers
// must all be held at the moment the mouse button goes down for the binding to fire.
public sealed record MouseHookBinding(int Id, HotkeyModifiers Modifiers, MouseButton Button);
