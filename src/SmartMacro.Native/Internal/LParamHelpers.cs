namespace SmartMacro.Native.Internal;

// Win32 lParam packing utilities — used by the input implementations.
internal static class LParamHelpers
{
    public static IntPtr MakeCoordLParam(int x, int y)
    {
        var packed = ((uint)y << 16) | ((uint)x & 0xFFFF);
        return (IntPtr)packed;
    }

    // lParam layout for WM_KEYDOWN/WM_KEYUP:
    //   bits  0..15 : repeat count (we send 1)
    //   bits 16..23 : scan code (from MapVirtualKey)
    //   bit 24      : extended key flag (0 for the keys we care about)
    //   bit 30      : previous key state (1 on keyup)
    //   bit 31      : transition state (1 on keyup)
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
