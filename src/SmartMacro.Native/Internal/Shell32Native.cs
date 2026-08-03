using System.Runtime.InteropServices;

namespace SmartMacro.Native.Internal;

// Голая P/Invoke-поверхность shell32.dll — пока только API области уведомлений (трея).
internal static partial class Shell32Native
{
    // ─── Сообщения Shell_NotifyIcon ───
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    // ─── Какие поля NOTIFYICONDATA заполнены и должны учитываться ───
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    // NOTIFYICONDATAW блиттируема (буферы fixed char, см. Structs.cs), поэтому сгенерированный
    // stub передаёт её как есть. Возвращает false, если оболочка ещё не готова — вызывающему
    // это стоит трактовать как «повторить или сдаться», а не как фатальную ошибку.
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint dwMessage, in NOTIFYICONDATAW lpData);
}
