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
/// Профиль одного процесса: за кем следить и чем вводить.
///
/// Пришёл на смену <c>ProcessProfile</c> из <c>appsettings.json</c> — тот же смысл, но живёт в
/// Contracts, потому что теперь его правит панель, а не только читает демон.
///
/// <b>Скобки пробуждения здесь больше нет.</b> Магический lParam и обе паузы уехали в
/// <see cref="AppSettings.Hooks"/>: это одно понятие («как будить окна этого процесса»), и пока
/// оно лежало тремя полями профиля, четвёртой его частью была скобка в <c>GameWindow</c>, а пятой
/// — скобка в <c>AgentInputDispatcher</c>. Профиль отвечает ровно на два вопроса, которые
/// показывает экран, — за кем следить и чем вводить.
/// </summary>
public sealed record ProcessProfileSettings
{
    /// <summary>Имя процесса на уровне ОС, без расширения, например <c>elementclient_64</c>.</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// Способ ввода для этого процесса либо <c>null</c> — «взять из
    /// <see cref="InputSettings.DefaultMethod"/>».
    /// </summary>
    public InputMethod? InputMethod { get; init; }

    /// <summary>
    /// Запасной профиль для окон, чей процесс не описан в настройках: простой ввод и способ
    /// доставки по умолчанию.
    /// </summary>
    public static ProcessProfileSettings Inert { get; } = new();
}

/// <summary>
/// Вокруг чего срабатывает скобка пробуждения.
///
/// Названо <c>On</c>, а не <c>Triggers</c>, намеренно: «триггер» в этом проекте уже занят тем, что
/// ЗАПУСКАЕТ макрос (<c>HotkeyTrigger</c>, <c>ProcessAppearedTrigger</c>), и второе значение у
/// того же слова читалось бы как ошибка.
/// </summary>
public enum HookOn
{
    /// <summary>Нажатие клавиши и клик — то, что уходит в окно через <c>SendMessage</c>/<c>PostMessage</c>.</summary>
    Input = 0,

    /// <summary>
    /// Захват кадра: <c>PrintWindow</c> по замороженному фоновому клиенту возвращает устаревший
    /// или чёрный кадр, поэтому тик зрения тоже будит окно.
    /// </summary>
    Capture = 1,
}

/// <summary>
/// Насколько долго держится побудка.
///
/// <b>Флага для иконки здесь намеренно нет.</b> <c>WM_SETICON</c> сегодня проходит сквозь
/// замороженную очередь и работает; включить его в скобку значит поменять поведение, проверяемое
/// только на живой игре. У проекта на такой случай есть своё правило, записанное у
/// <see cref="InputMethod.SendInput"/>: предложить непроверенное хуже, чем не предлагать.
/// </summary>
public enum HookLifetime
{
    /// <summary>
    /// Сегодняшнее поведение и умолчание: каждое действие — свой цикл «разбудить → сделать →
    /// усыпить». Понодовая зернистость совпадает с тем, как читается граф.
    /// </summary>
    Action = 0,

    /// <summary>
    /// Окно, разбуженное впервые, не засыпает до конца прогона. Захват ЛЕНИВЫЙ: набор окон в
    /// начале прогона неизвестен, у каждой ноды свой селектор целей.
    ///
    /// Цена названа вслух: окно разбужено весь прогон, а не миллисекунды, и снятый посреди
    /// прогона демон оставит клиента размороженным до настоящей смены фокуса пользователем.
    /// </summary>
    Run = 1,
}

/// <summary>
/// Скобка пробуждения одного процесса — «хук процесса»: чем будить, сколько ждать, вокруг чего
/// срабатывать и как долго держаться.
///
/// <b>ОТСУТСТВИЕ КЛЮЧА В <see cref="AppSettings.Hooks"/> — ЭТО ОТСУТСТВИЕ СКОБКИ, А НЕ СКОБКА С
/// НУЛЯМИ.</b> На этом держатся профили не-игровых процессов: у <c>notepad</c> хука нет, и вся
/// пляска «побудка через <c>WM_ACTIVATEAPP</c> — деактивация» пропускается целиком. Поэтому
/// <see cref="ActivationLParam"/> здесь не nullable: раз запись есть — есть и сигнал, а «есть
/// запись, но сигнала нет» было бы вторым способом сказать то же самое, и эти два способа
/// разъехались бы.
///
/// В интерфейсе блока нет вовсе — ни числа, ни пауз. Само число добыто реверсом клиента Perfect
/// World, это костыль под одну игру, а не настройка; правится он в <c>settings.json</c>, и
/// <see cref="KnownActivationSignals"/> проставляет его ПРИ СОЗДАНИИ хука для узнанного имени.
/// </summary>
public sealed record ProcessHookSettings
{
    /// <summary>
    /// Магический lParam в паре с <c>WM_ACTIVATEAPP</c>, которым замороженного клиента будят
    /// перед вводом или захватом.
    /// </summary>
    public uint ActivationLParam { get; init; }

    /// <summary>
    /// Пауза после сигнала побудки — время на разморозку движка. Ниже 150 мс PW начинает терять
    /// ввод.
    /// </summary>
    public int SettleMs { get; init; }

    /// <summary>
    /// Пауза перед сигналом деактивации: ввод через <c>PostMessage</c> лежит в очереди цели и
    /// ещё не обработан, а PW, уходя в неактивное состояние, накопленное выбрасывает.
    /// </summary>
    public int DeactivateMs { get; init; }

    /// <summary>
    /// Вокруг каких операций скобка срабатывает. Умолчание — обе, потому что ровно так вело себя
    /// всё до появления этого блока.
    ///
    /// <b>Пустой набор законен:</b> хук есть, числа сохранены, не срабатывает нигде. Это не то же
    /// самое, что отсутствие ключа, — отсутствие теряет значения, пустой набор их бережёт.
    /// </summary>
    public IReadOnlyList<HookOn> On { get; init; } = [HookOn.Input, HookOn.Capture];

    /// <summary>Насколько долго держится побудка. Умолчание — сегодняшнее поведение.</summary>
    public HookLifetime Scope { get; init; } = HookLifetime.Action;

    /// <summary>Срабатывает ли скобка вокруг этой операции.</summary>
    /// <param name="on">Операция.</param>
    public bool Fires(HookOn on)
    {
        // Список, а не HashSet: в нём максимум два элемента, а читают его на каждое нажатие
        // клавиши — выделять множество ради двух сравнений нечего.
        for (var i = 0; i < On.Count; i++)
        {
            if (On[i] == on)
            {
                return true;
            }
        }

        return false;
    }
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
    /// Скобки пробуждения по имени процесса. <b>Ключа нет — скобки нет</b>, см.
    /// <see cref="ProcessHookSettings"/>.
    ///
    /// Отдельно от <see cref="Profiles"/>, хотя ключ у обоих один и тот же — имя процесса.
    /// Вопросы разные и живут по-разному: профиль отвечает «следить ли за этим процессом»
    /// (снимается галочкой в панели), хук — «как будить его окна» (добыт реверсом, в панели его
    /// нет и удалить его панели нечем). Поэтому снятый профиль хук за собой не уносит: число,
    /// которое нельзя набрать заново, не имеет права стирать интерфейс, который его не
    /// показывает.
    /// </summary>
    public IReadOnlyDictionary<string, ProcessHookSettings> Hooks { get; init; } =
        new Dictionary<string, ProcessHookSettings>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// То, что демон запишет в <c>settings.json</c>, не найдя файла при первом старте.
    ///
    /// Профиль PW и его хук входят в умолчания намеренно: ровно это лежало в
    /// <c>appsettings.json</c> до волны живых настроек, и свежая установка обязана вести себя так
    /// же, как установка, которую обновили.
    /// </summary>
    public static AppSettings Default { get; } = new()
    {
        Profiles = [new ProcessProfileSettings { ProcessName = "elementclient_64" }],
        Hooks = new Dictionary<string, ProcessHookSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["elementclient_64"] = KnownActivationSignals.NewHook("elementclient_64")!,
        },
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
    /// Находит скобку пробуждения по имени процесса. Регистр не важен — в Windows имена процессов
    /// регистронезависимы, а ключи словаря приходят из файла, правленного руками.
    /// </summary>
    /// <param name="processName">Имя процесса без расширения.</param>
    /// <returns>Хук или <c>null</c> — «обычный процесс, пробуждение пропускается».</returns>
    public ProcessHookSettings? FindHook(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        // Перебором, а не TryGetValue: словарь приезжает из JSON с компаратором по умолчанию
        // (порядковым), а искать надо без учёта регистра — ровно как FindProfile. Записей здесь
        // единицы, и читается это один раз на создание окна.
        foreach (var (name, hook) in Hooks)
        {
            if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
            {
                return hook;
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
