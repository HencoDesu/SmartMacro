using System.Runtime.InteropServices;

namespace SmartMacro.App.Interop;

/// <summary>
/// Блокирующее нативное окно с ошибкой.
///
/// Встроенного диалога сообщений у Avalonia нет, а оба места, где панели он нужен, — это места,
/// куда окно Avalonia плохо ложится: отказ «демон недоступен» случается ДО
/// <c>AppBuilder.StartWithClassicDesktopLifetime</c> (потока UI ещё нет, стилей нет, нет и окна,
/// которое владело бы диалогом), а извещение «демон умер» обязано быть последним, что показано
/// перед выходом процесса. <c>MessageBoxW</c> крутит собственный модальный цикл и не нуждается
/// ни в том, ни в другом.
/// </summary>
internal static partial class Win32MessageBox
{
    private const uint MbOk = 0x0000_0000;
    private const uint MbIconError = 0x0000_0010;
    private const uint MbSystemModal = 0x0000_1000;
    private const uint MbSetForeground = 0x0001_0000;
    private const uint MbTopMost = 0x0004_0000;

    /// <summary>
    /// Показывает окно с ошибкой и возвращает управление, когда пользователь его закрыл. Поверх
    /// всех и на переднем плане: клиенты игры развёрнуты на весь экран, и диалог за ними выглядел
    /// бы как зависание.
    /// </summary>
    public static void Error(string caption, string text) =>
        MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbIconError | MbSystemModal | MbSetForeground | MbTopMost);

    // Возврат отбрасываем сознательно: MessageBoxW отдаёт код нажатой кнопки, а с MB_OK кнопка
    // ровно одна, так что читать нечего. Ноль тоже не значит ошибку — его отдают и при нехватке
    // памяти, и когда у процесса нет рабочего стола; звать GetLastError здесь не за чем, ведь
    // альтернативного способа сообщить о беде у нас в этот момент всё равно нет.
    // ReSharper disable once UnusedMethodReturnValue.Local
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
