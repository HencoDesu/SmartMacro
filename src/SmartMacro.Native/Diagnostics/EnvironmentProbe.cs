using System.Runtime.Versioning;
using System.Security.Principal;

namespace SmartMacro.Native.Diagnostics;

/// <summary>
/// Мелкие вопросы к среде, на которые может ответить только Win32.
///
/// Здесь, а не в Core, по общему правилу проекта: P/Invoke живёт в Native.
///
/// ⚠️ От класса остался ОДИН вопрос. Ещё три — «режет ли UIPI сообщения в это окно»
/// (<c>SendMessageTimeout</c> с <c>WM_NULL</c>), «какое масштабирование у экрана»
/// (<c>GetDpiForSystem</c>) и «пишется ли в эту папку» (проба файлом) — существовали
/// исключительно ради проверки среды и удалены вместе с ней. Не потому, что были плохи: каждый
/// отвечал на свой вопрос настоящим действием, а не выводом из косвенных признаков, — а потому,
/// что спрашивать их стало некому. Вернутся они вместе со своим потребителем или не вернутся
/// вовсе; лежать без вызывающих они не будут.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EnvironmentProbe
{
    /// <summary>
    /// Работает ли процесс с правами администратора.
    ///
    /// Единственный вызывающий — <c>AutoStartManager.NeedsElevationRelaunch</c>: повышение нельзя
    /// получить на лету, поэтому «настройка требует прав, а мы их не имеем» решается перезапуском,
    /// и вопрос этот про СТАРТ, а не про диагностику.
    /// </summary>
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
}
