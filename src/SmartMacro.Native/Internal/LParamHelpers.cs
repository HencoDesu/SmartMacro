namespace SmartMacro.Native.Internal;

// Утилиты упаковки Win32 lParam — используются реализациями ввода.
internal static class LParamHelpers
{
    public static IntPtr MakeCoordLParam(int x, int y)
    {
        var packed = ((uint)y << 16) | ((uint)x & 0xFFFF);
        return (IntPtr)packed;
    }

    // Раскладка lParam для WM_KEYDOWN/WM_KEYUP:
    //   биты  0..15 : счётчик повторов (шлём 1)
    //   биты 16..23 : скан-код (из MapVirtualKey)
    //   бит 24      : флаг расширенной клавиши (для нужных нам клавиш — 0)
    //   бит 30      : предыдущее состояние клавиши (1 при отпускании)
    //   бит 31      : состояние перехода (1 при отпускании)
    public static IntPtr BuildKeyLParam(VirtualKey key, bool isKeyUp)
    {
        var scanCode = User32Native.MapVirtualKey((uint)key, User32Native.MAPVK_VK_TO_VSC);
        var lParam = 1u | (scanCode << 16);
        if (isKeyUp)
        {
            lParam |= 0xC0000000;
        }

        return (IntPtr)lParam;
    }
}
