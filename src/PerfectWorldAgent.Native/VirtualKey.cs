namespace PerfectWorldAgent.Native;

// Windows virtual-key codes. Values match the VK_* constants from <WinUser.h>.
public enum VirtualKey : ushort
{
    Tab = 0x09,
    Enter = 0x0D,
    // Generic modifier codes (left/right not distinguished — fine for chord sends since
    // PostMessage WM_KEYDOWN with VK_SHIFT/VK_CONTROL/VK_MENU is treated by most apps
    // as "a shift/ctrl/alt is held").
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    Escape = 0x1B,
    Space = 0x20,

    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13 = 0x7C, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,

    // OEM keys — punctuation, brackets, and the `~ / Ё key. Names match Avalonia's
    // Key enum names so KeyBindingPicker.OnKeyDown's `Enum.IsDefined` check accepts
    // them. Values match Windows VK_OEM_* constants so PostMessage/SendMessage deliver
    // the actual physical key press.
    OemSemicolon = 0xBA,     // ; :
    OemPlus = 0xBB,          // = +
    OemComma = 0xBC,         // , <
    OemMinus = 0xBD,         // - _
    OemPeriod = 0xBE,        // . >
    OemQuestion = 0xBF,      // / ?
    OemTilde = 0xC0,         // ` ~   — same physical key = Ё on Russian layout
    OemOpenBrackets = 0xDB,  // [ {
    OemPipe = 0xDC,          // \ |
    OemCloseBrackets = 0xDD, // ] }
    OemQuotes = 0xDE,        // ' "
}
