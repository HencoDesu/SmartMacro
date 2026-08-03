using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Ipc;
using SmartMacro.GameWindows;
using SmartMacro.Windows;

namespace SmartMacro.Vision;

/// <summary>
/// Диагностика «Дамп захватов»: для каждого известного реестру окна записать в <c>debug/</c>
/// рядом с исполняемым файлом то, что конвейер зрения на самом деле видит.
///
///   <c>{process}-{tags}-full.png</c>      — сырой захват PrintWindow
///   <c>{process}-{tags}-class-bin.png</c> — <see cref="IClassMatcher.DebugBinarizeClassRegion"/>,
///                                            то есть та маска, которую отдают в MatchTemplate
///
/// По двум файлам сразу видно, промахнулась ли область или бинаризация вышла нечитаемой, — а
/// это два режима отказа, которые со стороны «макрос не нашёл элемент» выглядят одинаково.
/// Перед дампом откройте игровое окно характеристик (по умолчанию <c>C</c>), иначе область
/// класса будет пуста по построению.
///
/// До стадии 2B это жило в <c>MainWindow.axaml.cs</c>. Переехало сюда, потому что захваты
/// обязан делать тот процесс, которому окна и принадлежат: после разделения у UI нет ни
/// <see cref="WindowRegistry"/>, ни OpenCV, ни дескрипторов <see cref="IGameWindow"/> — он
/// отправляет <c>DumpCaptures</c>, получает обратно путь к папке и открывает на ней проводник.
///
/// Ничто здесь не бросает из-за одного плохого окна: неудавшийся захват пишет рядом
/// <c>.error.txt</c>, и обход продолжается, потому что обычная причина снимать дамп — что-то
/// уже сломано.
/// </summary>
public sealed partial class CaptureDumpService
{
    /// <summary>Имя папки дампа относительно каталога приложения.</summary>
    public const string FolderName = "debug";

    private readonly WindowRegistry _windows;
    private readonly IClassMatcher _matcher;
    private readonly ILogger<CaptureDumpService> _logger;

    /// <summary>
    /// Боевой конструктор: <c>debug/</c> в КОРНЕ УСТАНОВКИ, рядом с <c>macros/</c> и
    /// <c>templates/</c>, а не в подпапке демона. Дампы смотрит человек, и лежать они обязаны
    /// там, куда он и так ходит; см. <see cref="InstallationLayout"/>.
    /// </summary>
    public CaptureDumpService(WindowRegistry windows, IClassMatcher matcher, ILogger<CaptureDumpService> logger)
        : this(InstallationLayout.RootFromDaemonDirectory(AppContext.BaseDirectory), windows, matcher, logger)
    {
    }

    /// <param name="baseDirectory">Папка, внутри которой окажется <c>debug/</c>. Тесты направляют её во временный каталог.</param>
    /// <param name="windows">Источник окон для захвата и поиска «hwnd → фасад».</param>
    /// <param name="matcher">Поставляет бинаризованный вид области класса.</param>
    /// <param name="logger">Приёмник диагностики.</param>
    public CaptureDumpService(
        string baseDirectory,
        WindowRegistry windows,
        IClassMatcher matcher,
        ILogger<CaptureDumpService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _windows = windows;
        _matcher = matcher;
        _logger = logger;
        FolderPath = Path.Combine(baseDirectory, FolderName);
    }

    /// <summary>Абсолютный путь к папке дампа. Существует только после первого <see cref="DumpAsync"/>.</summary>
    public string FolderPath { get; }

    /// <summary>
    /// Захватывает каждое зарегистрированное окно и пишет по два PNG на окно.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>
    /// <see cref="FolderPath"/> — вызывающему (UI, по IPC) нужно только куда-то направить
    /// проводник. Реестр без окон всё равно даёт созданную пустую папку, а не ошибку: «ничего
    /// не захвачено» само по себе и есть диагностика.
    /// </returns>
    public async Task<string> DumpAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(FolderPath);

        var captured = 0;
        var failed = 0;
        foreach (var info in _windows.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Реестр — единственный способ найти по hwnd управляемое окно; у записи,
            // зарегистрированной без фасада (или у окна, умершего после снятия снимка), просто
            // нечего захватывать.
            if (_windows.TryGetWindow(info.Hwnd) is not { } window)
            {
                continue;
            }

            var stem = StemFor(info);

            byte[] fullCapture;
            try
            {
                fullCapture = window.CaptureScreenshot();
                await File.WriteAllBytesAsync(Path.Combine(FolderPath, $"{stem}-full.png"), fullCapture,
                        cancellationToken)
                    .ConfigureAwait(false);
                captured++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCaptureFailed(ex, info.Hwnd.ToInt64());
                await WriteErrorAsync($"{stem}.error.txt", ex, cancellationToken).ConfigureAwait(false);
                failed++;
                continue;
            }

            try
            {
                var binarised = _matcher.DebugBinarizeClassRegion(fullCapture);
                await File.WriteAllBytesAsync(Path.Combine(FolderPath, $"{stem}-class-bin.png"), binarised,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBinarizeFailed(ex, info.Hwnd.ToInt64());
                await WriteErrorAsync($"{stem}-class-bin.error.txt", ex, cancellationToken).ConfigureAwait(false);
                failed++;
            }
        }

        LogDumpFinished(captured, failed, FolderPath);
        return FolderPath;
    }

    // «{process}-{тег+тег}» либо дескриптор, если тегов у окна пока нет, — а это как раз и есть
    // интересный случай (дамп обычно снимают потому, что опознание не сработало).
    private static string StemFor(ManagedWindowInfo info)
    {
        var label = info.Tags.Count > 0
            ? string.Join('+', info.Tags)
            : $"0x{info.Hwnd.ToInt64():X}";
        return SafeFileName($"{info.ProcessName}-{label}");
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private async Task WriteErrorAsync(string fileName, Exception ex, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(FolderPath, fileName), ex.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
        {
            // В саму папку дампа писать нельзя. Настоящий сбой уже записан в лог выше, и
            // потеря его .txt-копии не стоит того, чтобы обрывать обход.
        }
    }

    [LoggerMessage(LogLevel.Warning, "Дамп захватов: PrintWindow не удался для hwnd=0x{Hwnd:X}")]
    partial void LogCaptureFailed(Exception ex, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Дамп захватов: бинаризация области класса не удалась для hwnd=0x{Hwnd:X}")]
    partial void LogBinarizeFailed(Exception ex, long hwnd);

    [LoggerMessage(LogLevel.Information,
        "Дамп захватов закончен: снято окон {Captured}, сбоев {Failed} → '{Folder}'")]
    partial void LogDumpFinished(int captured, int failed, string folder);
}
