using System.Globalization;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Обходит <see cref="MacroGraph"/>: выполнить текущую ноду, пойти по её ребру (у условных —
/// по ребру нужного исхода), чисто остановиться на ребре со значением <c>null</c>. Побочные
/// эффекты идут через <see cref="IMacroPrimitives"/> (ввод/зрение/иконка) и
/// <see cref="WindowRegistry"/> (теги); под-макросы разрешаются через
/// <see cref="IMacroGraphResolver"/>.
///
/// Семантика:
///   * Нода действия с селектором Target — снимок реестра снимается в этот самый момент, и ТО
///     ЖЕ действие веером уходит параллельно на каждое подходящее окно. Ноль совпадений —
///     законное ничегонеделание. Без Target действие бьёт по контекст-окну; нет контекста —
///     обрыв.
///   * Условным нодам контекст-окно обязательно; исход выбирает ребро и записывает
///     <c>FoundPointVar</c>/<c>ResultVar</c> до того, как по ребру пойдут.
///   * <see cref="RunMacroNode"/> — под-прогоны с копией переменных, глубина ≤
///     <see cref="MaxDepth"/>, цикл по именам обрывает прогон. <c>Await=false</c> — «отправил и
///     забыл» (сбои только пишутся в лог).
///   * Отмена уважается между нодами и внутри примитивов; отменённый прогон молча заканчивается
///     со статусом <see cref="MacroRunStatus.Cancelled"/>.
///
/// Без состояния и без знания о реестре — синглтоном безопасен; учёт прогонов живёт в
/// <see cref="MacroRunRegistry"/>, и подводит его вызывающий через
/// <see cref="MacroRunContext.OnNodeEntered"/>.
///
/// <b>Съём показаний (волна D3b).</b> Каждый вызов <see cref="RunAsync"/> — это один ОБХОД со
/// своим id и своими часами, о котором докладывают в <see cref="MacroRunContext.Observer"/>.
/// Единица здесь обход, а не прогон: разветвление <see cref="RunMacroNode"/> порождает по
/// обходу на окно, и различить их дальше по течению можно только потому, что каждый получил
/// собственный id вот здесь. Понодовые события — и строки подробностей вместе с ними —
/// производятся ТОЛЬКО пока наблюдатель говорит, что кто-то слушает, так что демон, за которым
/// не смотрят, платит одно чтение флага на ноду.
///
/// <b>Отладка (волна D5).</b> <see cref="MacroRunContext.Debugger"/> может припарковать обход
/// МЕЖДУ двумя нодами — и никогда внутри ноды. Эта граница и есть весь довод в пользу
/// безопасности: и обрамление <c>ActivateAsync</c>/<c>DeactivateAsync</c> у любой ноды ввода, и
/// побудка с обратной заморозкой на каждом тике зрения целиком живут внутри
/// <see cref="IMacroPrimitives"/>, так что к моменту возврата управления сюда ни одно игровое
/// окно не остаётся разбуженным. Пауза здесь не может бросить клиент замороженным; пауза
/// где-нибудь глубже — может. Дисциплина затвора по <c>IsActive</c> та же, что у наблюдателя:
/// обход, который никто не отлаживает, платит одно volatile-чтение на ноду и ничего не выделяет.
/// </summary>
public sealed partial class MacroExecutor
{
    /// <summary>Предельная глубина вложенности <see cref="RunMacroNode"/> (корневой прогон = 0).</summary>
    public const int MaxDepth = 4;

    private readonly IMacroPrimitives _primitives;
    private readonly WindowRegistry _windows;
    private readonly IMacroGraphResolver _resolver;
    private readonly ILogger<MacroExecutor> _logger;

    public MacroExecutor(
        IMacroPrimitives primitives,
        WindowRegistry windows,
        IMacroGraphResolver resolver,
        ILogger<MacroExecutor> logger)
    {
        _primitives = primitives;
        _windows = windows;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Прогоняет граф до конца. На сбоях уровня прогона не бросает никогда — исход (завершён /
    /// оборван с причиной / отменён) и есть возвращаемый результат.
    /// </summary>
    /// <param name="macro">Обходимый граф.</param>
    /// <param name="context">Состояние прогона: контекст-окно, переменные, наблюдатель, отладчик.</param>
    /// <param name="ct">Отмена; уважается между нодами и внутри примитивов.</param>
    public async Task<MacroRunResult> RunAsync(MacroGraph macro, MacroRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(context);

        // Открывается до первой ноды и закрывается на каждом пути выхода ниже, чтобы список
        // живых обходов у наблюдателя не мог протечь ни одной записью, — именно этот список
        // показывают панели, подключившейся посреди прогона.
        var trace = MacroWalkTrace.Begin(
            context.Observer,
            context.RunId,
            macro.Name,
            context.ContextWindow,
            context.Depth);

        MacroRunResult result;
        try
        {
            result = await RunCoreAsync(macro, context, trace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogCancelled(macro.Name);
            result = MacroRunResult.Cancelled;
        }
        catch (MacroVariableException ex)
        {
            LogAborted(macro.Name, ex.Message);
            result = MacroRunResult.Aborted(ex.Message);
        }
        catch (MacroRunAbortException ex)
        {
            LogAborted(macro.Name, ex.Message);
            result = MacroRunResult.Aborted(ex.Message);
        }

        // До события о завершении, чтобы панель, которая на WalkFinished перечитывает отладчик,
        // не могла увидеть обход, который одновременно и закончился, и всё ещё зарегистрирован.
        context.Debugger?.WalkFinished(trace.WalkId);
        trace.Finished(WalkOutcome(result.Status), result.Error);
        return result;
    }

    private static string WalkOutcome(MacroRunStatus status) => status switch
    {
        MacroRunStatus.Completed => RunOutcomes.Completed,
        MacroRunStatus.Cancelled => RunOutcomes.Cancelled,
        _ => RunOutcomes.Aborted,
    };

    private async Task<MacroRunResult> RunCoreAsync(MacroGraph macro, MacroRunContext context, MacroWalkTrace trace,
        CancellationToken ct)
    {
        var nodesById = new Dictionary<string, MacroNode>(StringComparer.Ordinal);
        foreach (var node in macro.Nodes)
        {
            if (!nodesById.TryAdd(node.Id, node))
            {
                throw new MacroRunAbortException($"Макрос «{macro.Name}»: дубликат id ноды «{node.Id}».");
            }
        }

        // Цепочка, включающая ЭТОТ макрос, — на ней строятся и проверка на циклы, и дочерние
        // контексты.
        var callChain = new List<string>(context.CallChain.Count + 1);
        callChain.AddRange(context.CallChain);
        callChain.Add(macro.Name);

        // Переменные, с которыми обход стартует, — на практике сид `cursor` от триггера плюс
        // всё, что спустил родительский обход. Докладываются один раз, чтобы у панели
        // переменных было живое значение той единственной переменной, которую не пишет ни одна
        // нода.
        if (trace.IsTracing)
        {
            foreach (var (name, value) in context.Variables.Entries)
            {
                trace.VariableSet(name, value.DisplayString, nodeId: null);
            }
        }

        var currentId = macro.StartNodeId;
        while (currentId is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (!nodesById.TryGetValue(currentId, out var node))
            {
                throw new MacroRunAbortException(
                    $"Макрос «{macro.Name}»: ребро ведёт в несуществующую ноду «{currentId}».");
            }

            context.OnNodeEntered?.Invoke(node.Id);
            trace.NodeEntered(node.Id);

            // ЗАТВОР ОТЛАДЧИКА. Между двумя нодами и до того, как пойдут часы ноды, — чтобы
            // пауза не стоила припаркованной ноде ни миллисекунды замеренного времени и чтобы,
            // как сказано в комментарии к классу, пока мы ждём, ни одно игровое окно не сидело
            // разбуженным.
            await GateAsync(context, trace, macro.Name, node.Id, ct).ConfigureAwait(false);

            var nodeStart = MacroWalkTrace.Now;

            NodeStep step;
            try
            {
                step = await ExecuteNodeAsync(macro, node, context, callChain, trace, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (trace.IsTracing)
            {
                // Обход в любом случае окончен — это лишь заставляет лог сказать, КАКАЯ нода его
                // прикончила, а это первое, что хочет знать всякий, кто читает полосу. Фильтр
                // держит всю ветку в стороне от пути, когда никто не смотрит.
                trace.NodeExited(
                    node.Id,
                    ex is OperationCanceledException ? RunOutcomes.Cancelled : RunOutcomes.Error,
                    ex is OperationCanceledException ? null : ex.Message,
                    nodeStart);
                throw;
            }

            trace.NodeExited(node.Id, step.Outcome, step.Detail, nodeStart);
            currentId = step.Next;
        }

        LogCompleted(macro.Name);
        return MacroRunResult.Completed;
    }

    /// <summary>
    /// Придерживает обход перед <paramref name="nodeId"/>, если так велел отладчик.
    ///
    /// Устроено как несинхронный быстрый путь плюс асинхронный медленный, чтобы обычный случай
    /// — отладчика нет либо ничего не взведено — сводился к одному volatile-чтению и возврату
    /// <see cref="Task.CompletedTask"/>, без выделения конечного автомата на каждую ноду.
    /// </summary>
    private static Task GateAsync(
        MacroRunContext context,
        MacroWalkTrace trace,
        string macroName,
        string nodeId,
        CancellationToken ct)
    {
        if (context.Debugger is not { IsActive: true } debugger)
        {
            return Task.CompletedTask;
        }

        // Точки останова ключуются парой (макрос, нода) — той же самой, которой их ставит
        // редактор.
        if (debugger.Arm(trace.WalkId, macroName, nodeId) is not { } gate)
        {
            return Task.CompletedTask;
        }

        return WaitAsync(debugger, gate, trace, ct);

        static async Task WaitAsync(IMacroDebugger debugger, MacroDebugGate gate, MacroWalkTrace trace,
            CancellationToken ct)
        {
            trace.Paused(gate.NodeId, gate.Reason);
            try
            {
                // Отмена («■ Стоп», выключение демона) распускает парковку: OCE
                // распространяется, и обход заканчивается как Cancelled, а не сидит здесь,
                // удерживая свой слот single-flight.
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                debugger.Disarm(gate);
            }

            trace.Resumed(gate.NodeId);
        }
    }

    /// <summary>
    /// Что сделала одна нода: куда идти дальше, каким путём она пошла и (только когда идёт
    /// съём показаний) строка с описанием. Readonly-структура, чтобы обход без съёма показаний
    /// ничего не выделял на ноду.
    /// </summary>
    private readonly record struct NodeStep(string? Next, string Outcome, string? Detail = null)
    {
        /// <summary>Нода действия: выход ровно один.</summary>
        public static NodeStep Done(string? next, string? detail) => new(next, RunOutcomes.Ok, detail);
    }

    private async Task<NodeStep> ExecuteNodeAsync(
        MacroGraph macro,
        MacroNode node,
        MacroRunContext context,
        IReadOnlyList<string> callChain,
        MacroWalkTrace trace,
        CancellationToken ct)
    {
        switch (node)
        {
            case KeyPressNode n:
            {
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.PressKeyAsync(hwnd, n.Key, ct)))
                    .ConfigureAwait(false);
                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout(n.Key.ToString(), targets.Count)));
            }
            case ClickNode n:
            {
                var point = ResolveClickPoint(n, context.Variables);
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.ClickAsync(hwnd, point, n.DoubleClick, ct)))
                    .ConfigureAwait(false);
                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout(
                    n.DoubleClick ? $"{point.X},{point.Y} dbl" : $"{point.X},{point.Y}",
                    targets.Count)));
            }
            case DelayNode n:
            {
                if (n.Ms > 0)
                {
                    await Task.Delay(n.Ms, ct).ConfigureAwait(false);
                }

                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => $"{n.Ms} мс"));
            }
            case AddTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                var targets = ResolveTargets(n.Id, n.Target, context);
                foreach (var hwnd in targets)
                {
                    _windows.AddTag(hwnd, tag);
                }

                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout($"+{tag}", targets.Count)));
            }
            case RemoveTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                var targets = ResolveTargets(n.Id, n.Target, context);
                foreach (var hwnd in targets)
                {
                    _windows.RemoveTag(hwnd, tag);
                }

                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout($"−{tag}", targets.Count)));
            }
            case SetIconNode n:
            {
                var iconPath = context.Variables.Interpolate(n.IconPath);
                var targets = ResolveTargets(n.Id, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.SetIconAsync(hwnd, iconPath, ct)))
                    .ConfigureAwait(false);
                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout(FileNameOf(iconPath), targets.Count)));
            }
            case RunMacroNode n:
                return await ExecuteRunMacroAsync(macro, n, context, callChain, trace, ct).ConfigureAwait(false);
            case FindElementNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var found = await _primitives.FindElementAsync(hwnd, n.Template, n.Region, ct).ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        SetVariable(trace, context, n.Id, n.FoundPointVar, point);
                    }

                    return new NodeStep(n.Found, RunOutcomes.Found,
                        DetailIfTracing(trace, () => $"{n.Template} @ {point.X},{point.Y}"));
                }

                return new NodeStep(n.NotFound, RunOutcomes.NotFound, DetailIfTracing(trace, () => n.Template));
            }
            case WaitForElementNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var found = await _primitives.WaitForElementAsync(hwnd, n.Template, n.Region, n.TimeoutMs, ct)
                    .ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        SetVariable(trace, context, n.Id, n.FoundPointVar, point);
                    }

                    return new NodeStep(n.Found, RunOutcomes.Found,
                        DetailIfTracing(trace, () => $"{n.Template} @ {point.X},{point.Y}"));
                }

                return new NodeStep(n.Timeout, RunOutcomes.Timeout,
                    DetailIfTracing(trace, () => $"{n.Template} · лимит {n.TimeoutMs} мс"));
            }
            case RecognizeTagNode n:
            {
                var hwnd = RequireContext(n.Id, context);
                var tag = await _primitives.RecognizeAsync(hwnd, n.TemplateSet, n.Region, ct).ConfigureAwait(false);
                if (tag is not null)
                {
                    SetVariable(trace, context, n.Id, n.ResultVar, tag);
                    if (n.ApplyTag)
                    {
                        _windows.AddTag(hwnd, tag);
                    }

                    return new NodeStep(n.Matched, RunOutcomes.Matched,
                        DetailIfTracing(trace, () => $"{n.TemplateSet} → {tag}"));
                }

                return new NodeStep(n.NotMatched, RunOutcomes.NotMatched, DetailIfTracing(trace, () => n.TemplateSet));
            }
            default:
                throw new MacroRunAbortException(
                    $"Макрос «{macro.Name}»: нода «{node.Id}» имеет неподдерживаемый тип {node.GetType().Name}.");
        }
    }

    // Всего три места, где нода пишет переменную (§5.3 спеки). Проведены через одного
    // помощника, чтобы в каком-нибудь из них нельзя было забыть доложить наружу: панель
    // переменных, показывающая устаревшее значение {tag}, была бы неотличима от несработавшего
    // распознавания.
    private static void SetVariable(MacroWalkTrace trace, MacroRunContext context, string nodeId, string name,
        VariableValue value)
    {
        context.Variables.Set(name, value);
        trace.VariableSet(name, value.DisplayString, nodeId);
    }

    // Строки подробностей существуют только ради полосы лога, поэтому и строятся они, только
    // когда её кто-то читает. Именно поэтому всё выше передаёт лямбду, а не строку:
    // интерполяции — то единственное по-настоящему понодовое выделение памяти, которое этот
    // класс иначе делал бы на каждом прогоне каждого макроса, смотрят на него или нет.
    // Имя не просто «Detail»: у NodeStep есть одноимённое СВОЙСТВО, и метод внешнего класса его
    // затенял бы — предупреждение анализатора, а заодно и настоящая двусмысленность при чтении.
    private static string? DetailIfTracing(MacroWalkTrace trace, Func<string> build) =>
        trace.IsTracing ? build() : null;

    // «C» для обычного случая с одним окном, «C ×7» для веера по селектору, «C ×0» для
    // селектора, который не совпал ни с чем, — а это законное ничегонеделание и ровно то, что
    // читающий лог и пытается выяснить.
    private static string Fanout(string what, int targets) =>
        targets == 1 ? what : string.Create(CultureInfo.InvariantCulture, $"{what} ×{targets}");

    private static string FileNameOf(string path)
    {
        try
        {
            return Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        }
        catch (ArgumentException)
        {
            // Подставленная переменная способна засунуть сюда что угодно, включая недопустимые
            // в пути символы. Сырая строка — вполне годная строка лога.
            return path;
        }
    }

    private async Task<NodeStep> ExecuteRunMacroAsync(
        MacroGraph macro,
        RunMacroNode node,
        MacroRunContext context,
        IReadOnlyList<string> callChain,
        MacroWalkTrace trace,
        CancellationToken ct)
    {
        var name = context.Variables.Interpolate(node.MacroName);

        if (context.Depth + 1 > MaxDepth)
        {
            throw new MacroRunAbortException(
                $"Макрос «{macro.Name}»: запуск «{name}» вышел бы за предел вложенности под-макросов ({MaxDepth}).");
        }

        if (callChain.Contains(name, StringComparer.Ordinal))
        {
            throw new MacroRunAbortException(
                $"Макрос «{macro.Name}»: цикл вызовов макросов {string.Join(" → ", callChain)} → {name}.");
        }

        var subMacro = _resolver.TryGet(name)
                       ?? throw new MacroRunAbortException(
                           $"Макрос «{macro.Name}»: нода «{node.Id}» ссылается на несуществующий макрос «{name}».");

        List<MacroRunContext> childContexts = [];
        if (node.Target is { } selector)
        {
            foreach (var window in SelectorEvaluator.Select(_windows.Snapshot(), selector))
            {
                childContexts.Add(BuildChildContext(context, callChain, window.Hwnd));
            }

            if (childContexts.Count == 0)
            {
                LogNoTargets(macro.Name, node.Id);
            }
        }
        else
        {
            var hwnd = RequireContext(node.Id, context);
            childContexts.Add(BuildChildContext(context, callChain, hwnd));
        }

        var subRuns = childContexts.Select(child => RunAsync(subMacro, child, ct)).ToList();
        if (node.Await)
        {
            var results = await Task.WhenAll(subRuns).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result.Status == MacroRunStatus.Cancelled)
                {
                    throw new OperationCanceledException(ct);
                }

                if (result.Status == MacroRunStatus.Aborted)
                {
                    throw new MacroRunAbortException(
                        $"Макрос «{macro.Name}»: под-макрос «{name}» оборван: {result.Error}");
                }
            }
        }
        else
        {
            _ = ObserveDetachedSubRunsAsync(name, subRuns);
        }

        return NodeStep.Done(node.Next, DetailIfTracing(trace, () => Fanout(
            node.Await ? name : $"{name} (без ожидания)",
            childContexts.Count)));
    }

    private static MacroRunContext BuildChildContext(MacroRunContext parent, IReadOnlyList<string> callChain,
        IntPtr contextWindow)
    {
        return new MacroRunContext
        {
            ContextWindow = contextWindow,
            Variables = parent.Variables.Clone(),
            Depth = parent.Depth + 1,
            CallChain = callChain,
            RunId = parent.RunId,
            OnNodeEntered = parent.OnNodeEntered,
            // Наследуется, а не заводится на каждого ребёнка: наблюдатель — синглтон, а
            // собственная идентичность ДОЧЕРНЕГО ОБХОДА рождается в MacroWalkTrace.Begin внутри
            // его же RunAsync. Именно это и делает веер на десять окон десятью обходами, за
            // которыми можно следить по отдельности, хотя докладывают они один RunId.
            Observer = parent.Observer,
            // Соображение то же: сессия — синглтон, а состояние паузы по обходу ключуется
            // СОБСТВЕННЫМ id дочернего обхода, поэтому каждая ветка веера ставится на паузу и
            // шагает независимо.
            Debugger = parent.Debugger,
        };
    }

    /// <summary>
    /// У под-прогонов «отправил и забыл» сбои всё равно наблюдаются и попадают в лог — просто
    /// они не задерживают родительский обход.
    /// </summary>
    private async Task ObserveDetachedSubRunsAsync(string name, IReadOnlyList<Task<MacroRunResult>> subRuns)
    {
        try
        {
            var results = await Task.WhenAll(subRuns).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result.Status == MacroRunStatus.Aborted)
                {
                    LogDetachedSubRunAborted(name, result.Error ?? "(без подробностей)");
                }
            }
        }
        catch (Exception ex)
        {
            LogDetachedSubRunFailed(ex, name);
        }
    }

    private IReadOnlyList<IntPtr> ResolveTargets(string nodeId, TargetSelector? target, MacroRunContext context)
    {
        if (target is null)
        {
            if (context.ContextWindow is { } hwnd)
            {
                return [hwnd];
            }

            throw new MacroRunAbortException(
                $"У ноды «{nodeId}» нет селектора Target, а у этого прогона нет контекстного окна (у прогонов от хоткея его не бывает).");
        }

        var matched = SelectorEvaluator.Select(_windows.Snapshot(), target);
        return matched.Select(window => window.Hwnd).ToArray();
    }

    private static IntPtr RequireContext(string nodeId, MacroRunContext context)
    {
        return context.ContextWindow
               ?? throw new MacroRunAbortException(
                   $"Ноде «{nodeId}» нужно контекстное окно, а у этого прогона его нет (у прогонов от хоткея его не бывает).");
    }

    private static ScreenPoint ResolveClickPoint(ClickNode node, MacroVariables variables)
    {
        return (node.Point, node.PointVar) switch
        {
            ({ } point, null) => point,
            (null, { } pointVar) => variables.GetPoint(pointVar),
            _ => throw new MacroRunAbortException(
                $"У ноды ClickNode «{node.Id}» должно быть задано ровно одно из Point / PointVar."),
        };
    }

    [LoggerMessage(LogLevel.Information, "Макрос '{MacroName}' завершён")]
    partial void LogCompleted(string macroName);

    [LoggerMessage(LogLevel.Information, "Макрос '{MacroName}' отменён")]
    partial void LogCancelled(string macroName);

    [LoggerMessage(LogLevel.Warning, "Макрос '{MacroName}' оборван: {Reason}")]
    partial void LogAborted(string macroName, string reason);

    [LoggerMessage(LogLevel.Debug,
        "Макрос '{MacroName}': селектор ноды '{NodeId}' не совпал ни с одним окном — ничего не делаем")]
    partial void LogNoTargets(string macroName, string nodeId);

    [LoggerMessage(LogLevel.Warning, "Под-макрос без ожидания '{MacroName}' оборван: {Reason}")]
    partial void LogDetachedSubRunAborted(string macroName, string reason);

    [LoggerMessage(LogLevel.Error, "Под-макрос без ожидания '{MacroName}' упал непредвиденно")]
    partial void LogDetachedSubRunFailed(Exception exception, string macroName);
}

/// <summary>
/// Внутреннее исключение потока управления: нода напоролась на ошибку уровня прогона.
/// <see cref="MacroExecutor.RunAsync"/> превращает его в <see cref="MacroRunStatus.Aborted"/> —
/// за пределы исполнителя оно не выходит никогда.
/// </summary>
internal sealed class MacroRunAbortException(string message) : Exception(message);
