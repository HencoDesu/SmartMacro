using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.Versioning;
using SmartMacro.Native.Internal;

namespace SmartMacro.Native.Window;

// Статический на весь процесс кэш: путь к файлу изображения → HICON. Каждая уникальная
// иконка класса декодируется один раз и переиспользуется всеми игровыми окнами, которым она
// нужна. DestroyIcon мы не вызываем никогда, потому что HICON'ы должны пережить рассылки
// WM_SETICON, которые их потребляют: освободи мы их по завершении вызова — целевые окна
// остались бы с недействительными хендлами. Утечка на время жизни процесса в ~18 HICON'ов
// (по одному на класс) безвредна.
//
// Пути загрузки:
//   * .ico — Win32 LoadImage справляется нативно (с выбором нужного разрешения из нескольких).
//   * .png / .jpg / .bmp и прочие — декодирует System.Drawing.Bitmap, затем GetHicon()
//     собирает HICON из пикселей. Владеет им вызывающая сторона (потребовался бы
//     DestroyIcon), но мы кэшируем на всё время жизни процесса, так что это не проблема.
//
// Многопоточность: ConcurrentDictionary разруливает гонку, когда два агента из разных
// потоков пытаются загрузить один и тот же путь. В худшем случае оба декодируют параллельно —
// один HICON оставляем, второй уничтожаем (единственное место, где мы вообще уничтожаем
// HICON).
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
            // Гонка — кто-то успел загрузить тот же путь между нашим TryGetValue и этим
            // моментом. Освобождаем свой лишний HICON и берём тот, что уже в кэше.
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
            // LoadImage сам выбирает из многоразрешенческого .ico кадр, лучше всего
            // подходящий под системный размер по умолчанию. Самый чистый путь, когда файл
            // и так .ico.
            return User32Native.LoadImage(
                IntPtr.Zero,
                path,
                User32Native.IMAGE_ICON,
                cx: 0,
                cy: 0,
                User32Native.LR_LOADFROMFILE | User32Native.LR_DEFAULTSIZE);
        }

        // PNG / JPG / BMP — декодируем через GDI+ и синтезируем HICON. GetHicon копирует
        // растр в ресурс иконки, которым владеем мы; сам Bitmap после этого можно спокойно
        // освободить. Под реальные поверхности (панель задач, заголовок окна) Windows
        // пересэмплирует HICON сама, поэтому неквадратные или слишком большие PNG всё равно
        // отображаются вменяемо.
        try
        {
            using var bitmap = new Bitmap(path);
            return bitmap.GetHicon();
        }
        catch
        {
            // GDI+ бросает исключение на отсутствующих и повреждённых файлах и на
            // неподдерживаемых форматах. Отдаём наружу IntPtr.Zero, чтобы вызывающий смог
            // записать это в лог и пропустить, — падать нельзя ни в коем случае.
            return IntPtr.Zero;
        }
    }
}
