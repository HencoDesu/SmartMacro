using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Window;

// Process-static cache: image file path → HICON. Each unique class icon is decoded once
// and reused across every game window that wants it. We never DestroyIcon because the
// HICONs need to outlive the WM_SETICON broadcasts that consume them — if we freed them
// at end-of-call, the target windows would be left pointing at invalid handles.
// Process-lifetime leak of ~18 HICONs (one per class) is harmless.
//
// Loading paths:
//   * .ico — Win32 LoadImage handles natively (with multi-resolution selection).
//   * .png / .jpg / .bmp / others — System.Drawing.Bitmap decodes, then GetHicon() builds
//     an HICON from the pixels. Caller owns it (would need DestroyIcon) but we cache for
//     process lifetime so it's fine.
//
// Concurrency: ConcurrentDictionary handles the race where two agents on different
// threads try to load the same path. Worst case both decode in parallel — we keep one
// HICON and destroy the other (only place we ever destroy an HICON).
[SupportedOSPlatform("windows")]
internal static class WindowIconCache
{
    private static readonly ConcurrentDictionary<string, IntPtr> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IntPtr GetOrLoad(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var hicon = LoadIconFromFile(path);
        if (hicon == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (!Cache.TryAdd(path, hicon))
        {
            // Race — someone else loaded the same path between our TryGetValue and now.
            // Free our redundant HICON and use the cached one.
            User32Native.DestroyIcon(hicon);
            return Cache[path];
        }

        return hicon;
    }

    private static IntPtr LoadIconFromFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase))
        {
            // LoadImage picks the best frame from a multi-resolution .ico for the
            // system-default size. Cleanest path when the file is already .ico.
            return User32Native.LoadImage(
                IntPtr.Zero,
                path,
                User32Native.IMAGE_ICON,
                cx: 0,
                cy: 0,
                User32Native.LR_LOADFROMFILE | User32Native.LR_DEFAULTSIZE);
        }

        // PNG / JPG / BMP — decode via GDI+ then synthesize an HICON. GetHicon copies
        // the bitmap into an icon resource we own; the Bitmap can be safely disposed
        // afterwards. Windows resamples the HICON for the actual taskbar/title-bar
        // surfaces, so non-square or oversized PNGs still display sensibly.
        try
        {
            using var bitmap = new Bitmap(path);
            return bitmap.GetHicon();
        }
        catch
        {
            // GDI+ throws on missing/corrupt files or unsupported formats. Surface as
            // IntPtr.Zero so the caller can log and skip — never crash.
            return IntPtr.Zero;
        }
    }
}
