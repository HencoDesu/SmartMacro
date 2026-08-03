using Microsoft.Extensions.Logging;

namespace SmartMacro.Macros.Execution;

/// <summary>Неизменяемый вид одного отслеживаемого прогона в том виде, в каком его отдаёт <see cref="MacroRunRegistry.Snapshot"/>.</summary>
/// <param name="RunId">Уникальный id прогона.</param>
/// <param name="MacroName">Имя прогоняемого макроса.</param>
/// <param name="StartedUtc">Когда прогон начался.</param>
/// <param name="CurrentNodeName">Подпись ноды, в которую walker вошёл последней; <c>null</c> до первой ноды.</param>
public sealed record MacroRunSnapshot(Guid RunId, string MacroName, DateTime StartedUtc, string? CurrentNodeName);

/// <summary>
/// Живой дескриптор одного прогона, который возвращает <see cref="MacroRunRegistry.TryBegin"/>.
/// Бегун исполняет прогон с <see cref="Token"/> и докладывает о прогрессе через
/// <see cref="CurrentNodeName"/> (подведите его к <see cref="MacroRunContext.OnNodeEntered"/>); а
/// когда прогон заканчивается — чем бы он ни закончился — вызовите
/// <see cref="MacroRunRegistry.Complete"/>.
/// </summary>
public sealed class MacroRunHandle
{
    private string? _currentNodeName;

    internal MacroRunHandle(Guid runId, string macroName, DateTime startedUtc, CancellationToken token)
    {
        RunId = runId;
        MacroName = macroName;
        StartedUtc = startedUtc;
        Token = token;
    }

    /// <summary>Уникальный id прогона.</summary>
    public Guid RunId { get; }

    /// <summary>Имя прогоняемого макроса.</summary>
    public string MacroName { get; }

    /// <summary>Когда прогон начался.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>Отменяется через <see cref="MacroRunRegistry.StopAsync"/> / <see cref="MacroRunRegistry.StopAllAsync"/>.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Подпись ноды, в которую walker вошёл последней. Volatile — бегун обновляет её на ходу, а
    /// читают её снимки для UI. Подпись, а не id: единственный её потребитель — колонка «где
    /// сейчас» в режиме «Прогоны», и адресовать эту ноду ей незачем.
    /// </summary>
    public string? CurrentNodeName
    {
        get => Volatile.Read(ref _currentNodeName);
        set => Volatile.Write(ref _currentNodeName, value);
    }
}

/// <summary>
/// Ведёт учёт выполняющихся макросов: single-flight по имени макроса (повторный запуск уже
/// идущего макроса ничего не делает, только пишет в лог), «Стоп» из UI, отмена всего при
/// выключении и снимок для показа. Исполнитель об этом классе ничего не знает — вызывающие сами
/// обрамляют <see cref="MacroExecutor.RunAsync"/> парой <see cref="TryBegin"/> /
/// <see cref="Complete"/>.
/// </summary>
public sealed partial class MacroRunRegistry : IDisposable
{
    private sealed class ActiveRun
    {
        public required MacroRunHandle Handle { get; init; }

        /// <summary>Значение, по которому <see cref="TryBegin"/> схлопывает повторы; по умолчанию — имя макроса.</summary>
        public required string SingleFlightKey { get; init; }

        public required CancellationTokenSource Cts { get; init; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, ActiveRun> _runs = [];
    private readonly ILogger<MacroRunRegistry> _logger;
    private bool _disposed;

    public MacroRunRegistry(ILogger<MacroRunRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>Поднимается (снаружи блокировки) после добавления или удаления прогона.</summary>
    public event Action? RunsChanged;

    /// <summary>
    /// Регистрирует новый прогон макроса <paramref name="macroName"/>. Single-flight:
    /// возвращает <c>null</c> (с записью в лог), если прогон с таким же ключом уже отслеживается.
    /// </summary>
    /// <param name="macroName">Прогоняемый макрос. Идёт на показ и служит ключом single-flight по умолчанию.</param>
    /// <param name="singleFlightKey">
    /// Переопределяет то, по чему схлопываются одновременные прогоны. Значение по умолчанию
    /// (имя макроса) правильно для хоткеев — долбить по одному бесполезно. Прогоны на окно
    /// (загрузочный макрос от появления процесса) передают ключ, включающий окно, чтобы девять
    /// одновременно запущенных клиентов получили каждый свой прогон, а не восемь отказов.
    /// </param>
    public MacroRunHandle? TryBegin(string macroName, string? singleFlightKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        var key = string.IsNullOrWhiteSpace(singleFlightKey) ? macroName : singleFlightKey;

        MacroRunHandle handle;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runs.Values.Any(run => string.Equals(run.SingleFlightKey, key, StringComparison.Ordinal)))
            {
                LogAlreadyRunning(macroName);
                return null;
            }

            var cts = new CancellationTokenSource();
            handle = new MacroRunHandle(Guid.NewGuid(), macroName, DateTime.UtcNow, cts.Token);
            _runs.Add(handle.RunId, new ActiveRun { Handle = handle, SingleFlightKey = key, Cts = cts });
        }

        LogRunStarted(macroName, handle.RunId);
        RunsChanged?.Invoke();
        return handle;
    }

    /// <summary>
    /// Убирает завершившийся прогон и разблокирует всех, кто ждёт по нему
    /// <see cref="StopAsync"/>. Бегун обязан вызвать это ровно один раз на каждый
    /// <see cref="TryBegin"/>, в <c>finally</c>. На неизвестный id возвращает <c>false</c>
    /// (события не будет).
    /// </summary>
    public bool Complete(Guid runId)
    {
        ActiveRun? run;
        lock (_lock)
        {
            if (!_runs.Remove(runId, out run))
            {
                return false;
            }
        }

        run.Completed.TrySetResult();
        run.Cts.Dispose();
        LogRunCompleted(run.Handle.MacroName, runId);
        RunsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Отменяет прогон и возвращает задачу, которая завершится, когда бегун подтвердит это
    /// вызовом <see cref="Complete"/>. Неизвестные (уже завершившиеся) id завершаются мгновенно.
    /// </summary>
    public Task StopAsync(Guid runId)
    {
        ActiveRun? run;
        lock (_lock)
        {
            if (!_runs.TryGetValue(runId, out run))
            {
                return Task.CompletedTask;
            }
        }

        LogStopRequested(run.Handle.MacroName, runId);
        Cancel(run);
        return run.Completed.Task;
    }

    /// <summary>Отменяет все отслеживаемые прогоны и ждёт, пока каждый из них дойдёт до <see cref="Complete"/> (путь выключения).</summary>
    public Task StopAllAsync()
    {
        List<ActiveRun> runs;
        lock (_lock)
        {
            runs = [.. _runs.Values];
        }

        foreach (var run in runs)
        {
            Cancel(run);
        }

        return Task.WhenAll(runs.Select(run => run.Completed.Task));
    }

    /// <summary>Атомарный снимок всех отслеживаемых прогонов, изолированный от последующих изменений.</summary>
    public IReadOnlyList<MacroRunSnapshot> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<MacroRunSnapshot>(_runs.Count);
            foreach (var run in _runs.Values)
            {
                result.Add(new MacroRunSnapshot(
                    run.Handle.RunId,
                    run.Handle.MacroName,
                    run.Handle.StartedUtc,
                    run.Handle.CurrentNodeName));
            }

            return result;
        }
    }

    /// <summary>Отменяет все отслеживаемые прогоны, не дожидаясь их. При штатном выключении лучше <see cref="StopAllAsync"/>.</summary>
    public void Dispose()
    {
        List<ActiveRun> runs;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runs = [.. _runs.Values];
        }

        foreach (var run in runs)
        {
            Cancel(run);
        }
    }

    private static void Cancel(ActiveRun run)
    {
        try
        {
            run.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Complete() обогнал нас и уже освободил CTS — прогон закончился, вопрос закрыт.
        }
    }

    [LoggerMessage(LogLevel.Information,
        "Макрос '{MacroName}' уже выполняется — триггер проигнорирован (single-flight)")]
    partial void LogAlreadyRunning(string macroName);

    [LoggerMessage(LogLevel.Information, "Прогон макроса начат: '{MacroName}' ({RunId})")]
    partial void LogRunStarted(string macroName, Guid runId);

    [LoggerMessage(LogLevel.Information, "Прогон макроса закончен: '{MacroName}' ({RunId})")]
    partial void LogRunCompleted(string macroName, Guid runId);

    [LoggerMessage(LogLevel.Information, "Запрошена остановка прогона '{MacroName}' ({RunId})")]
    partial void LogStopRequested(string macroName, Guid runId);
}
