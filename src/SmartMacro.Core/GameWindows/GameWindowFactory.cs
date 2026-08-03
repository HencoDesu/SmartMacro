using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.Native.Keyboard;
using SmartMacro.Native.Mouse;
using SmartMacro.ProcessMonitoring;

namespace SmartMacro.GameWindows;

// Фабрика по умолчанию — вставляет стратегии ввода в GameWindow и подбирает процессу профиль
// активации. За саму активацию (побудка через WM_ACTIVATEAPP замороженных фоновых клиентов PW)
// отвечает вызывающий, через IGameWindow.ActivateAsync/DeactivateAsync; параметры для неё
// берутся из ProcessProfile, подходящего по имени процесса окна (а если профиль не настроен —
// из инертных значений по умолчанию: простой ввод, без пляски с побудкой).
//
// Текущая смесь способов ввода (это направленный эксперимент):
//   * Клавиатура → SendMessage. Похоже, WndProc у PW пропускает ввод с клавиатуры только при
//     определённом внутреннем состоянии «активен»; синхронная доставка гарантирует, что PW
//     дообработал нажатие до того, как мы пойдём дальше. Что наблюдалось с вариантом на
//     PostMessage: 1–2 агента из 11 время от времени пропускали широковещательный имун даже с
//     паузой на устаканивание в 30–50 мс.
//   * Мышь → PostMessage. Клики доходят до PW надёжно в любом случае (видимо, в обработчике
//     мыши у PW проверки фокуса нет), так что блокирующие накладные расходы SendMessage тут
//     ничем не оправданы.
//
// Если это окажется стабильным, ради единообразия можно схлопнуть всё в Send. Если надёжность
// клавиатуры так и останется проблемой — значит, дело внутри PW (проверка состояния, на которую
// снаружи не повлиять), и следующий шаг — рассылка кликами по иконкам вместо клавиш.
[SupportedOSPlatform("windows")]
public sealed class GameWindowFactory : IGameWindowFactory
{
    private readonly IOptions<ProcessProfileOptions> _profileOptions;
    private readonly IOptions<WindowVisionOptions> _visionOptions;
    private readonly ILoggerFactory _loggerFactory;

    public GameWindowFactory(
        IOptions<ProcessProfileOptions> profileOptions,
        IOptions<WindowVisionOptions> visionOptions,
        ILoggerFactory loggerFactory)
    {
        _profileOptions = profileOptions;
        _visionOptions = visionOptions;
        _loggerFactory = loggerFactory;
    }

    public IGameWindow Create(ProcessInfo info)
    {
        var profile = _profileOptions.Value.FindByProcessName(info.ProcessName) ?? ProcessProfile.Inert;
        return new GameWindow(
            info,
            profile,
            new SendMessageKeyboardInput(),
            new PostMessageMouseInput(),
            _visionOptions,
            _loggerFactory.CreateLogger<GameWindow>());
    }
}
