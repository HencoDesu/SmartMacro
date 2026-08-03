using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using SmartMacro.Config;
using SmartMacro.Native;
using SmartMacro.Native.Window;
using SmartMacro.ProcessMonitoring;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SmartMacro.GameWindows;

[SupportedOSPlatform("windows")]
public sealed partial class GameWindow : IGameWindow
{
    private readonly IKeyboardInput _keyboard;
    private readonly IMouseInput _mouse;

    private readonly INativeWindow _nativeWindow;

    // Null = процесс с простым вводом (профиля нет либо ActivationLParam не настроен): вся
    // пляска «побудка через WM_ACTIVATEAPP — деактивация» пропускается.
    private readonly uint? _activationLParam;
    private readonly int _settleDelayMs;
    private readonly int _deactivationDelayMs;
    private readonly double _matchThreshold;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<GameWindow> _logger;

    public GameWindow(
        ProcessInfo info,
        ProcessProfile profile,
        IKeyboardInput keyboard,
        IMouseInput mouse,
        IOptions<WindowVisionOptions> visionOptions,
        ILogger<GameWindow> logger)
    {
        if (info.MainWindowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Process must have a non-zero MainWindowHandle.", nameof(info));
        }

        _keyboard = keyboard;
        _mouse = mouse;
        _nativeWindow = Win32NativeWindowSystem.Open(info.MainWindowHandle);
        _activationLParam = profile.ActivationLParam;
        _settleDelayMs = profile.SettleDelayMs;
        _deactivationDelayMs = profile.DeactivationDelayMs;
        _matchThreshold = visionOptions.Value.MatchThreshold;
        _pollInterval = TimeSpan.FromMilliseconds(visionOptions.Value.PollIntervalMs);
        _logger = logger;
    }

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

    // PW замораживает неактивные клиенты (встают и ввод, и отрисовка). Activate отправляет
    // будящий сигнал WM_ACTIVATEAPP, чтобы последующий ввод был обработан; пауза на
    // устаканивание даёт движку действительно вернуться в строй до того, как мы начнём слать
    // ввод. Процессы с простым вводом (без ActivationLParam в профиле) сигнал не шлют вовсе.
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activationLParam is { } lParam)
        {
            _nativeWindow.SendActivationSignal(lParam);
        }

        if (_settleDelayMs > 0)
        {
            await Task.Delay(_settleDelayMs, cancellationToken).ConfigureAwait(false);
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
        if (_activationLParam is null)
        {
            // Окно с простым вводом — мы его и не активировали, так что нечего ни сливать, ни
            // укладывать обратно спать.
            return;
        }

        if (_deactivationDelayMs > 0)
        {
            await Task.Delay(_deactivationDelayMs, cancellationToken).ConfigureAwait(false);
        }

        if (Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }
    }

    public Task PressKeyAsync(VirtualKey key, CancellationToken cancellationToken = default) =>
        _keyboard.SendKeyAsync(Handle, key, cancellationToken);

    public Task ClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _mouse.ClickAsync(Handle, point.X, point.Y, cancellationToken);

    public Task DoubleClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _mouse.DoubleClickAsync(Handle, point.X, point.Y, cancellationToken);

    // Самодостаточен — распоряжается активацией и деактивацией сам, потому что это синхронный
    // API, который дёргают одноразовые пути UI (диалог метки, «Дамп захватов»), и им незачем
    // согласовываться с более широкой сессией ввода. Thread.Sleep вместо Task.Delay сохраняет
    // API синхронным; пауза на устаканивание достаточно короткая (по умолчанию 20 мс), чтобы
    // краткая блокировка вызывающего потока была незаметной.
    public byte[] CaptureScreenshot()
    {
        if (_activationLParam is { } lParam)
        {
            _nativeWindow.SendActivationSignal(lParam);
            Thread.Sleep(_settleDelayMs);
        }

        var png = _nativeWindow.CapturePng();
        if (_activationLParam is not null && Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }

        return png;
    }

    public bool SetIconFromFile(string imagePath) => _nativeWindow.SetIconFromFile(imagePath);

    // Одноразовый близнец WaitForElementAsync: один свежий захват и один проход сопоставления,
    // без опроса. FindElementNode ветвится по исходу немедленно, так что бюджет на опрос здесь
    // был бы просто скрытым ожиданием, о котором автор графа не просил.
    public async Task<ScreenPoint?> FindElementAsync(byte[] elementTemplate, ScreenRect position,
        CancellationToken cancellationToken = default)
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

        if (TryMatchOnce(capture, elementTemplate, position, out var score, out var center))
        {
            LogMatchHit(position, score, _matchThreshold);
            return center;
        }

        LogMatchMiss(position, score, _matchThreshold);
        return null;
    }

    // Сопоставление шаблона опросом. Цикл активно захватывает кадр, обрезает по position (или
    // берёт весь экран, если position пуст), переводит в полутона и источник, и шаблон,
    // запускает MatchTemplate (CCoeffNormed) и возвращает ЦЕНТР совпадения на первом же тике,
    // где максимальная оценка перевалила за MatchThreshold. По таймауту возвращает null.
    public async Task<ScreenPoint?> WaitForElementAsync(byte[] elementTemplate, ScreenRect position,
        TimeSpan waitDuration, CancellationToken cancellationToken = default)
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
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (TryMatchOnce(capture, elementTemplate, position, out var score, out var center))
            {
                LogMatchHit(position, score, _matchThreshold);
                return center;
            }

            LogMatchMiss(position, score, _matchThreshold);

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // Асинхронный близнец CaptureScreenshot — будит окно через WM_ACTIVATEAPP, выжидает паузу
    // на устаканивание, захватывает кадр и замораживает обратно (если только это окно САМО не
    // на переднем плане — тогда оно и так остаётся активным). Используется одноразовым
    // сопоставлением и циклом опроса.
    private async Task<byte[]> CaptureFreshAsync(CancellationToken cancellationToken)
    {
        if (_activationLParam is { } lParam)
        {
            _nativeWindow.SendActivationSignal(lParam);
            if (_settleDelayMs > 0)
            {
                await Task.Delay(_settleDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        var png = _nativeWindow.CapturePng();
        if (_activationLParam is not null && Win32NativeWindowSystem.GetForeground().Handle != Handle)
        {
            _nativeWindow.SendDeactivationSignal();
        }

        return png;
    }

    // Один проход сопоставления. `center` — центр лучшего совпадения в КЛИЕНТСКИХ координатах:
    // начало области обрезки прибавляется обратно, чтобы вызывающий получил точку, которую можно
    // сразу отдать в ClickAsync. Ради этого FoundPointVar и существует («найди кнопку где
    // угодно, а потом кликни по ней»).
    private bool TryMatchOnce(byte[] sourcePng, byte[] templatePng, ScreenRect position, out double score,
        out ScreenPoint center)
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
        return score >= _matchThreshold;
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
