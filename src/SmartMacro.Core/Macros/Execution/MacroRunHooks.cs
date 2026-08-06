using SmartMacro.Contracts.Settings;
using SmartMacro.GameWindows;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Ссылки на побудку, которые держит ПРОГОН, — вторая половина <see cref="HookLifetime.Run"/>.
///
/// Обычный хук (<see cref="HookLifetime.Action"/>) сюда не попадает вовсе: его область живёт
/// внутри одной ноды и закрывается там же. <c>Run</c> означает «окно, разбуженное впервые, не
/// засыпает до конца прогона», и держать такую ссылку должен кто-то, кто прогон переживает.
///
/// <b>ЗАХВАТ ЛЕНИВЫЙ, и иначе быть не может:</b> набор окон в начале прогона неизвестен, у каждой
/// ноды свой селектор целей. Ссылку берёт ОБХОДЧИК, у которого цели уже посчитаны, — не примитив:
/// иначе слой «ввод, зрение и косметика по hwnd» начал бы знать, внутри какого прогона он
/// работает, а это ровно тот прокол параметра сквозь <c>IMacroPrimitives</c>, от которого волна F2
/// отказалась для шаблонов.
///
/// <b>Понодовая область при этом остаётся на месте.</b> Нода всё так же открывает свою — просто
/// счётчик окна уже не на нуле, так что вторая область ничего не будит, а её закрытие ничего не
/// замораживает. Никакого второго механизма: <c>Run</c> — это одна лишняя ссылка в том же
/// счётчике.
///
/// <b>⚠️ Припарковавшийся обход отпускает ссылки — <see cref="ReleaseAllAsync"/>.</b> Без этого
/// <c>Run</c> ломал бы довод, на котором стоит затвор отладчика: пауза между нодами не может
/// бросить клиента размороженным именно потому, что к возврату в <c>MacroExecutor</c> все области
/// закрыты. Таймаута бездействия у паузы намеренно нет, так что «человек ушёл за чаем» — это до
/// десяти клиентов, рендерящих в фоне неограниченно долго. Отпускаются ссылки ВСЕГО прогона, а не
/// «свои»: у веера каждый обход и так ведёт своё окно, то есть множества совпадают, а вторая
/// бухгалтерия «кто чью ссылку внёс» стоила бы дороже, чем даёт. Перебор безопасен по направлению
/// — худшее, что бывает, это окно, которое разбудят заново на следующей ноде, то есть ровно
/// <see cref="HookLifetime.Action"/>.
///
/// Возвращать ссылки на возобновлении не нужно: захват ленивый, и ближайшая же нода возьмёт их
/// сама.
/// </summary>
public sealed class MacroRunHooks : IAsyncDisposable
{
    private readonly WindowRegistry _windows;
    private readonly Lock _lock = new();
    private readonly Dictionary<IntPtr, WindowHookScope> _held = [];

    // Окна, чью побудку прямо сейчас кто-то заводит. Нужно, чтобы два обхода веера, нацелившиеся
    // на одно окно, не взяли по ссылке — вторая протекла бы до конца прогона.
    private readonly HashSet<IntPtr> _claiming = [];

    // Поколение растёт на каждом роспуске. Побудка, начатая до роспуска и закончившаяся после,
    // обязана отдать свою ссылку сразу, а не осесть в _held: иначе затвор отладчика оставил бы
    // окно разбуженным — тот самый случай, ради которого роспуск и заведён.
    private int _generation;
    private bool _closed;

    /// <param name="windows">Реестр окон: <c>hwnd → IGameWindow</c>.</param>
    public MacroRunHooks(WindowRegistry windows) => _windows = windows;

    /// <summary>Сколько окон прогон держит разбуженными прямо сейчас. Диагностика и тесты.</summary>
    public int HeldCount
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>
    /// Берёт по ссылке на каждое из <paramref name="targets"/>, чей хук велит держать побудку весь
    /// прогон. Окна с обычным хуком и окна без хука пропускаются — им эта машинерия не нужна.
    /// </summary>
    /// <param name="targets">Дескрипторы, на которые нацелилась нода.</param>
    /// <param name="on">Вокруг чего скобка: ввод или захват.</param>
    /// <param name="cancellationToken">Отмена побудки.</param>
    public Task EnsureAsync(IReadOnlyList<IntPtr> targets, HookOn on, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);

        List<(IntPtr Hwnd, IGameWindow Window)>? claimed = null;
        int generation;
        lock (_lock)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            generation = _generation;
            foreach (var hwnd in targets)
            {
                if (_held.ContainsKey(hwnd) || _claiming.Contains(hwnd))
                {
                    continue;
                }

                if (_windows.TryGetWindow(hwnd) is not { } window || !window.WakesForWholeRun(on))
                {
                    continue;
                }

                _claiming.Add(hwnd);
                (claimed ??= []).Add((hwnd, window));
            }
        }

        if (claimed is null)
        {
            // Обычный случай — ни у одного окна нет хука на весь прогон. Ни ожидания, ни
            // выделения памяти: цена Scope: Run для тех, кто им не пользуется, — один проход по
            // списку целей.
            return Task.CompletedTask;
        }

        // Параллельно, а не по очереди: веер на десять окон иначе платил бы десять пауз
        // устаканивания подряд там, где сегодня платит одну.
        return Task.WhenAll(claimed.Select(target => TakeAsync(target.Hwnd, target.Window, on, generation,
            cancellationToken)));
    }

    /// <summary>
    /// Отпускает все ссылки прогона. Зовёт затвор отладчика перед тем, как встать на паузу;
    /// ближайшая нода после возобновления возьмёт их заново.
    /// </summary>
    public ValueTask ReleaseAllAsync() => DrainAsync(close: false);

    /// <summary>Конец прогона: отпускает всё и закрывает лавочку.</summary>
    public ValueTask DisposeAsync() => DrainAsync(close: true);

    private async Task TakeAsync(IntPtr hwnd, IGameWindow window, HookOn on, int generation,
        CancellationToken cancellationToken)
    {
        WindowHookScope scope;
        try
        {
            scope = await window.EnterHookAsync(on, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _claiming.Remove(hwnd);
            }
        }

        bool keep;
        lock (_lock)
        {
            // Роспуск или конец прогона, случившиеся ПОКА мы будили: ссылку не оставляем.
            keep = !_closed && _generation == generation;
            if (keep)
            {
                _held[hwnd] = scope;
            }
        }

        if (!keep)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask DrainAsync(bool close)
    {
        WindowHookScope[] scopes;
        lock (_lock)
        {
            if (close)
            {
                _closed = true;
            }

            _generation++;
            scopes = [.. _held.Values];
            _held.Clear();
        }

        foreach (var scope in scopes)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }
}
