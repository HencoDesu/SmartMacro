using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SmartMacro.Contracts.Settings;
using SmartMacro.Input;
using SmartMacro.Native;
using SmartMacro.Native.Window;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Settings;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SmartMacro.GameWindows;

[SupportedOSPlatform("windows")]
public sealed partial class GameWindow : IGameWindow, IWindowHookHolder
{
    private readonly KeyboardInputResolver _keyboard;
    private readonly IMouseInput _mouse;
    private readonly ISettingsSource _settings;
    private readonly string _processName;

    private readonly INativeWindow _nativeWindow;

    // ХУК ЗАПЕЧЁН ПРИ СОЗДАНИИ, и это осознанно, в отличие от порогов и способа ввода ниже. Хук
    // описывает, КАК будить именно это окно; смени его на лету — и окно, чей хук пользователь
    // удалил или переименовал посреди прогона, молча стало бы «обычным процессом», то есть
    // перестало бы просыпаться, не сказав ни слова. Правка хука применяется к окнам, появившимся
    // после неё; клиент для этого достаточно переоткрыть.
    //
    // Null = процесс с простым вводом (записи в Hooks нет): вся пляска «побудка через
    // WM_ACTIVATEAPP — деактивация» пропускается.
    private readonly ProcessHookSettings? _hook;
    private readonly ILogger<GameWindow> _logger;

    // ---- счётчик областей пробуждения -------------------------------------------------------
    //
    // СЧЁТЧИК НА ОКНО: первый вход будит, последний выход замораживает. Заведён потому, что одно
    // окно могут захотеть двое — демон во время прогона и панель во время снимка, — а ещё потому,
    // что Scope: Run держит ссылку на весь прогон, пока ноды открывают и закрывают свои.
    // Та же фигура, что у аренды хоткеев (HotkeyListener) и у счёта отладчиков
    // (MacroDebugSession): считаем держателей, а не флаг.
    //
    // ⚠️ ЗЕРНИСТОСТЬ ОТ СЧЁТЧИКА НЕ МЕНЯЕТСЯ. Последовательные ноды входят и выходят полностью
    // (0→1→0), так что цикл пробуждения остаётся один на ноду — ровно как было. Счётчик меняет
    // поведение только там, где области ВЛОЖЕНЫ, а вложенности до этой волны не существовало.
    private readonly Lock _hookLock = new();
    private int _hookDepth;

    // Был ли внутри нынешней побудки ввод. От этого зависит ПАУЗА НА СЛИВ при заморозке: она
    // существует ради того, что PostMessage положил в очередь цели, а захват кадра не кладёт туда
    // ничего. Так сегодня и устроено — CaptureFreshAsync/CaptureScreenshot замораживают без слива,
    // а входной путь со сливом, — и волна обязана повторить это дословно.
    private bool _inputSinceWake;

    // Цепочка переходов окна: побудка и заморозка идут строго по очереди. Без неё счётчик завёл бы
    // новую беду вместо старой — область, пришедшая ровно в паузу слива предыдущей, разбудила бы
    // окно, а заканчивающаяся заморозка тут же уложила бы его обратно, и держащий область писал бы
    // в замороженный клиент. Вторая половина того же: вошедший ВТОРЫМ обязан дождаться чужой паузы
    // устаканивания, иначе «первый вход будит» превращается в «второй пишет в ещё не проснувшееся
    // окно».
    private Task _transition = Task.CompletedTask;

    /// <summary>Боевой конструктор: окно открывается по дескриптору главного окна процесса.</summary>
    public GameWindow(
        ProcessInfo info,
        ProcessHookSettings? hook,
        KeyboardInputResolver keyboard,
        IMouseInput mouse,
        ISettingsSource settings,
        ILogger<GameWindow> logger)
        : this(Open(info), info.ProcessName, hook, keyboard, mouse, settings, logger)
    {
    }

    /// <summary>
    /// Конструктор поверх УЖЕ ОТКРЫТОГО окна.
    ///
    /// Заведён ради одного свойства, которое иначе нечем проверить: пара «побудка → заморозка
    /// обратно» наблюдаема только со стороны <see cref="INativeWindow"/>, а боевой конструктор
    /// окно открывает сам и подменить ему нижний слой невозможно. Незакрытая пара оставляет
    /// клиента PW размороженным до настоящей смены фокуса от ОС, а живой игры под рукой не бывает
    /// — значит, шов нужен.
    /// </summary>
    public GameWindow(
        INativeWindow nativeWindow,
        string processName,
        ProcessHookSettings? hook,
        KeyboardInputResolver keyboard,
        IMouseInput mouse,
        ISettingsSource settings,
        ILogger<GameWindow> logger)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _settings = settings;
        _processName = processName;
        _nativeWindow = nativeWindow;
        _hook = hook;
        _logger = logger;
    }

    // Проверка дескриптора стоит здесь, до делегирования: ProcessMonitor придерживает pid'ы с
    // hwnd=0 именно потому, что дальше этой строки с нулём проходить нельзя.
    private static INativeWindow Open(ProcessInfo info)
    {
        if (info.MainWindowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Process must have a non-zero MainWindowHandle.", nameof(info));
        }

        return Win32NativeWindowSystem.Open(info.MainWindowHandle);
    }

    // Пороги и темп опроса читаются В МОМЕНТ СОПОСТАВЛЕНИЯ, а не запоминаются в конструкторе:
    // окно живёт часами, а порог подбирают именно тогда, когда шаблон не попадает, — и «поправил
    // и перезапустил всё» вместо «поправил и попробовал ещё раз» это ровно тот опыт, ради
    // отсутствия которого хранилище настроек и заводилось. Чтение — одна ссылка на неизменяемый
    // снимок.
    private double MatchThreshold => _settings.Current.Vision.MatchThreshold;

    private TimeSpan PollInterval => TimeSpan.FromMilliseconds(_settings.Current.Vision.PollIntervalMs);

    public IntPtr Handle => _nativeWindow.Handle;

    public bool IsAlive => _nativeWindow.IsAlive;

    public (int Width, int Height) ClientSize
    {
        get
        {
            try
            {
                return _nativeWindow.GetClientSize();
            }
            catch
            {
                return (0, 0);
            }
        }
    }

    // ==== СКОБКА ПРОБУЖДЕНИЯ =================================================================
    //
    // СКОБКА — ЭТО ОБЛАСТЬ, А НЕ ПАРА ВЫЗОВОВ, и это главное, что здесь надо знать. На её
    // закрытии держатся два записанных в другом месте обоснования: затвор отладчика стоит между
    // нодами потому, что «к моменту возврата управления в MacroExecutor ни одно игровое окно не
    // остаётся разбуженным», и Orchestrator.StopAsync обещает, что «ни один прогон не бросают
    // посреди активации, оставив клиент разбуженным». Пока вторая половина была отдельным
    // вызовом, её выполнение держалось на внимательности вызывающего: между половинами бросают
    // двое — Task.Delay паузы устаканивания (отмена: «■ Стоп», выключение демона) и CapturePng
    // (нулевая клиентская область, отказ PrintWindow), — и ревью нашло четыре скобки без
    // try/finally. Веер даёт до десяти тиков зрения разом, то есть до десяти клиентов PW,
    // оставшихся рендерить в фоне; починить их некому — обход-то отменён, — и само это проходит
    // только от настоящей смены фокуса пользователем. Теперь скобку закрывает компилятор:
    // `await using var scope = await window.EnterHookAsync(...)`.
    //
    // ТОКЕН В ЗАМОРОЗКУ НЕ ПРОБРАСЫВАЕТСЯ, и это не упущение. Заморозка есть уборка за побудкой,
    // а отменять уборку по тому же токену, который её и вызвал, значит не делать её ровно в том
    // случае, ради которого она и нужна. Теперь правило поддержано устройством, а не памятью:
    // у закрытия области токена нет в сигнатуре (IWindowHookHolder.ExitHookAsync), передать его
    // туда нечем.

    /// <inheritdoc />
    public ValueTask<WindowHookScope> EnterHookAsync(HookOn on, CancellationToken cancellationToken = default)
    {
        if (!Applies(on))
        {
            return ValueTask.FromResult(default(WindowHookScope));
        }

        Task wake;
        lock (_hookLock)
        {
            if (on == HookOn.Input)
            {
                _inputSinceWake = true;
            }

            // Побудку заводит ПЕРВЫЙ, остальные ждут её же задачу. Именно поэтому WakeAsync
            // запускается прямо под замком: до первого настоящего await он успевает послать
            // WM_ACTIVATEAPP, так что «сигнал ушёл» и «счётчик поднят» неразделимы. Цена названа:
            // сигнал побудки уходит блокирующим SendMessage, то есть замок этого окна на время
            // отправки занят. Зависший клиент и так вешает свой обход намертво (у SendMessage нет
            // таймаута) — новое здесь только то, что вместе с ним встанут области ТОГО ЖЕ окна;
            // окна с другими дескрипторами держат каждое свой замок и не задеты.
            if (_hookDepth++ == 0)
            {
                _transition = WakeAsync(_transition, cancellationToken);
            }

            wake = _transition;
        }

        return Enter(this, wake);

        static async ValueTask<WindowHookScope> Enter(GameWindow window, Task wake)
        {
            try
            {
                await wake.ConfigureAwait(false);
            }
            catch
            {
                // Побудка не удалась — обычно это отмена в паузе устаканивания. Окно она
                // заморозила обратно сама (см. ActivateAsync), а нам остаётся не оставить
                // счётчик поднятым: области, которая его опустила бы, не будет.
                window.RollBackEntry();
                throw;
            }

            return new WindowHookScope(window);
        }
    }

    /// <inheritdoc />
    public WindowHookScope EnterHook(HookOn on)
    {
        if (!Applies(on))
        {
            return default;
        }

        Task wake;
        lock (_hookLock)
        {
            if (on == HookOn.Input)
            {
                _inputSinceWake = true;
            }

            if (_hookDepth++ == 0)
            {
                _transition = WakeAsync(_transition, CancellationToken.None);
            }

            wake = _transition;
        }

        try
        {
            // Блокирующее ожидание — то же, чем была Thread.Sleep на этом пути: API синхронный
            // потому, что его дёргают одноразовые пути UI («Дамп захватов», диалог метки), и им
            // незачем согласовываться с более широкой сессией ввода. Контекста синхронизации у
            // демона нет, так что взаимной блокировки здесь взяться неоткуда.
            wake.GetAwaiter().GetResult();
        }
        catch
        {
            RollBackEntry();
            throw;
        }

        return new WindowHookScope(this);
    }

    /// <inheritdoc />
    public bool WakesForWholeRun(HookOn on) => _hook is { Scope: HookLifetime.Run } hook && hook.Fires(on);

    /// <inheritdoc />
    ValueTask IWindowHookHolder.ExitHookAsync() => new(Leave());

    /// <inheritdoc />
    void IWindowHookHolder.ExitHook() => Leave().GetAwaiter().GetResult();

    // Есть ли вокруг этой операции скобка вообще. Отсутствие хука — рабочая семантика «обычный
    // процесс», пустой набор On — «хук есть, числа сохранены, не срабатывает нигде»; для окна это
    // одно и то же, и различает их только файл настроек.
    private bool Applies(HookOn on) => _hook is { } hook && hook.Fires(on);

    private void RollBackEntry()
    {
        lock (_hookLock)
        {
            if (--_hookDepth <= 0)
            {
                _hookDepth = 0;
                _inputSinceWake = false;
            }
        }
    }

    // Выход одной области. Последний выход и есть заморозка; лишний (двойной Dispose копии
    // структуры) не уводит счётчик в минус — по тем же соображениям, по каким этого не делает
    // HotkeyListener.ResumeAsync.
    private Task Leave()
    {
        lock (_hookLock)
        {
            if (_hookDepth == 0)
            {
                return Task.CompletedTask;
            }

            if (--_hookDepth > 0)
            {
                return Task.CompletedTask;
            }

            var drain = _inputSinceWake;
            _inputSinceWake = false;
            _transition = FreezeAsync(_transition, drain);
            return _transition;
        }
    }

    private async Task WakeAsync(Task previous, CancellationToken cancellationToken)
    {
        await Settled(previous).ConfigureAwait(false);
        await ActivateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FreezeAsync(Task previous, bool drain)
    {
        await Settled(previous).ConfigureAwait(false);
        if (drain)
        {
            await DeactivateAsync().ConfigureAwait(false);
        }
        else
        {
            // Захват ничего не клал в очередь цели, сливать нечего — замораживаем сразу. Ровно
            // так вели себя оба пути захвата до этой волны.
            Refreeze();
        }
    }

    // Дождаться предыдущего перехода, чем бы он ни кончился. Уже завершённый (обычный случай)
    // отдаётся как CompletedTask, чтобы продолжение пошло СИНХРОННО: иначе WM_ACTIVATEAPP уехал
    // бы из-под замка, и «сигнал ушёл раньше, чем второй вошедший увидел счётчик» перестало бы
    // быть правдой.
    private static Task Settled(Task previous) => previous.IsCompleted ? Task.CompletedTask : Swallow(previous);

    private static async Task Swallow(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // Чужой отказ — не наше дело: тот, кто его получил, уже откатил счётчик и (если
            // надо) заморозил окно. Нам важен только порядок переходов.
        }
    }

    // ---- половины скобки --------------------------------------------------------------------
    //
    // ⚠️ ВЫЗЫВАТЬ НАПРЯМУЮ НЕЛЬЗЯ, кроме одного места: GameWindowActivationTests пиннит ими
    // поведение скобки счётом и порядком обращений к Win32, и ради этого обе остались публичными
    // на КЛАССЕ, но ушли из IGameWindow. Счётчика они не трогают: считает область.

    // PW замораживает неактивные клиенты (встают и ввод, и отрисовка). Activate отправляет
    // будящий сигнал WM_ACTIVATEAPP, чтобы последующий ввод был обработан; пауза на
    // устаканивание даёт движку действительно вернуться в строй до того, как мы начнём слать
    // ввод. Процессы без хука сигнал не шлют вовсе.
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_hook is not { } hook)
        {
            return;
        }

        _nativeWindow.SendActivationSignal(hook.ActivationLParam);
        if (hook.SettleMs <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(hook.SettleMs, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Асимметрия намеренная: на успешном пути заморозка — дело области (она закроется и
            // всё уберёт), а на неуспешном закрывать нечего — области ещё нет. Бросок отсюда
            // уносит управление мимо всякого using, так что убрать за собой обязаны мы сами.
            Refreeze();
            throw;
        }
    }

    // Слив перед деактивацией: ввод через PostMessage попадает в очередь цели, но к моменту
    // возврата из PressKeyAsync ещё не обработан. Если деактивировать сразу, PW сначала
    // обработает деактивацию и (наблюдалось) выбросит накопленный отправленный ввод при уходе в
    // неактивное состояние. Пауза на слив даёт насосу сообщений время разобрать очередь и
    // обработать этот ввод. После слива шлём WM_ACTIVATEAPP(FALSE) — если только это не то
    // окно, с которым пользователь сейчас работает (передний план); тогда оставляем его
    // активным, чтобы не вырывать у него фокус.
    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (_hook is not { } hook)
        {
            // Окно с простым вводом — мы его и не активировали, так что нечего ни сливать, ни
            // укладывать обратно спать.
            return;
        }

        try
        {
            if (hook.DeactivateMs > 0)
            {
                await Task.Delay(hook.DeactivateMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Отменённый слив — это потерянный ввод, неприятность; отменённая заморозка — это
            // клиент, оставшийся рендерить в фоне до конца сеанса. Поэтому пауза отменяема, а
            // заморозка после неё — нет.
            Refreeze();
        }
    }

    // Заморозка обратно — ОДНО место на все три пути (ввод и оба захвата). Правило «окно, с
    // которым сейчас работает пользователь, не трогаем» иначе размножается копиями и расходится:
    // разбудить уже активное окно безвредно, а вот усыпить его — значит вырвать у пользователя
    // фокус посреди игры.
    private void Refreeze()
    {
        if (Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }
    }

    // Способ доставки спрашивается на КАЖДОЕ нажатие: настройка «ввод по умолчанию» обязана
    // применяться со следующего цикла активации, а не со следующего запуска демона. Резолвер
    // держит обе реализации полями, так что это выбор из двух ссылок, а не создание объекта.
    public Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default) =>
        _keyboard.For(_processName).SendKeyAsync(Handle, key, cancellationToken);

    public Task ClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _mouse.ClickAsync(Handle, point.X, point.Y, cancellationToken);

    public Task DoubleClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _mouse.DoubleClickAsync(Handle, point.X, point.Y, cancellationToken);

    // Самодостаточен — открывает область сам, потому что это синхронный API, который дёргают
    // одноразовые пути UI (диалог метки, «Дамп захватов»), и им незачем согласовываться с более
    // широкой сессией ввода.
    //
    // CapturePng бросает на нулевой клиентской области и на отказе PrintWindow, а «Дамп захватов»
    // проходит по всем клиентам разом — без закрытия скобки один свёрнутый клиент оставался бы
    // разбуженным. Закрывает её здесь `using`, а не finally.
    public byte[] CaptureScreenshot()
    {
        using var scope = EnterHook(HookOn.Capture);
        return _nativeWindow.CapturePng();
    }

    public bool SetIconFromFile(string imagePath) => _nativeWindow.SetIconFromFile(imagePath);

    // Одноразовый близнец WaitForElementAsync: один свежий захват и один проход сопоставления,
    // без опроса. FindElementNode ветвится по исходу немедленно, так что бюджет на опрос здесь
    // был бы просто скрытым ожиданием, о котором автор графа не просил.
    public async Task<ScreenPoint?> FindElementAsync(byte[] elementTemplate, ScreenRect position,
        double? matchThreshold = null, CancellationToken cancellationToken = default)
    {
        byte[] capture;
        try
        {
            capture = await CaptureFreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(ex);
            return null;
        }

        // Порог снимается ОДИН раз на попытку и передаётся дальше значением: прочитай мы его
        // отдельно для сравнения и отдельно для строки лога, правка настройки ровно между двумя
        // чтениями дала бы запись «оценка 0.62 < 0.70» под вердиктом «попадание». Порог НОДЫ
        // главнее настройки — та лишь умолчание для нод, где автор его не трогал.
        var threshold = matchThreshold ?? MatchThreshold;
        if (TryMatchOnce(capture, elementTemplate, position, threshold, out var score, out var center))
        {
            LogMatchHit(position, score, threshold);
            return center;
        }

        LogMatchMiss(position, score, threshold);
        return null;
    }

    // Сопоставление шаблона опросом. Цикл активно захватывает кадр, обрезает по position (или
    // берёт весь экран, если position пуст), переводит в полутона и источник, и шаблон,
    // запускает MatchTemplate (CCoeffNormed) и возвращает ЦЕНТР совпадения на первом же тике,
    // где максимальная оценка перевалила за MatchThreshold. По таймауту возвращает null.
    public async Task<ScreenPoint?> WaitForElementAsync(byte[] elementTemplate, ScreenRect position,
        TimeSpan waitDuration, double? matchThreshold = null, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + (long)waitDuration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] capture;
            try
            {
                // АКТИВНЫЙ захват на каждом тике — пассивный PrintWindow по замороженному
                // фоновому клиенту PW возвращает устаревшие или чёрные кадры и ломает опрос в
                // загрузочном сценарии, когда пользователь запускает несколько клиентов подряд
                // и каждый уступает передний план следующему. WM_ACTIVATEAPP ненадолго
                // размораживает PW, чтобы кадр был свежим; после этого мы замораживаем обратно
                // (если окно не на переднем плане), чтобы не трогать реальный фокус
                // пользователя. Накладные расходы ~25–50 мс на тик.
                capture = await CaptureFreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCaptureFailed(ex);
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var threshold = matchThreshold ?? MatchThreshold;
            if (TryMatchOnce(capture, elementTemplate, position, threshold, out var score, out var center))
            {
                LogMatchHit(position, score, threshold);
                return center;
            }

            LogMatchMiss(position, score, threshold);

            // Темп опроса тоже перечитывается на каждом тике: цикл ожидания живёт до минуты, и
            // правка, применяющаяся только к следующему запуску макроса, здесь мало чем лучше
            // перезапуска демона.
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // Асинхронный близнец CaptureScreenshot — будит окно через WM_ACTIVATEAPP, выжидает паузу
    // на устаканивание, захватывает кадр и замораживает обратно (если только это окно САМО не
    // на переднем плане — тогда оно и так остаётся активным). Используется одноразовым
    // сопоставлением и циклом опроса.
    //
    // Самый дорогой из путей: тик зрения, и тиков этих в веере до десяти разом. Отмена приходит
    // прямо в паузу устаканивания («■ Стоп» или выключение демона), а CapturePng бросает сам по
    // себе, — без закрытия скобки оба случая оставляли бы клиента размороженным, причём чинить
    // его было бы уже некому: обход отменён.
    private async Task<byte[]> CaptureFreshAsync(CancellationToken cancellationToken)
    {
        await using var scope = await EnterHookAsync(HookOn.Capture, cancellationToken).ConfigureAwait(false);
        return _nativeWindow.CapturePng();
    }

    // Один проход сопоставления. `center` — центр лучшего совпадения в КЛИЕНТСКИХ координатах:
    // начало области обрезки прибавляется обратно, чтобы вызывающий получил точку, которую можно
    // сразу отдать в ClickAsync. Ради этого FoundPointVar и существует («найди кнопку где
    // угодно, а потом кликни по ней»).
    private bool TryMatchOnce(byte[] sourcePng, byte[] templatePng, ScreenRect position, double threshold,
        out double score, out ScreenPoint center)
    {
        score = 0.0;
        center = default;
        using var sourceFull = Cv2.ImDecode(sourcePng, ImreadModes.Color);
        if (sourceFull.Empty()) return false;

        // Пустая область = ищем по всему кадру.
        var rect = position.Width <= 0 || position.Height <= 0
            ? new Rect(0, 0, sourceFull.Width, sourceFull.Height)
            : ClampToImage(new Rect(position.X, position.Y, position.Width, position.Height), sourceFull.Size());

        using var crop = new Mat(sourceFull, rect);
        using var sourceGray = ToGrayscale(crop);

        using var templateBgr = Cv2.ImDecode(templatePng, ImreadModes.Color);
        if (templateBgr.Empty()) return false;
        using var templateGray = ToGrayscale(templateBgr);

        if (templateGray.Width > sourceGray.Width || templateGray.Height > sourceGray.Height)
        {
            LogTemplateLargerThanRegion(templateGray.Width, templateGray.Height, sourceGray.Width, sourceGray.Height);
            return false;
        }

        // Полутона + CCoeffNormed вместо бинаризации + CCoeffNormed: многие элементы игрового
        // интерфейса (иконки панели чата и прочее) лежат на полупрозрачных затемнённых
        // подложках, где просвечивающий снизу мир делает бинаризацию нестабильной. CCoeff
        // вычитает среднее и нормирует по среднеквадратичному отклонению, так что сдвиги
        // яркости взаимно гасятся. ClassMatcher — единственное место, которое бинаризует до сих
        // пор: его предмет (текст класса на непрозрачной панели характеристик) чисто отделяется
        // по фиксированному порогу яркости.
        using var result = new Mat();
        Cv2.MatchTemplate(sourceGray, templateGray, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);
        score = maxVal;
        // maxLoc — это левый верхний угол шаблона внутри ОБРЕЗКИ; сдвигаем на начало обрезки,
        // чтобы получить клиентские координаты, а потом на половину шаблона, чтобы попасть в
        // центр.
        center = new ScreenPoint(
            rect.X + maxLoc.X + (templateGray.Width / 2),
            rect.Y + maxLoc.Y + (templateGray.Height / 2));
        return score >= threshold;
    }

    private static Mat ToGrayscale(Mat bgr)
    {
        var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Rect ClampToImage(Rect rect, Size imageSize)
    {
        var x = Math.Max(0, Math.Min(rect.X, imageSize.Width - 1));
        var y = Math.Max(0, Math.Min(rect.Y, imageSize.Height - 1));
        var w = Math.Min(rect.Width, imageSize.Width - x);
        var h = Math.Min(rect.Height, imageSize.Height - y);
        return new Rect(x, y, w, h);
    }

    [LoggerMessage(LogLevel.Warning,
        "Сопоставление: шаблон ({TplW}x{TplH}) больше области поиска ({SrcW}x{SrcH}) — уменьшите шаблон или расширьте область")]
    partial void LogTemplateLargerThanRegion(int tplW, int tplH, int srcW, int srcH);

    [LoggerMessage(LogLevel.Debug, "Сопоставление: попадание в {Region} оценка={Score:F3} >= {Threshold:F3}")]
    partial void LogMatchHit(ScreenRect region, double score, double threshold);

    [LoggerMessage(LogLevel.Debug, "Сопоставление: промах в {Region} оценка={Score:F3} < {Threshold:F3}")]
    partial void LogMatchMiss(ScreenRect region, double score, double threshold);

    [LoggerMessage(LogLevel.Debug, "Сопоставление: захват не удался (обычно временно — окно свёрнуто, затык GPU)")]
    partial void LogCaptureFailed(Exception ex);
}
