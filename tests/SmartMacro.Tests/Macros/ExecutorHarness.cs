using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Macros;

/// <summary>
/// Написанная руками записывающая подделка <see cref="IMacroPrimitives"/>: тесты walker'а
/// проверяют последовательности вызовов, а это читается яснее, чем настройка упорядоченных
/// вызовов в FakeItEasy. Результаты vision подставляются функциями <c>*Handler</c>;
/// <see cref="PressKeyGate"/> позволяет тестам семантики await застопорить прогон в заранее
/// известной точке.
/// </summary>
internal sealed class RecordingPrimitives : IMacroPrimitives
{
    public sealed record Call(string Op, IntPtr Hwnd, object? A = null, object? B = null);

    private readonly Lock _lock = new();
    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public Func<IntPtr, string, ScreenRect?, ScreenPoint?> FindHandler { get; set; } = (_, _, _) => null;
    public Func<IntPtr, string, ScreenRect?, int, ScreenPoint?> WaitHandler { get; set; } = (_, _, _, _) => null;
    public Func<IntPtr, string, ScreenRect, string?> RecognizeHandler { get; set; } = (_, _, _) => null;

    /// <summary>Если задан, каждый PressKeyAsync после записи вызова дожидается возвращённой задачи.</summary>
    public Func<Task>? PressKeyGate { get; set; }

    public async Task PressKeyAsync(IntPtr hwnd, VirtualKey key, CancellationToken ct)
    {
        Record(new Call("PressKey", hwnd, key));
        if (PressKeyGate is { } gate)
        {
            await gate();
        }
    }

    public Task ClickAsync(IntPtr hwnd, ScreenPoint point, bool doubleClick, CancellationToken ct)
    {
        Record(new Call("Click", hwnd, point, doubleClick));
        return Task.CompletedTask;
    }

    public Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, string template, ScreenRect? region, CancellationToken ct)
    {
        Record(new Call("Find", hwnd, template));
        return Task.FromResult(FindHandler(hwnd, template, region));
    }

    public Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, string template, ScreenRect? region, int timeoutMs,
        CancellationToken ct)
    {
        Record(new Call("Wait", hwnd, template, timeoutMs));
        return Task.FromResult(WaitHandler(hwnd, template, region, timeoutMs));
    }

    public Task<string?> RecognizeAsync(IntPtr hwnd, string templateSet, ScreenRect region, CancellationToken ct)
    {
        Record(new Call("Recognize", hwnd, templateSet));
        return Task.FromResult(RecognizeHandler(hwnd, templateSet, region));
    }

    public Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct)
    {
        Record(new Call("SetIcon", hwnd, iconPath));
        return Task.CompletedTask;
    }

    private void Record(Call call)
    {
        lock (_lock)
        {
            _calls.Add(call);
        }
    }
}

/// <summary>Разрешатель <see cref="IMacroGraphResolver"/> поверх словаря — для тестов с под-макросами.</summary>
internal sealed class DictionaryResolver : IMacroGraphResolver
{
    private readonly Dictionary<string, MacroGraph> _graphs = new(StringComparer.Ordinal);

    public void Add(MacroGraph graph) => _graphs[graph.Name] = graph;

    public MacroGraph? TryGet(string name) => _graphs.GetValueOrDefault(name);
}

/// <summary>
/// Один исполнитель, собранный с настоящим WindowRegistry, записывающей подделкой примитивов и
/// разрешателем поверх словаря.
/// </summary>
internal sealed class ExecutorHarness
{
    /// <summary>Контекстное окно по умолчанию, которым пользуются тесты.</summary>
    public static readonly IntPtr Window = new(0xA);

    public WindowRegistry Registry { get; } = new(NullLogger<WindowRegistry>.Instance);
    public RecordingPrimitives Primitives { get; } = new();
    public DictionaryResolver Resolver { get; } = new();
    public MacroExecutor Executor { get; }

    public ExecutorHarness()
    {
        Executor = new MacroExecutor(Primitives, Registry, Resolver, NullLogger<MacroExecutor>.Instance);
    }

    public MacroRunContext Context(
        IntPtr? window = null,
        MacroVariables? variables = null,
        Action<string>? onNodeEntered = null,
        IMacroRunObserver? observer = null,
        Guid runId = default,
        IMacroDebugger? debugger = null)
    {
        return new MacroRunContext
        {
            ContextWindow = window,
            Variables = variables ?? new MacroVariables(),
            OnNodeEntered = onNodeEntered,
            Observer = observer,
            RunId = runId,
            Debugger = debugger,
        };
    }

    public static MacroGraph Graph(string name, string startId, params MacroNode[] nodes) =>
        new() { Name = name, StartNodeId = startId, Nodes = [.. nodes] };
}

/// <summary>
/// Записывающий <see cref="IMacroRunObserver"/> для тестов трассировки.
///
/// <see cref="IsEnabled"/> сделан изменяемым, потому что этот флаг и есть вся защита от
/// затопления: когда он выключен, walker обязан пропускать и замер времени, и форматирование
/// подробностей, — а проверить это можно единственным способом: выключить и убедиться, что не
/// пришло ничего.
/// </summary>
internal sealed class RecordingObserver : IMacroRunObserver
{
    /// <summary>Одно сообщённое событие, разложенное в плоскую запись.</summary>
    /// <param name="Kind">начало обхода / вход / выход / конец обхода.</param>
    /// <param name="WalkId">Обход, которому оно принадлежит.</param>
    /// <param name="NodeId">Нода или <c>null</c> для видов уровня обхода.</param>
    /// <param name="Outcome">Константа из <c>RunOutcomes</c> или <c>null</c>.</param>
    /// <param name="Detail">Строка для лога или <c>null</c>.</param>
    internal sealed record Entry(string Kind, Guid WalkId, string? NodeId, string? Outcome, string? Detail);

    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];
    private readonly List<MacroWalkStart> _walks = [];

    public bool IsEnabled { get; set; } = true;

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Все объявленные обходы по порядку.</summary>
    public IReadOnlyList<MacroWalkStart> Walks
    {
        get
        {
            lock (_lock)
            {
                return [.. _walks];
            }
        }
    }

    /// <summary>Записи одного вида по порядку.</summary>
    public IReadOnlyList<Entry> OfKind(string kind) => [.. Entries.Where(e => e.Kind == kind)];

    // Две проекции ниже существуют ради честности аннотаций, а не ради краткости.
    //
    // Поля Entry размечены nullable правильно: запись — это плоское объединение всех видов
    // событий, и у «walk» действительно нет ноды, а у «enter» — исхода. Но КОНКРЕТНЫЙ вид эту
    // неопределённость снимает: у «exit» исход есть всегда, у «enter» всегда есть нода. Тесты
    // этим и пользуются, и раньше отдавали в проверку IEnumerable<string?> — TUnit ждёт
    // IEnumerable<string>, отсюда и брался CS8631.
    //
    // Приведение типа (`e.Outcome!`) заглушило бы предупреждение, ничего не проверив: null
    // доехал бы до сравнения коллекций и вывалился бы там как «ожидали "found", получили
    // null» — сообщение, по которому не видно, что сломался инвариант вида, а не логика
    // walker'а. Поэтому проекции ненулевое ТРЕБУЮТ и падают с внятным текстом на месте.

    /// <summary>
    /// Id нод у записей вида <paramref name="kind"/>. Годится для видов, где нода есть всегда:
    /// «enter», «exit», <see cref="PausedKind"/>, <see cref="ResumedKind"/>. У
    /// <see cref="VariableKind"/> нода бывает пустой (затравка от триггера) — для него это
    /// не подходит.
    /// </summary>
    public IReadOnlyList<string> NodeIdsOf(string kind) =>
    [
        .. OfKind(kind).Select(e => e.NodeId ?? throw new InvalidOperationException(
            $"У записи вида «{kind}» пустой NodeId, хотя в этом виде нода есть всегда.")),
    ];

    /// <summary>
    /// Исходы записей вида <paramref name="kind"/>. Годится для видов, где исход есть всегда:
    /// «exit», «end», <see cref="PausedKind"/>, а также <see cref="VariableKind"/>, где в исход
    /// уложено имя переменной.
    /// </summary>
    public IReadOnlyList<string> OutcomesOf(string kind) =>
    [
        .. OfKind(kind).Select(e => e.Outcome ?? throw new InvalidOperationException(
            $"У записи вида «{kind}» пустой Outcome, хотя в этом виде исход есть всегда.")),
    ];

    public void WalkStarted(MacroWalkStart walk)
    {
        lock (_lock)
        {
            _walks.Add(walk);
            _entries.Add(new Entry("walk", walk.WalkId, null, null, walk.MacroName));
        }
    }

    public void NodeEntered(Guid walkId, int elapsedMs, string nodeId) =>
        Add(new Entry("enter", walkId, nodeId, null, null));

    public void NodeExited(Guid walkId, int elapsedMs, string nodeId, string outcome, string? detail, int durationMs) =>
        Add(new Entry("exit", walkId, nodeId, outcome, detail));

    public void WalkFinished(Guid walkId, int elapsedMs, string outcome, string? detail) =>
        Add(new Entry("end", walkId, null, outcome, detail));

    // ---- D5 -----------------------------------------------------------------------------
    // Уложены в тот же поток Entry, чтобы можно было проверять ПОРЯДОК паузы относительно входа
    // и выхода её ноды, — а в этом и состоит всё свойство безопасности: парковка между нодами,
    // никогда внутри ноды.

    /// <summary>
    /// Строка вида для <c>VariableSet</c>; в <c>NodeId</c> лежит писатель, в <c>Outcome</c> —
    /// имя переменной, в <c>Detail</c> — значение.
    /// </summary>
    public const string VariableKind = "var";

    /// <summary>Строка вида для паузы; в <c>Outcome</c> уезжает <see cref="DebugPauseReason"/>.</summary>
    public const string PausedKind = "paused";

    /// <summary>Строка вида для возобновления.</summary>
    public const string ResumedKind = "resumed";

    public void VariableSet(Guid walkId, int elapsedMs, string name, string value, string? nodeId) =>
        Add(new Entry(VariableKind, walkId, nodeId, name, value));

    public void WalkPaused(Guid walkId, int elapsedMs, string nodeId, DebugPauseReason reason) =>
        Add(new Entry(PausedKind, walkId, nodeId, reason.ToString(), null));

    public void WalkResumed(Guid walkId, int elapsedMs, string nodeId) =>
        Add(new Entry(ResumedKind, walkId, nodeId, null, null));

    private void Add(Entry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }
}
