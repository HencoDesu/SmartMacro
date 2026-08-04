using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;
using SmartMacro.Input;
using SmartMacro.Native;
using SmartMacro.Presentation;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Настоящая реализация <see cref="IMacroPrimitives"/>: превращает адресованные по hwnd
/// операции walker'а во ввод, зрение и косметику окна.
///
/// Любая операция начинается с разрешения hwnd через <see cref="WindowRegistry"/> — реестр
/// владеет и состоянием тегов, по которому сверяются селекторы, и фасадом
/// <see cref="IGameWindow"/>, которым окном действительно можно управлять. Hwnd без
/// зарегистрированного фасада (окно умерло посреди прогона либо было зарегистрировано только
/// ради тегов) даёт запись в лог и ничегонеделание, а не исключение: разветвления сплошь и
/// рядом бегут наперегонки со сносом окон, и одно мёртвое окно не должно обрывать прогон,
/// который законно нацелился ещё на восемь.
///
/// Жизненный цикл активации живёт в <see cref="AgentInputDispatcher"/>, так что каждая клавиша
/// и каждый клик — это собственный цикл «разбудить → отправить → дать слиться → усыпить».
/// Понодовая зернистость означает, что нода паузы между двумя нодами клавиш действительно даёт
/// окну на это время уснуть обратно; это совпадает с тем, как граф читается, и с тем, что делал
/// прежний путь «на сообщение».
///
/// Шаблонов этот класс больше не разрешает: с волны F2 они приезжают байтами из бандла прогона
/// (<see cref="IMacroTemplateSource"/>), и провайдер общего дерева, который здесь когда-то лежал
/// полем, удалён вместе с самим деревом.
/// </summary>
public sealed partial class MacroPrimitives : IMacroPrimitives
{
    private readonly WindowRegistry _windows;
    private readonly AgentInputDispatcher _input;
    private readonly IClassMatcher _matcher;
    private readonly WindowIconService _icons;
    private readonly ILogger<MacroPrimitives> _logger;

    public MacroPrimitives(
        WindowRegistry windows,
        AgentInputDispatcher input,
        IClassMatcher matcher,
        WindowIconService icons,
        ILogger<MacroPrimitives> logger)
    {
        _windows = windows;
        _input = input;
        _matcher = matcher;
        _icons = icons;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PressKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(PressKeyAsync)) is not { } window)
        {
            return Task.CompletedTask;
        }

        return _input.FireKeyAsync(window, key, Describe(hwnd));
    }

    /// <inheritdoc />
    public Task ClickAsync(IntPtr hwnd, ScreenPoint point, bool doubleClick, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(ClickAsync)) is not { } window)
        {
            return Task.CompletedTask;
        }

        return _input.FireClickAsync(window, point, doubleClick, Describe(hwnd));
    }

    /// <inheritdoc />
    public async Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, byte[] template, ScreenRect? region,
        double? matchThreshold, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Resolve(hwnd, nameof(FindElementAsync)) is not { } window)
        {
            return null;
        }

        return await window.FindElementAsync(template, region ?? default, matchThreshold, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, byte[] template, ScreenRect? region,
        int timeoutMs, double? matchThreshold, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Resolve(hwnd, nameof(WaitForElementAsync)) is not { } window)
        {
            return null;
        }

        var budget = TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs));
        return await window.WaitForElementAsync(template, region ?? default, budget, matchThreshold, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string?> RecognizeAsync(IntPtr hwnd, IReadOnlyDictionary<string, byte[]> templates, ScreenRect region,
        double? matchThreshold, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (Resolve(hwnd, nameof(RecognizeAsync)) is not { } window)
        {
            return Task.FromResult<string?>(null);
        }

        if (templates.Count == 0)
        {
            // Пустой набор сюда доходит только тогда, когда обходчик уже сказал в журнал, какого
            // имени не хватило: собственного имени набора у нас здесь больше нет.
            return Task.FromResult<string?>(null);
        }

        ct.ThrowIfCancellationRequested();

        // АКТИВНЫЙ захват. К моменту выполнения этой ноды нажатие клавиши, открывшее игровую
        // панель, уже завершило собственный цикл активации и деактивации, так что пассивный
        // PrintWindow, скорее всего, вернул бы замороженный кадр, снятый ещё до панели.
        byte[] screenshot;
        try
        {
            screenshot = window.CaptureScreenshot();
        }
        catch (Exception ex)
        {
            LogCaptureFailed(ex, hwnd.ToInt64());
            return Task.FromResult<string?>(null);
        }

        try
        {
            var match = _matcher.Match(screenshot, templates, region, matchThreshold);
            if (match is null)
            {
                LogRecognizeNoMatch(templates.Count, hwnd.ToInt64());
                return Task.FromResult<string?>(null);
            }

            LogRecognized(match.Tag, match.Score);
            return Task.FromResult<string?>(match.Tag);
        }
        catch (Exception ex)
        {
            LogRecognizeFailed(ex, templates.Count);
            return Task.FromResult<string?>(null);
        }
    }

    /// <inheritdoc />
    public Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct)
    {
        if (Resolve(hwnd, nameof(SetIconAsync)) is { } window)
        {
            _icons.TryApply(window, iconPath);
        }

        return Task.CompletedTask;
    }

    private IGameWindow? Resolve(IntPtr hwnd, string operation)
    {
        var window = _windows.TryGetWindow(hwnd);
        if (window is null)
        {
            LogUnknownWindow(operation, hwnd.ToInt64());
        }

        return window;
    }

    // Метка для лога у операций ввода: когда работают девять клиентов, теги окна читаются в
    // логе куда лучше голого дескриптора.
    private string Describe(IntPtr hwnd)
    {
        var tags = _windows.GetTags(hwnd);
        return tags.Count > 0 ? string.Join("/", tags) : $"hwnd=0x{hwnd.ToInt64():X}";
    }

    [LoggerMessage(LogLevel.Warning, "{Operation}: для hwnd=0x{Hwnd:X} в реестре нет управляемого окна — пропускаем")]
    partial void LogUnknownWindow(string operation, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Не удался захват hwnd=0x{Hwnd:X} при распознавании")]
    partial void LogCaptureFailed(Exception ex, long hwnd);

    [LoggerMessage(LogLevel.Information, "Распознан '{Tag}' (оценка {Score:F3})")]
    partial void LogRecognized(string tag, double score);

    [LoggerMessage(LogLevel.Information, "Ни один из {Count} шаблонов не совпал на hwnd=0x{Hwnd:X}")]
    partial void LogRecognizeNoMatch(int count, long hwnd);

    [LoggerMessage(LogLevel.Error, "Распознавание по набору из {Count} шаблонов бросило исключение")]
    partial void LogRecognizeFailed(Exception ex, int count);
}
