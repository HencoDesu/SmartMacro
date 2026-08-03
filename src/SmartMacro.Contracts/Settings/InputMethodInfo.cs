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
            "в фоне ✓ · Shift+1 ✕",
            "Синхронно, по фоновым окнам. Ждёт ответа каждого окна: на десяти клиентах заметно "
            + "медленнее. Модификаторы (Shift+1) не работают — PW читает их через GetKeyState, "
            + "который межпоточный SendMessage не обновляет. Умолчание: именно на нём работает игра.",
            IsAvailable: true),

        new(InputMethod.PostMessage,
            "PostMessage",
            "в фоне ✓ · Shift+1 ✕",
            "Асинхронно, по фоновым окнам. Быстрее всех, но факт доставки не подтверждается: "
            + "с ним 1–2 клиента из 11 периодически теряли широковещательное нажатие. "
            + "Модификаторы (Shift+1) не работают.",
            IsAvailable: true),

        new(InputMethod.SendInput,
            "SendInput",
            "в фоне ✕ · Shift+1 ✓",
            "Через очередь ввода ОС. Единственный способ, который умеет сочетания с модификаторами, "
            + "но требует переднего плана: SmartMacro отберёт фокус, а действия по десяти окнам "
            + "пойдут по очереди. Пока не реализован.",
            IsAvailable: false),
    ];

    /// <summary>Только те способы, которые интерфейс вправе предлагать.</summary>
    public static IReadOnlyList<InputMethodInfo> Available { get; } =
        [.. All.Where(static info => info.IsAvailable)];

    /// <summary>Описание одного способа. Незнакомый разрешается в <see cref="InputMethod.SendMessage"/>.</summary>
    public static InputMethodInfo Of(InputMethod method) =>
        All.FirstOrDefault(info => info.Method == method) ?? All[0];
}
