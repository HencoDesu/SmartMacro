using System.Text;
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
    private readonly List<double?> _thresholds = [];

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

    /// <summary>Пороги совпадения, дошедшие до примитивов, по порядку вызовов. <c>null</c> = нода порога не задавала.</summary>
    public IReadOnlyList<double?> Thresholds
    {
        get
        {
            lock (_lock)
            {
                return [.. _thresholds];
            }
        }
    }

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

    // Волна F2: в интерфейс приезжают БАЙТЫ, разрешённые обходчиком из бандла прогона. Тесты
    // walker'а при этом проверяют, какое ИМЯ шаблона нода назвала, — поэтому подделка источника
    // (FakeTemplateSource) кодирует имя в байты, а здесь оно читается обратно. Сговор двух
    // подделок, и он честнее, чем сверять массивы байтов: проверяется ровно то, что проверялось
    // до F2, — что до примитивов доехал шаблон ТОЙ ноды.
    public Task<ScreenPoint?> FindElementAsync(IntPtr hwnd, byte[] template, ScreenRect? region,
        double? matchThreshold, CancellationToken ct)
    {
        var name = FakeTemplateSource.NameOf(template);
        Record(new Call("Find", hwnd, name), matchThreshold);
        return Task.FromResult(FindHandler(hwnd, name, region));
    }

    public Task<ScreenPoint?> WaitForElementAsync(IntPtr hwnd, byte[] template, ScreenRect? region, int timeoutMs,
        double? matchThreshold, CancellationToken ct)
    {
        var name = FakeTemplateSource.NameOf(template);
        Record(new Call("Wait", hwnd, name, timeoutMs), matchThreshold);
        return Task.FromResult(WaitHandler(hwnd, name, region, timeoutMs));
    }

    public Task<string?> RecognizeAsync(IntPtr hwnd, IReadOnlyDictionary<string, byte[]> templates, ScreenRect region,
        double? matchThreshold, CancellationToken ct)
    {
        var setName = FakeTemplateSource.SetNameOf(templates);
        Record(new Call("Recognize", hwnd, setName), matchThreshold);
        return Task.FromResult(RecognizeHandler(hwnd, setName, region));
    }

    public Task SetIconAsync(IntPtr hwnd, string iconPath, CancellationToken ct)
    {
        Record(new Call("SetIcon", hwnd, iconPath));
        return Task.CompletedTask;
    }

    private void Record(Call call, double? threshold = null)
    {
        lock (_lock)
        {
            _calls.Add(call);
            _thresholds.Add(threshold);
        }
    }
}

/// <summary>
/// Подделка <see cref="IMacroTemplateSource"/>: имя шаблона кодируется в его же байты.
///
/// Так тесты walker'а продолжают говорить про ИМЕНА, хотя интерфейс примитивов с волны F2 берёт
/// байты. <see cref="Missing"/> изображает шаблон, которого в бандле нет, — именно этим путём
/// проверяется, что нода уходит по «не найдено», не позвав примитив вовсе.
/// </summary>
internal sealed class FakeTemplateSource : IMacroTemplateSource
{
    /// <summary>Имена, которых в «бандле» нет.</summary>
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    public byte[]? TryGetTemplate(string templateName) =>
        Missing.Contains(templateName) ? null : Encoding.UTF8.GetBytes(templateName);

    public IReadOnlyDictionary<string, byte[]> GetSet(string setName) =>
        Missing.Contains(setName)
            ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
            : new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [setName] = Encoding.UTF8.GetBytes(setName),
            };

    /// <summary>Обратное преобразование для записывающих примитивов.</summary>
    public static string NameOf(byte[] template) => Encoding.UTF8.GetString(template);

    /// <summary>Имя набора из словаря, который построил <see cref="GetSet"/>.</summary>
    public static string SetNameOf(IReadOnlyDictionary<string, byte[]> templates) =>
        templates.Count == 1 ? templates.Keys.First() : string.Join('+', templates.Keys);
}

/// <summary>
/// Один исполнитель, собранный с настоящим WindowRegistry и записывающей подделкой примитивов.
///
/// Разрешателя макросов по имени здесь больше нет: с волны F4 под-макросы едут в самом контексте
/// прогона (<see cref="MacroRunContext.Submacros"/>), так что тесту достаточно
/// <see cref="AddSubmacro"/>.
/// </summary>
internal sealed class ExecutorHarness
{
    /// <summary>Контекстное окно по умолчанию, которым пользуются тесты.</summary>
    public static readonly IntPtr Window = new(0xA);

    private readonly Dictionary<Guid, MacroGraph> _submacros = [];

    public WindowRegistry Registry { get; } = new(NullLogger<WindowRegistry>.Instance);
    public RecordingPrimitives Primitives { get; } = new();
    public FakeTemplateSource Templates { get; } = new();
    public MacroExecutor Executor { get; }

    public ExecutorHarness()
    {
        Executor = new MacroExecutor(Primitives, Registry, NullLogger<MacroExecutor>.Instance);
    }

    /// <summary>Кладёт под-макрос в «бандл» прогона и отдаёт его id — то, что несёт нода вызова.</summary>
    public Guid AddSubmacro(MacroGraph graph, Guid? id = null)
    {
        var key = id ?? Guid.NewGuid();
        _submacros[key] = graph;
        return key;
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
            // Источник шаблонов в контексте — это и есть порунное разрешение из F2; в бою его
            // ставит Orchestrator.RunAsync. Под-макросы приезжают тем же путём и с волны F4.
            Templates = Templates,
            Submacros = _submacros,
            OnNodeEntered = onNodeEntered,
            Observer = observer,
            RunId = runId,
            Debugger = debugger,
        };
    }

    public static MacroGraph Graph(string name, Guid startId, params MacroNode[] nodes) =>
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
    /// <param name="NodeName">Подпись ноды или <c>null</c> для видов уровня обхода.</param>
    /// <param name="Outcome">Константа из <c>RunOutcomes</c> или <c>null</c>.</param>
    /// <param name="Detail">Строка для лога или <c>null</c>.</param>
    /// <param name="NodeId">Её id — по нему проверяется адресация, тогда как читается подпись.</param>
    internal sealed record Entry(
        string Kind,
        Guid WalkId,
        string? NodeName,
        string? Outcome,
        string? Detail,
        Guid? NodeId = null);

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
        .. OfKind(kind).Select(e => e.NodeName ?? throw new InvalidOperationException(
            $"У записи вида «{kind}» пустая подпись ноды, хотя в этом виде нода есть всегда.")),
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

    public void NodeEntered(Guid walkId, int elapsedMs, Guid nodeId, string nodeName) =>
        Add(new Entry("enter", walkId, nodeName, null, null, nodeId));

    public void NodeExited(Guid walkId, int elapsedMs, Guid nodeId, string nodeName, string outcome, string? detail,
        int durationMs) =>
        Add(new Entry("exit", walkId, nodeName, outcome, detail, nodeId));

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

    public void VariableSet(Guid walkId, int elapsedMs, string name, string value, Guid? nodeId, string? nodeName) =>
        Add(new Entry(VariableKind, walkId, nodeName, name, value, nodeId));

    public void WalkPaused(Guid walkId, int elapsedMs, Guid nodeId, string nodeName, DebugPauseReason reason) =>
        Add(new Entry(PausedKind, walkId, nodeName, reason.ToString(), null, nodeId));

    public void WalkResumed(Guid walkId, int elapsedMs, Guid nodeId, string nodeName) =>
        Add(new Entry(ResumedKind, walkId, nodeName, null, null, nodeId));

    private void Add(Entry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }
}
