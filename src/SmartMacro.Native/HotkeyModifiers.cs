namespace SmartMacro.Native;

// Values match Win32 fsModifiers for RegisterHotKey — keep this enum 1:1 with the API.
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}
