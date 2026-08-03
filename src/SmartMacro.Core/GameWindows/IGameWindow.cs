using SmartMacro.Native;

namespace SmartMacro.GameWindows;

// Фасад одного окна, объединяющий ввод и захват. PW замораживает фоновые клиенты — встаёт и
// ввод, и отрисовка, — поэтому любая работа с окном, которое не на переднем плане, требует
// сначала явной активации. Жизненным циклом теперь управляет вызывающий, через
// ActivateAsync/DeactivateAsync:
//
//   await window.ActivateAsync();
//   try
//   {
//       await window.PressKeyAsync(...);     // или сколько угодно вызовов ввода
//       await window.ClickAsync(...);
//   }
//   finally
//   {
//       await window.DeactivateAsync();
//   }
//
// Последовательность идёт внутри ОДНОЙ активации, и это уводит от тонких гонок, в которых PW
// теряет накопленный ввод при деактивации между вызовами. CaptureScreenshot — единственное
// самодостаточное исключение: он распоряжается активацией сам, потому что его дёргают
// синхронные пути UI.
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
    /// упала / пользователь закрыл). Цикл опроса в CharacterAgent по нему сам себя завершает,
    /// когда клиента не стало.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// Размер клиентской области в пикселях. <c>(0, 0)</c> обычно означает, что окно свёрнуто в
    /// трей либо что процесс — это лаунчер, который не рисует настоящий игровой клиент.
    /// CharacterAgentFactory смотрит сюда при создании агента, чтобы отсеять окна, с которых
    /// нечего захватывать.
    /// </summary>
    (int Width, int Height) ClientSize { get; }

    /// <summary>
    /// Отправляет <c>WM_ACTIVATEAPP(TRUE)</c>, чтобы разбудить (возможно, замороженный) клиент
    /// PW, и выжидает паузу на устаканивание, чтобы движок был готов принимать ввод. Вызывающий
    /// обязан спарить это с <see cref="DeactivateAsync"/> в try/finally вокруг каждой сессии
    /// ввода.
    /// </summary>
    Task ActivateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Выжидает паузу на слив перед деактивацией (даёт насосу сообщений PW дообработать всё
    /// отправленное до того, как окно станет неактивным, — без этого накопленные
    /// WM_KEYDOWN/CLICK могут потеряться), затем отправляет <c>WM_ACTIVATEAPP(FALSE)</c> — если
    /// только это окно не находится сейчас на переднем плане (пользователь с ним активно
    /// работает, вырывать фокус нельзя).
    /// </summary>
    Task DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Отправляет одну пару «клавиша нажата / отпущена». Активацией НЕ управляет — обернуть в
    /// <see cref="ActivateAsync"/>/<see cref="DeactivateAsync"/> обязан вызывающий.
    /// </summary>
    Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отправляет клик левой кнопкой в указанную точку клиентской области. Активацией НЕ
    /// управляет.
    /// </summary>
    Task ClickAsync(ScreenPoint point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отправляет двойной клик левой кнопкой в указанную точку клиентской области. Активацией
    /// НЕ управляет.
    /// </summary>
    Task DoubleClickAsync(ScreenPoint point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Активный захват: будит (возможно, замороженный) клиент PW через WM_ACTIVATEAPP, чтобы
    /// PrintWindow вернул свежий кадр, и деактивирует обратно, если окно не на переднем плане.
    /// Жизненный цикл активации замкнут внутри — это для одиночных захватов по инициативе
    /// пользователя (диалог метки), где свежесть кадра критична, а вызывающий не ведёт сессию
    /// ввода.
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
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>ЦЕНТР совпадения в клиентских координатах или <c>null</c>, если ничто не набрало выше порога (либо захват не удался).</returns>
    Task<ScreenPoint?> FindElementAsync(byte[] elementTemplate, ScreenRect position,
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
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>
    /// ЦЕНТР совпадения в клиентских координатах в первый же раз, когда оно набирает выше
    /// порога; <c>null</c> по таймауту. Именно центр позволяет макросу «найти кнопку где угодно,
    /// а потом кликнуть по ней» через <c>FoundPointVar</c>.
    /// </returns>
    Task<ScreenPoint?> WaitForElementAsync(byte[] elementTemplate, ScreenRect position, TimeSpan waitDuration,
        CancellationToken cancellationToken = default);
}
