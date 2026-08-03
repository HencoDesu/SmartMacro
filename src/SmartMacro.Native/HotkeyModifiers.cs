namespace SmartMacro.Native;

// Значения совпадают с fsModifiers из Win32 RegisterHotKey — перечисление держим 1:1 с API.
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}
