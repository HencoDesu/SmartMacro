using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SmartMacro.Native.Diagnostics;

/// <summary>
/// Мелкие вопросы к среде, на которые может ответить только Win32: повышены ли мы, какое
/// масштабирование у экрана и принимает ли конкретное окно наши сообщения.
///
/// Здесь, а не в Core, по общему правилу проекта: P/Invoke живёт в Native. Логика «что из ответов
/// считать поломкой и как об этом сказать» — наоборот, в Core, у <c>EnvironmentDiagnostics</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class EnvironmentProbe
{
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int ErrorAccessDenied = 5;

    /// <summary>DPI «без масштабирования». 96 = 100%.</summary>
    private const double BaselineDpi = 96.0;

    /// <summary>Работает ли процесс с правами администратора.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // Токен не читается — считаем, что не повышены: ложная тревога здесь дешевле ложного
            // спокойствия, потому что вторая ошибка выглядит как «макросы просто не работают».
            return false;
        }
    }

    /// <summary>Масштабирование системного экрана в процентах: 100, 125, 150…</summary>
    public static int DisplayScalePercent()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi == 0 ? 100 : (int)Math.Round(dpi * 100 / BaselineDpi);
        }
        catch (EntryPointNotFoundException)
        {
            // GetDpiForSystem появилась в Windows 10 1607. Старее — считаем, что 100%: сказать
            // «не знаю» диагностике нечем, а выдумывать предупреждение не за что.
            return 100;
        }
    }

    /// <summary>
    /// Отвергает ли окно наши сообщения из-за UIPI — то есть работает ли его процесс на более
    /// высоком уровне целостности, чем мы.
    ///
    /// <b>Проверка настоящая, а не вывод из «мы не повышены».</b> Шлём безобидное
    /// <c>WM_NULL</c>: UIPI режет межуровневые сообщения, и вызов возвращает ноль с
    /// <c>ERROR_ACCESS_DENIED</c>. Именно это и происходит с настоящим вводом, поэтому ответ
    /// здесь — то же самое, что случится с макросом, а не догадка о нём.
    ///
    /// <c>SMTO_ABORTIFHUNG</c> плюс короткий таймаут обязательны: клиенты PW в фоне заморожены и
    /// сообщения не разбирают, так что без него каждое окно стоило бы полного таймаута. У
    /// зависшего окна вызов возвращает ноль с <c>ERROR_TIMEOUT</c> — это НЕ отказ доступа, и
    /// такое окно считается нормальным.
    /// </summary>
    /// <param name="hwnd">Проверяемое окно.</param>
    /// <param name="timeoutMs">Сколько ждать ответа.</param>
    /// <returns><c>true</c>, если сообщения в это окно блокируются.</returns>
    public static bool IsBlockedByUipi(IntPtr hwnd, int timeoutMs = 50)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        Marshal.SetLastSystemError(0);
        var ok = SendMessageTimeout(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, (uint)timeoutMs,
            out _);
        return ok == IntPtr.Zero && Marshal.GetLastWin32Error() == ErrorAccessDenied;
    }

    /// <summary>
    /// Можно ли писать в эту папку. Рядом с exe лежат макросы, настройки и журнал, так что «нет»
    /// означает, что не сохранится ничего.
    /// </summary>
    /// <param name="folder">Проверяемая папка.</param>
    public static bool IsFolderWritable(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        // Проверяем записью, а не разбором ACL: на права влияют наследование, отказы, фильтрация
        // токена UAC и перенаправление в VirtualStore, и единственный способ узнать наверняка —
        // попробовать. Имя с Guid, чтобы не наступить на чужой файл.
        var probe = Path.Combine(folder, $".smartmacro-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or NotSupportedException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
