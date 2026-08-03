using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Settings;
using SmartMacro.Native;
using SmartMacro.Native.Keyboard;
using SmartMacro.Settings;

namespace SmartMacro.Input;

/// <summary>
/// Выбирает способ доставки нажатия по настройкам — <b>на каждый вызов, а не один раз при
/// создании окна</b>. Именно это и делает настройку «ввод по умолчанию» живой: смена применяется
/// со следующего цикла активации, без перезапуска демона и без переоткрытия клиента.
///
/// Обе реализации не имеют состояния и держатся полями: переключение способа не должно ничего
/// выделять и уж точно не должно ничего инициализировать посреди прогона.
///
/// <b>Мышь сюда не входит.</b> Клики всегда уходят <c>PostMessage</c>: в обработчике мыши у PW
/// проверки фокуса нет, они доходили надёжно всегда, и блокирующие накладные расходы
/// <c>SendMessage</c> на них ничем не оправданы. Спорной всегда была клавиатура — см.
/// <c>GameWindowFactory</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class KeyboardInputResolver
{
    private readonly ISettingsSource _settings;
    private readonly ILogger<KeyboardInputResolver> _logger;
    private readonly SendMessageKeyboardInput _send = new();
    private readonly PostMessageKeyboardInput _post = new();

    // Про нереализованный способ докладываем ОДИН раз на процесс, а не на каждое нажатие: иначе
    // одна правка файла руками превратила бы журнал в сплошную стену из одной и той же строки —
    // а именно в журнал человек и пойдёт смотреть, почему клавиши идут не тем способом.
    private int _sendInputWarned;

    public KeyboardInputResolver(ISettingsSource settings, ILogger<KeyboardInputResolver> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Чем стучаться в окна этого процесса прямо сейчас: способ профиля, иначе умолчание.
    /// </summary>
    /// <param name="processName">Имя процесса окна.</param>
    public IKeyboardInput For(string processName)
    {
        var method = _settings.Current.InputMethodFor(processName);
        switch (method)
        {
            case InputMethod.PostMessage:
                return _post;

            case InputMethod.SendInput:
                // Способ объявлен в модели, но не реализован, и интерфейс его не предлагает —
                // сюда можно попасть только правкой файла руками. Молча подменять способ нельзя:
                // симптомом была бы «настройка, которая ни на что не влияет».
                if (Interlocked.Exchange(ref _sendInputWarned, 1) == 0)
                {
                    LogSendInputUnavailable(processName);
                }

                return _send;

            case InputMethod.SendMessage:
            default:
                return _send;
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "Способ ввода SendInput ещё не реализован (процесс '{ProcessName}') — клавиши идут через SendMessage. "
        + "Значение попало в settings.json правкой руками: интерфейс этот способ не предлагает.")]
    partial void LogSendInputUnavailable(string processName);
}
