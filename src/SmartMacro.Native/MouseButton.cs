namespace SmartMacro.Native;

// Mouse buttons we allow as global-hotkey inputs. Left/Right are deliberately excluded —
// binding a normal click as a global hotkey makes the system effectively unusable (every
// click in every app would trigger the bound action). Middle (wheel-click) and XButton1/2
// (thumb buttons) are rare enough in normal Windows operation that binding them is safe
// and a natural fit for game-action remapping.
//
// XButton1/XButton2 values match the XBUTTON1/XBUTTON2 constants from <WinUser.h> (high
// word of WM_XBUTTON's MSLLHOOKSTRUCT.mouseData). Middle has no equivalent constant in
// that protocol (WM_MBUTTONDOWN identifies it via the message itself, not mouseData), so
// we pick an arbitrary unique value that doesn't collide with the XButton ones.
//
// Enum is serialised as the name string in macro-graph JSON (JsonStringEnumConverter), so
// these numeric values aren't part of the on-disk contract — feel free to renumber.
public enum MouseButton : ushort
{
    None = 0,
    XButton1 = 0x0001,
    XButton2 = 0x0002,
    Middle = 0x0010,
}
