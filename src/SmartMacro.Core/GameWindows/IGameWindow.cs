using SmartMacro.Contracts.Settings;
using SmartMacro.Native;

namespace SmartMacro.GameWindows;

// Фасад одного окна, объединяющий ввод и захват. PW замораживает фоновые клиенты — встаёт и
// ввод, и отрисовка, — поэтому любая работа с окном, которое не на переднем плане, требует
// сначала явной побудки. Управляет этим вызывающий, ОБЛАСТЬЮ:
//
//   await using var scope = await window.EnterHookAsync(HookOn.Input, ct);
//   await window.PressKeyAsync(...);     // или сколько угодно вызовов ввода
//   await window.ClickAsync(...);
//
// Последовательность идёт внутри ОДНОЙ побудки, и это уводит от тонких гонок, в которых PW
// теряет накопленный ввод при деактивации между вызовами. Раньше здесь стояла пара
// ActivateAsync/DeactivateAsync, которую вызывающий был обязан спарить в try/finally сам, — и
// четыре из пяти мест этого не делали; почему область, написано у WindowHookScope.
// CaptureScreenshot — единственное самодостаточное исключение: он открывает область сам, потому
// что его дёргают синхронные пути UI.
//
// ЧЕМ будить (магический lParam) и КАК ДОЛГО (одно действие или весь прогон) — это хук процесса,
// ProcessHookSettings в settings.json. Окна, чей процесс хука не имеет, не будят и не
// замораживают вовсе: область у них пустая.
//
// Примитива для аккорда (Shift+N) здесь намеренно нет: PW читает состояние модификаторов через
// GetKeyState, а межпоточный SendMessage его никогда не обновляет, так что впрыснутый аккорд
// приезжает голой клавишей. Ответ на уровне макроса — ClickNode по тому элементу интерфейса, до
// которого аккорд должен был дотянуться (см. пример pw-assist, который кликает по первому слоту
// пати).
public interface IGameWindow
{
    IntPtr Handle { get; }

    /// <summary>
    /// Дешёвая проверка живости — <c>false</c>, как только окно под капотом уничтожено (игра
    /// упала / пользователь закрыл). По нему <c>WindowLifetimeMonitor</c> и снимает окно с
    /// регистрации, когда клиента не стало.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// Размер клиентской области в пикселях. <c>(0, 0)</c> обычно означает, что окно свёрнуто в
    /// трей либо что процесс — это лаунчер, который не рисует настоящий игровой клиент.
    /// Оркестратор смотрит сюда, беря окно под управление, чтобы отсеять те, с которых нечего
    /// захватывать.
    /// </summary>
    (int Width, int Height) ClientSize { get; }

    /// <summary>
    /// Открывает область пробуждения: первая посылает <c>WM_ACTIVATEAPP(TRUE)</c> и выжидает
    /// паузу на устаканивание, вложенные только считаются. Закрытие последней области выжидает
    /// паузу на слив (если внутри был ввод) и отправляет <c>WM_ACTIVATEAPP(FALSE)</c> — если
    /// только окно не на переднем плане.
    ///
    /// Пустая область (<see cref="WindowHookScope.IsHolding"/> = <c>false</c>) означает «скобки
    /// нет»: у процесса нет хука либо хук не срабатывает вокруг <paramref name="on"/>. Закрывать
    /// её всё равно обязательно — <c>await using</c> и делает это за вызывающего.
    /// </summary>
    /// <param name="on">Вокруг чего скобка: ввод или захват кадра.</param>
    /// <param name="cancellationToken">
    /// Отмена ПОБУДКИ. В закрытие области токен не уходит и уйти не может — см.
    /// <see cref="IWindowHookHolder.ExitHookAsync"/>.
    /// </param>
    ValueTask<WindowHookScope> EnterHookAsync(HookOn on, CancellationToken cancellationToken = default);

    /// <summary>
    /// Синхронный близнец для синхронных путей захвата (<see cref="CaptureScreenshot"/>, «Дамп
    /// захватов»).
    /// </summary>
    /// <param name="on">Вокруг чего скобка.</param>
    WindowHookScope EnterHook(HookOn on);

    /// <summary>
    /// <c>true</c>, когда хук этого окна велит держать побудку ВЕСЬ ПРОГОН
    /// (<see cref="HookLifetime.Run"/>) вокруг операций вида <paramref name="on"/>.
    ///
    /// Спрашивает <c>MacroRunHooks</c>: ссылку на весь прогон берёт обходчик, а не примитив, —
    /// иначе слою «ввод, зрение и косметика по hwnd» пришлось бы знать, внутри какого прогона он
    /// работает.
    /// </summary>
    /// <param name="on">Вокруг чего скобка.</param>
    bool WakesForWholeRun(HookOn on);

    /// <summary>
    /// Отправляет одну пару «клавиша нажата / отпущена». Побудкой НЕ управляет — открыть
    /// область <see cref="EnterHookAsync"/> обязан вызывающий.
    /// </summary>
    Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отправляет клик левой кнопкой в указанную точку клиентской области. Побудкой НЕ
    /// управляет.
    /// </summary>
    Task ClickAsync(ScreenPoint point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отправляет двойной клик левой кнопкой в указанную точку клиентской области. Побудкой
    /// НЕ управляет.
    /// </summary>
    Task DoubleClickAsync(ScreenPoint point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Активный захват: будит (возможно, замороженный) клиент PW через WM_ACTIVATEAPP, чтобы
    /// PrintWindow вернул свежий кадр, и замораживает обратно, если окно не на переднем плане.
    /// Область замкнута внутри — это для одиночных захватов по инициативе пользователя (диалог
    /// метки, «Дамп захватов»), где свежесть кадра критична, а вызывающий не ведёт сессию ввода.
    /// </summary>
    byte[] CaptureScreenshot();

    /// <summary>
    /// Подменяет иконку игрового окна в заголовке и на панели задач содержимым файла
    /// изображения (.ico через LoadImage, .png/.jpg через GDI+). Нужно, чтобы вытащить иконку
    /// класса персонажа на панель задач и пользователь мог различить девять окон PW с одного
    /// взгляда.
    /// </summary>
    /// <returns><c>false</c>, если файл не удалось загрузить (нет, повреждён, неподдерживаемый формат).</returns>
    bool SetIconFromFile(string imagePath);

    /// <summary>
    /// Один захват и одно сопоставление шаблона. Одноразовый близнец
    /// <see cref="WaitForElementAsync"/>, за которым стоит <c>FindElementNode</c>.
    /// </summary>
    /// <param name="elementTemplate">Изображение искомого элемента интерфейса, закодированное в PNG.</param>
    /// <param name="position">Область обрезки в клиентских координатах. Пустая (Width=0 или Height=0) означает «искать по всей клиентской области».</param>
    /// <param name="matchThreshold">Порог совпадения, заданный НОДОЙ; <c>null</c> = настроенное умолчание окна.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>ЦЕНТР совпадения в клиентских координатах или <c>null</c>, если ничто не набрало выше порога (либо захват не удался).</returns>
    Task<ScreenPoint?> FindElementAsync(byte[] elementTemplate, ScreenRect position, double? matchThreshold = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Опрашивает окно, пока <paramref name="elementTemplate"/> не совпадёт внутри
    /// <paramref name="position"/> — или пока не истечёт <paramref name="waitDuration"/>. Темп
    /// внутреннего опроса и порог совпадения берутся из опций окна (настраиваются через
    /// "Vision:Window" в appsettings.json).
    /// </summary>
    /// <param name="elementTemplate">Изображение искомого элемента интерфейса, закодированное в PNG.</param>
    /// <param name="position">Область обрезки в клиентских координатах. Пустая (Width=0 или Height=0) означает «искать по всей клиентской области».</param>
    /// <param name="waitDuration">Общий бюджет времени — как только он исчерпан, сдаёмся.</param>
    /// <param name="matchThreshold">Порог совпадения, заданный НОДОЙ; <c>null</c> = настроенное умолчание окна.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>
    /// ЦЕНТР совпадения в клиентских координатах в первый же раз, когда оно набирает выше
    /// порога; <c>null</c> по таймауту. Именно центр позволяет макросу «найти кнопку где угодно,
    /// а потом кликнуть по ней» через <c>FoundPointVar</c>.
    /// </returns>
    Task<ScreenPoint?> WaitForElementAsync(byte[] elementTemplate, ScreenRect position, TimeSpan waitDuration,
        double? matchThreshold = null, CancellationToken cancellationToken = default);
}
