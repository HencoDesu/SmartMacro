using SmartMacro.Resources;

namespace SmartMacro.Contracts.Settings;

/// <summary>
/// Как способ ввода называется и чем он платит — <b>в одном месте на всю программу</b>.
///
/// Подсказка про каждый способ нужна дважды: в списке «Ввод по умолчанию» и на кнопках выбора
/// ввода у конкретного процесса. Скопировать её в разметку два раза — значит гарантированно
/// получить две разные версии: правку внесут в ту, что попалась на глаза, и расхождение никто
/// не заметит, потому что оба текста по отдельности выглядят правдоподобно. Поэтому текст живёт
/// в коде, а разметка его только показывает.
/// </summary>
/// <param name="Method">Сам способ.</param>
/// <param name="Title">Как он называется — латиницей, потому что это имя функции Win32.</param>
/// <param name="Badge">
/// Две главные характеристики одной строкой: работает ли по фоновым окнам и умеет ли сочетания
/// с модификаторами. Ровно то, что стоит рядом с пунктом списка в макете.
/// </param>
/// <param name="Detail">Полная подсказка при наведении: как работает и чем платит.</param>
/// <param name="IsAvailable">
/// <c>false</c> — способ объявлен в модели, но не реализован. Интерфейс такие НЕ предлагает:
/// показать в списке непроверенный способ хуже, чем не показать.
/// </param>
public sealed record InputMethodInfo(
    InputMethod Method,
    string Title,
    string Badge,
    string Detail,
    bool IsAvailable)
{
    /// <summary>Все способы, включая нереализованные, в порядке объявления.</summary>
    public static IReadOnlyList<InputMethodInfo> All { get; } =
    [
        new(InputMethod.SendMessage,
            "SendMessage",
            Strings_Engine.Settings_Engine_InputMethod_SendMessage_Badge,
            Strings_Engine.Settings_Engine_InputMethod_SendMessage_Detail,
            IsAvailable: true),

        new(InputMethod.PostMessage,
            "PostMessage",
            Strings_Engine.Settings_Engine_InputMethod_PostMessage_Badge,
            Strings_Engine.Settings_Engine_InputMethod_PostMessage_Detail,
            IsAvailable: true),

        new(InputMethod.SendInput,
            "SendInput",
            Strings_Engine.Settings_Engine_InputMethod_SendInput_Badge,
            Strings_Engine.Settings_Engine_InputMethod_SendInput_Detail,
            IsAvailable: false),
    ];

    /// <summary>Только те способы, которые интерфейс вправе предлагать.</summary>
    public static IReadOnlyList<InputMethodInfo> Available { get; } =
        [.. All.Where(static info => info.IsAvailable)];

    /// <summary>Описание одного способа. Незнакомый разрешается в <see cref="InputMethod.SendMessage"/>.</summary>
    public static InputMethodInfo Of(InputMethod method) =>
        All.FirstOrDefault(info => info.Method == method) ?? All[0];
}
