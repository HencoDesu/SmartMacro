namespace PerfectWorldAgent.Native;

// Windows virtual-key codes. Values match the VK_* constants from <WinUser.h>.
public enum VirtualKey : ushort
{
    Tab = 0x09,
    Enter = 0x0D,
    Escape = 0x1B,
    Space = 0x20,

    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13 = 0x7C, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
}
