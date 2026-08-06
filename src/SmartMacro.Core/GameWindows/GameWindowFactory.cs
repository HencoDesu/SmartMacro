using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Settings;
using SmartMacro.Input;
using SmartMacro.Native.Mouse;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Settings;

namespace SmartMacro.GameWindows;

// Фабрика по умолчанию — вставляет стратегии ввода в GameWindow и подбирает процессу его ХУК.
// За саму побудку (WM_ACTIVATEAPP замороженным фоновым клиентам PW) отвечает вызывающий, открывая
// область IGameWindow.EnterHookAsync; параметры для неё берутся из хука, подходящего по имени
// процесса окна. Хука нет — нет и скобки: простой ввод, без пляски с побудкой.
//
// Текущая смесь способов ввода (это направленный эксперимент):
//   * Клавиатура → НАСТРАИВАЕМАЯ, умолчание SendMessage. Похоже, WndProc у PW пропускает ввод с
//     клавиатуры только при определённом внутреннем состоянии «активен»; синхронная доставка
//     гарантирует, что PW дообработал нажатие до того, как мы пойдём дальше. Что наблюдалось с
//     вариантом на PostMessage: 1–2 агента из 11 время от времени пропускали широковещательный
//     имун даже с паузой на устаканивание в 30–50 мс. Выбор перестал быть зашитым в этот файл и
//     переехал в настройки (InputSettings.DefaultMethod плюс переопределение на профиль), но
//     УМОЛЧАНИЕ ОСТАЛОСЬ ТЕМ ЖЕ: появление настройки не имеет права менять поведение работающей
//     установки. Спрашивается способ на каждое нажатие — см. KeyboardInputResolver.
//   * Мышь → PostMessage, и это НЕ настраивается. Клики доходят до PW надёжно в любом случае
//     (видимо, в обработчике мыши у PW проверки фокуса нет), так что блокирующие накладные
//     расходы SendMessage тут ничем не оправданы, а ручка, у которой нет второго правильного
//     положения, — лишнее состояние, о котором придётся помнить при разборе «почему не сработало».
//
// Если это окажется стабильным, ради единообразия можно схлопнуть всё в Send. Если надёжность
// клавиатуры так и останется проблемой — значит, дело внутри PW (проверка состояния, на которую
// снаружи не повлиять), и следующий шаг — рассылка кликами по иконкам вместо клавиш.
[SupportedOSPlatform("windows")]
public sealed class GameWindowFactory : IGameWindowFactory
{
    private readonly ISettingsSource _settings;
    private readonly KeyboardInputResolver _keyboard;
    private readonly ILoggerFactory _loggerFactory;

    public GameWindowFactory(
        ISettingsSource settings,
        KeyboardInputResolver keyboard,
        ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _keyboard = keyboard;
        _loggerFactory = loggerFactory;
    }

    public IGameWindow Create(ProcessInfo info)
    {
        // Хук ЗАПЕКАЕТСЯ в окно здесь и больше не пересматривается — обоснование записано у поля
        // в GameWindow. Правка хука применяется к окнам, появившимся после неё; пороги машинного
        // зрения и способ ввода, наоборот, живые, и окно читает их само.
        //
        // null здесь — рабочая семантика «обычный процесс», и подставлять на этом месте что-либо
        // по таблице известных сигналов нельзя: таблица применяется ПРИ СОЗДАНИИ хука, а не при
        // чтении, иначе у notepad сигнал однажды взялся бы «сам» (см. KnownActivationSignals).
        return new GameWindow(
            info,
            _settings.Current.FindHook(info.ProcessName),
            _keyboard,
            new PostMessageMouseInput(),
            _settings,
            _loggerFactory.CreateLogger<GameWindow>());
    }
}
