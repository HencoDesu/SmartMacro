using SmartMacro.Native;

namespace SmartMacro.Contracts.Settings;

/// <summary>
/// Способ доставки нажатия клавиши в окно.
///
/// <b>Про клавиатуру, а не про мышь.</b> Клики всегда уходят <c>PostMessage</c> и настройке не
/// подчиняются: в обработчике мыши у PW проверки фокуса нет, клики доходили надёжно в любом
/// случае, а блокирующие накладные расходы <c>SendMessage</c> на них ничем не оправданы. Спорным
/// местом всегда была именно клавиатура — см. комментарий к <c>GameWindowFactory</c>.
/// </summary>
public enum InputMethod
{
    /// <summary>
    /// Синхронно, по фоновым окнам. Ждёт ответа каждого окна: на десяти клиентах заметно
    /// медленнее. Умолчание — на нём стоит вся отлаженная работа с PW.
    /// </summary>
    SendMessage = 0,

    /// <summary>
    /// Асинхронно, по фоновым окнам. Быстрее всех, но факт доставки не подтверждается: с ним
    /// 1–2 клиента из 11 периодически теряли широковещательное нажатие.
    /// </summary>
    PostMessage = 1,

    /// <summary>
    /// Через очередь ввода ОС. Единственный, кто умеет сочетания с модификаторами (Shift+1), но
    /// требует переднего плана.
    ///
    /// <b>МЕСТО ЗАРЕЗЕРВИРОВАНО, РЕАЛИЗАЦИИ НЕТ.</b> Член объявлен, чтобы файл настроек и
    /// протокол не пришлось менять, когда способ появится; интерфейс его не предлагает, потому
    /// что проверить его без живой игры нельзя, а предложить непроверенный способ хуже, чем не
    /// предложить. Значение, попавшее сюда правкой файла руками, разрешается в
    /// <see cref="SendMessage"/> — с предупреждением в журнале, а не молча.
    /// </summary>
    SendInput = 2,
}

/// <summary>
/// Профиль одного процесса: за кем следить и как будить его окна перед вводом.
///
/// Пришёл на смену <c>ProcessProfile</c> из <c>appsettings.json</c> — тот же смысл, но живёт в
/// Contracts, потому что теперь его правит панель, а не только читает демон.
/// </summary>
public sealed record ProcessProfileSettings
{
    /// <summary>Имя процесса на уровне ОС, без расширения, например <c>elementclient_64</c>.</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// Магический lParam в паре с <c>WM_ACTIVATEAPP</c>, которым замороженного клиента будят
    /// перед вводом.
    ///
    /// <b><c>null</c> — не «не задано», а рабочая семантика: «обычный процесс, пробуждение
    /// пропускается».</b> На ней держатся профили не-игровых процессов, и потерять её нельзя.
    /// Поэтому <see cref="KnownActivationSignals"/> применяется ПРИ СОЗДАНИИ профиля для
    /// узнанного имени, а не при чтении: <c>elementclient_64</c> получает 37336 сам, профиль для
    /// <c>notepad</c> остаётся без сигнала, и обе записи означают ровно то, что в них написано.
    ///
    /// В интерфейсе поля нет вовсе — это костыль под одну игру, а не настройка. У того, кому
    /// попадётся сборка с другим значением, остаётся выход через файл.
    /// </summary>
    public uint? ActivationLParam { get; init; }

    /// <summary>Пауза после сигнала побудки перед вводом — время на разморозку движка. Ниже 150 мс ввод начинает теряться.</summary>
    public int SettleDelayMs { get; init; }

    /// <summary>Пауза перед сигналом деактивации, чтобы насос сообщений цели успел разобрать очередь ввода.</summary>
    public int DeactivationDelayMs { get; init; }

    /// <summary>
    /// Способ ввода для этого процесса либо <c>null</c> — «взять из
    /// <see cref="InputSettings.DefaultMethod"/>».
    /// </summary>
    public InputMethod? InputMethod { get; init; }

    /// <summary>
    /// Запасной профиль для окон, чей процесс не описан в настройках: простой ввод, без
    /// задержек, без пляски с побудкой.
    /// </summary>
    public static ProcessProfileSettings Inert { get; } = new();
}

/// <summary>Два разных цикла опроса. Оба применяются живьём — со следующего тика.</summary>
public sealed record WatchSettings
{
    /// <summary>Как часто перечисляются процессы ОС, чтобы заметить запуск клиента.</summary>
    public int ProcessPollIntervalSeconds { get; init; } = 1;

    /// <summary>Как часто обходится реестр окон, чтобы заметить закрытие.</summary>
    public int WindowPollIntervalSeconds { get; init; } = 2;
}

/// <summary>Способ ввода по умолчанию — тот, что берут профили без собственного.</summary>
public sealed record InputSettings
{
    /// <summary>
    /// Умолчание для всех процессов. <see cref="SmartMacro.Contracts.Settings.InputMethod.SendMessage"/>
    /// — потому что именно на нём работает PW; см. пояснения у самого перечисления.
    /// </summary>
    public InputMethod DefaultMethod { get; init; } = InputMethod.SendMessage;
}

/// <summary>
/// Настройки сопоставителя текста класса в окне характеристик: область обрезки и пороги.
///
/// Живой путь берёт область из ноды <c>RecognizeTag</c>; здешняя нужна диагностическому
/// <c>DebugBinarizeClassRegion</c> («Дамп» в режиме «Окна»).
/// </summary>
public sealed record ClassMatcherSettings
{
    /// <summary>Прямоугольник со значением класса. Умолчание — авторский скриншот 4K.</summary>
    public ScreenRect Region { get; init; } = new(3200, 1060, 160, 35);

    /// <summary>Отсечка по яркости для бинаризации: текст класса светлый по тёмной панели.</summary>
    public double LuminanceThreshold { get; init; } = 200.0;

    /// <summary>Отсечка по оценке <c>TM_CCOEFF_NORMED</c> для текста класса.</summary>
    public double MatchThreshold { get; init; } = 0.6;
}

/// <summary>Машинное зрение. Применяется живьём — со следующего тика сопоставления.</summary>
public sealed record VisionSettings
{
    /// <summary>
    /// Порог совпадения по умолчанию для <c>Find</c> / <c>Wait</c>.
    ///
    /// <b>Умолчание, а не общий порог:</b> шаг вправе задать свой, и тогда это значение к нему
    /// не относится. Сколько именно шагов так делают, интерфейс НЕ показывает — чтобы назвать
    /// число, панели пришлось бы пересчитывать все графы библиотеки, и устарело бы оно при
    /// первой же правке любого из них.
    /// </summary>
    public double MatchThreshold { get; init; } = 0.7;

    /// <summary>Как часто <c>WaitForElement</c> заново захватывает кадр и сопоставляет.</summary>
    public int PollIntervalMs { get; init; } = 500;

    /// <summary>Сопоставитель класса — область и пороги.</summary>
    public ClassMatcherSettings ClassMatcher { get; init; } = new();
}

/// <summary>
/// Автозапуск и права. <b>Единственная часть настроек, которая живёт не в процессе</b>: её
/// применение — это ключ реестра и задача в Планировщике, а не поле, которое кто-то перечитает.
///
/// Две галочки, способ выбирает приложение:
/// <list type="table">
///   <item><term>— / —</term><description>ничего не регистрируется</description></item>
///   <item><term>✓ / —</term><description>ключ <c>Run</c> в <c>HKCU</c></description></item>
///   <item><term>— / ✓</term><description>автозапуска нет; при ручном старте перезапуск через <c>runas</c>, если не повышен</description></item>
///   <item><term>✓ / ✓</term><description>задача в Планировщике с наивысшими правами — ключ <c>Run</c> так не умеет</description></item>
/// </list>
/// </summary>
public sealed record StartupSettings
{
    /// <summary>Поднимать демон вместе с сеансом пользователя.</summary>
    public bool RunAtLogon { get; init; }

    /// <summary>
    /// Работать с правами администратора.
    ///
    /// <b>Смена требует перезапуска демона</b> — повышение нельзя получить на лету, — и
    /// <b>снятие ломает ввод в окна игры, запущенной от администратора</b>: UIPI заблокирует и
    /// сообщения, и захват экрана. Интерфейс обязан сказать и то и другое: симптом будет
    /// «макросы перестали работать», а причина — галочка, снятая неделю назад.
    /// </summary>
    public bool RunElevated { get; init; } = true;
}

/// <summary>
/// Всё, что настраивается, одним снимком.
///
/// <b>Уровень журнала сюда НЕ входит, и это принципиально.</b> Он остаётся в
/// <c>appsettings.json</c> вопреки общему правилу его ужать: если демон падает на чтении файла
/// настроек, отладить это можно только тем уровнем, который был известен ДО чтения. Настройка,
/// ломающая возможность диагностировать собственную поломку, — плохая настройка. Экран настроек
/// меняет уровень на лету через <c>LoggingLevelSwitch</c>, ничего не переписывая, и обязан
/// сказать, что до перезапуска.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Интервалы опроса.</summary>
    public WatchSettings Watch { get; init; } = new();

    /// <summary>Способ ввода по умолчанию.</summary>
    public InputSettings Input { get; init; } = new();

    /// <summary>Пороги и интервалы машинного зрения.</summary>
    public VisionSettings Vision { get; init; } = new();

    /// <summary>Автозапуск и права.</summary>
    public StartupSettings Startup { get; init; } = new();

    /// <summary>
    /// Профили процессов. Пусто = ни за одним процессом не следим (законно, но почти наверняка
    /// не то, чего хотели).
    /// </summary>
    public IReadOnlyList<ProcessProfileSettings> Profiles { get; init; } = [];

    /// <summary>
    /// То, что демон запишет в <c>settings.json</c>, не найдя файла при первом старте.
    ///
    /// Профиль PW входит в умолчания намеренно: ровно он лежал в <c>appsettings.json</c> до
    /// этой волны, и свежая установка обязана вести себя так же, как установка, которую
    /// обновили.
    /// </summary>
    public static AppSettings Default { get; } = new()
    {
        Profiles =
        [
            new ProcessProfileSettings
            {
                ProcessName = "elementclient_64",
                ActivationLParam = KnownActivationSignals.For("elementclient_64"),
                SettleDelayMs = 200,
                DeactivationDelayMs = 100,
            },
        ],
    };

    /// <summary>
    /// Находит профиль по имени процесса. Регистр не важен — в Windows имена процессов
    /// регистронезависимы. При дублях выигрывает первое совпадение.
    /// </summary>
    /// <param name="processName">Имя процесса без расширения.</param>
    /// <returns>Подходящий профиль или <c>null</c>.</returns>
    public ProcessProfileSettings? FindProfile(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        foreach (var profile in Profiles)
        {
            if (string.Equals(profile.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Объединение имён процессов, за которыми надо следить: без повторов (регистр не важен),
    /// пустые записи пропущены, исходный порядок сохранён.
    /// </summary>
    public IReadOnlyList<string> WatchedProcessNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(Profiles.Count);
        foreach (var profile in Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.ProcessName))
            {
                continue;
            }

            if (seen.Add(profile.ProcessName))
            {
                names.Add(profile.ProcessName);
            }
        }

        return names;
    }

    /// <summary>
    /// Способ ввода, которым надо стучаться в окна этого процесса: собственный способ профиля,
    /// иначе умолчание.
    /// </summary>
    /// <param name="processName">Имя процесса без расширения.</param>
    public InputMethod InputMethodFor(string? processName) =>
        FindProfile(processName)?.InputMethod ?? Input.DefaultMethod;
}
