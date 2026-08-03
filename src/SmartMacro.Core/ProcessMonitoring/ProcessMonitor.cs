using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMacro.Config;

namespace SmartMacro.ProcessMonitoring;

// Опрашивает Process.GetProcessesByName с настраиваемым интервалом и поднимает события
// ProcessAppeared / ProcessDisappeared на каждое расхождение с предыдущим снимком. Следит за
// ОБЪЕДИНЕНИЕМ имён процессов из всех ProcessProfiles — по одному перечислению на имя профиля за
// тик. Задуман дешёвым и терпимым к падениям процессов (исчезнувший процесс — норма, а не
// ошибка).
//
// Монитор — пассивный наблюдатель, он не знает, кто слушает его события. Оркестратор
// подписывается при создании. Другие потребители (UI, диагностика) тоже могут подписаться, и
// монитору до этого нет дела.
public sealed partial class ProcessMonitor : IHostedService, IDisposable
{
    private readonly IReadOnlyList<string> _processNames;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<ProcessMonitor> _logger;

    // Pid'ы, по которым мы уже подняли ProcessAppeared (то есть к моменту, когда мы до них
    // добрались, их MainWindowHandle был ненулевым).
    private readonly HashSet<int> _knownPids = [];

    // Pid'ы, которые мы видели с hwnd=0 и о которых уже написали в лог. Лаунчер PW создаёт
    // процесс за несколько секунд до того, как инициализируется его главное окно; без этого
    // двухступенчатого учёта мы подняли бы ProcessAppeared с hwnd=0 (что роняет конструктор
    // GameWindow) и больше никогда бы не попробовали. Такие pid'ы мы продолжаем опрашивать
    // каждый тик, пока hwnd != 0; запись «ждём» пишется по разу на pid, чтобы оператор видел
    // ожидание, но лог не засорялся.
    private readonly HashSet<int> _pidsLoggedWaiting = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Поднимается, когда обнаружен новый отслеживаемый процесс с годным дескриптором главного
    /// окна. Событие «позднее»: PW порождает процесс раньше, чем инициализирует главное окно, и
    /// монитор продолжает опрашивать, пока <see cref="ProcessInfo.MainWindowHandle"/> не станет
    /// ненулевым, и только тогда стреляет.
    /// </summary>
    public event Action<ProcessInfo>? ProcessAppeared;

    /// <summary>
    /// Поднимается, когда ранее объявленного процесса больше нет в списке процессов ОС.
    /// Стреляет только по тем pid'ам, которые мы до этого выпустили через
    /// <see cref="ProcessAppeared"/>.
    /// </summary>
    public event Action<int>? ProcessDisappeared;

    public ProcessMonitor(
        IOptions<AgentOptions> agentOptions,
        IOptions<ProcessProfileOptions> profileOptions,
        ILogger<ProcessMonitor> logger)
    {
        _processNames = profileOptions.Value.GetWatchedProcessNames();
        _pollInterval = TimeSpan.FromSeconds(agentOptions.Value.ProcessPollIntervalSeconds);
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("Monitor already started.");
        }

        if (_processNames.Count == 0)
        {
            // Законно, но почти наверняка ошибка в конфиге: нет профилей — значит, ни одно окно
            // никогда не будет отслежено. Цикл всё равно оставляем крутиться, чтобы
            // подключившиеся на ходу потребители видели последовательное (пустое) поведение, а
            // не мёртвую службу.
            LogNoProfiles();
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        LogStarted(string.Join(", ", _processNames), _pollInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            if (_loop is not null)
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
            LogStopped(string.Join(", ", _processNames));
        }
    }

    private void Poll()
    {
        var current = SnapshotAll(_processNames);

        var currentPids = current
            .Select(x => x.Pid)
            .ToImmutableHashSet();

        // Исчезновение — стреляем только по тем pid'ам, о появлении которых мы раньше
        // объявляли. Pid'ы, умершие, пока они ещё ждали своего окна, молча выбрасываются из
        // множества «ждущих»: незачем поднимать Disappeared по тому, о чьём появлении мы
        // изначально ничего не говорили.
        var gone = _knownPids
            .Where(knownPid => !currentPids.Contains(knownPid))
            .ToList();

        if (gone.Count != 0)
        {
            foreach (var pid in gone)
            {
                _knownPids.Remove(pid);
                LogProcessDisappeared(pid);
                ProcessDisappeared?.Invoke(pid);
            }
        }

        _pidsLoggedWaiting.RemoveWhere(pid => !currentPids.Contains(pid));

        // Новые появления — откладываем ProcessAppeared, пока MainWindowHandle не станет
        // ненулевым. elementclient_64 у PW порождает процесс за несколько секунд до создания
        // главного окна; выстрелить слишком рано — уронить конструктор GameWindow, после чего
        // pid уже никогда не пересматривается. Пока hwnd не готов, pid не попадает в
        // _knownPids, а значит, следующий тик опроса проверит его снова.
        foreach (var info in current)
        {
            if (_knownPids.Contains(info.Pid))
            {
                continue;
            }

            if (info.MainWindowHandle == IntPtr.Zero)
            {
                if (_pidsLoggedWaiting.Add(info.Pid))
                {
                    LogProcessWaitingForWindow(info.Pid, info.ProcessName);
                }

                continue;
            }

            _knownPids.Add(info.Pid);
            _pidsLoggedWaiting.Remove(info.Pid);
            LogProcessAppeared(info.Pid, info.ProcessName, info.MainWindowHandle.ToInt64());
            ProcessAppeared?.Invoke(info);
        }
    }

    private static List<ProcessInfo> SnapshotAll(IReadOnlyList<string> processNames)
    {
        var result = new List<ProcessInfo>();
        foreach (var name in processNames)
        {
            result.AddRange(SnapshotByName(name));
        }

        return result;
    }

    private static List<ProcessInfo> SnapshotByName(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            var result = new List<ProcessInfo>(procs.Length);
            foreach (var p in procs)
            {
                try
                {
                    result.Add(new ProcessInfo(p.Id, p.ProcessName, p.MainWindowHandle));
                }
                catch
                {
                    // Процесс мог умереть между перечислением и обращением к свойству —
                    // пропускаем его, чтобы один умирающий процесс не подорвал весь тик опроса.
                }
            }

            return result;
        }
        finally
        {
            // Process держит неуправляемый дескриптор; освобождать его нужно детерминированно,
            // а не ждать сборщика мусора.
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                LogPollFailed(ex);
            }

            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "ProcessMonitor запущен по [{ProcessNames}] (интервал {Interval})")]
    partial void LogStarted(string processNames, TimeSpan interval);

    [LoggerMessage(LogLevel.Information, "ProcessMonitor остановлен по [{ProcessNames}]")]
    partial void LogStopped(string processNames);

    [LoggerMessage(LogLevel.Warning,
        "Не настроено ни одного ProcessProfiles — ProcessMonitor'у не за чем следить; добавьте записи в раздел \"ProcessProfiles\" в appsettings.json")]
    partial void LogNoProfiles();

    [LoggerMessage(LogLevel.Information, "Появился процесс: pid={Pid} имя='{ProcessName}' hwnd=0x{Hwnd:X}")]
    partial void LogProcessAppeared(int pid, string processName, long hwnd);

    [LoggerMessage(LogLevel.Information, "Процесс исчез: pid={Pid}")]
    partial void LogProcessDisappeared(int pid);

    [LoggerMessage(LogLevel.Error, "Тик опроса не удался; цикл продолжается")]
    partial void LogPollFailed(Exception ex);

    [LoggerMessage(LogLevel.Information,
        "Процесс виден, но главное окно ещё не готово: pid={Pid} имя='{ProcessName}' — продолжаем опрашивать, пока не появится hwnd")]
    partial void LogProcessWaitingForWindow(int pid, string processName);

    #endregion
}
