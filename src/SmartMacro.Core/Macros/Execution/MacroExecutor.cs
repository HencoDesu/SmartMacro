using System.Globalization;
using Microsoft.Extensions.Logging;
using SmartMacro.Contracts.Dto;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;
using SmartMacro.Windows;

namespace SmartMacro.Macros.Execution;

/// <summary>
/// Обходит <see cref="MacroGraph"/>: выполнить текущую ноду, пойти по её ребру (у условных —
/// по ребру нужного исхода), чисто остановиться на ребре со значением <c>null</c>. Побочные
/// эффекты идут через <see cref="IMacroPrimitives"/> (ввод/зрение/иконка) и
/// <see cref="WindowRegistry"/> (теги); под-макросы берутся из
/// <see cref="MacroRunContext.Submacros"/> — то есть из бандла ЭТОГО прогона.
///
/// Семантика:
///   * Нода действия с селектором Target — снимок реестра снимается в этот самый момент, и ТО
///     ЖЕ действие веером уходит параллельно на каждое подходящее окно. Ноль совпадений —
///     законное ничегонеделание. Без Target действие бьёт по контекст-окну; нет контекста —
///     обрыв.
///   * Условным нодам контекст-окно обязательно; исход выбирает ребро и записывает
///     <c>FoundPointVar</c>/<c>ResultVar</c> до того, как по ребру пойдут.
///   * <see cref="RunSubmacroNode"/> — под-прогоны с копией переменных, ровно один уровень
///     вложенности. <c>Await=false</c> — «отправил и забыл» (сбои только пишутся в лог).
///   * Отмена уважается между нодами и внутри примитивов; отменённый прогон молча заканчивается
///     со статусом <see cref="MacroRunStatus.Cancelled"/>.
///
/// <b>Волна F4 сняла отсюда два механизма разом.</b> Раньше <c>RunMacroNode</c> звал любой макрос
/// библиотеки по имени, и это требовало предела вложенности (<c>MaxDepth = 4</c>) и детекта
/// циклов по цепочке имён. Под-макросы плоские, так что и то и другое заменено одной проверкой
/// «мы уже внутри под-макроса»: цикл стал невозможен по построению, а не пойман на бегу.
///
/// Без состояния и без знания о реестре — синглтоном безопасен; учёт прогонов живёт в
/// <see cref="MacroRunRegistry"/>, и подводит его вызывающий через
/// <see cref="MacroRunContext.OnNodeEntered"/>.
///
/// <b>Съём показаний (волна D3b).</b> Каждый вызов <see cref="RunAsync"/> — это один ОБХОД со
/// своим id и своими часами, о котором докладывают в <see cref="MacroRunContext.Observer"/>.
/// Единица здесь обход, а не прогон: разветвление <see cref="RunSubmacroNode"/> порождает по
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
/// окно не остаётся разбуженным. Держится это не на порядке строк, а на <c>try/finally</c> в обеих
/// скобках (<c>GameWindow</c> и <c>AgentInputDispatcher</c>): без него «к моменту возврата сюда»
/// было бы верно только для успешного пути, а интересен здесь как раз неуспешный — отмена по
/// «■ Стоп» посреди тика зрения. Пауза здесь не может бросить клиент замороженным; пауза
/// где-нибудь глубже — может. Дисциплина затвора по <c>IsActive</c> та же, что у наблюдателя:
/// обход, который никто не отлаживает, платит одно volatile-чтение на ноду и ничего не выделяет.
/// </summary>
public sealed partial class MacroExecutor
{
    private readonly IMacroPrimitives _primitives;
    private readonly WindowRegistry _windows;
    private readonly ILogger<MacroExecutor> _logger;

    public MacroExecutor(
        IMacroPrimitives primitives,
        WindowRegistry windows,
        ILogger<MacroExecutor> logger)
    {
        _primitives = primitives;
        _windows = windows;
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

        // Открывается до первой ноды и закрывается в finally — на ЛЮБОМ пути выхода, а не только
        // на трёх известных, — чтобы список живых обходов у наблюдателя не мог протечь ни одной
        // записью: именно этот список показывают панели, подключившейся посреди прогона.
        // Обход докладывает о себе МАКРОСОМ (бандлом) и, отдельно, под-макросом: у обхода функции
        // имя графа — это её подпись, а адресуются по макросу и точки останова, и переключатель
        // прогонов в панели.
        var trace = MacroWalkTrace.Begin(
            context.Observer,
            context.RunId,
            context.MacroName ?? macro.Name,
            context.SubmacroId,
            context.SubmacroId is null ? null : macro.Name,
            context.ContextWindow,
            context.Depth);

        // УЧЁТ ОБХОДА ЗАКРЫВАЕТСЯ В finally, А НЕ НА ПУТЯХ ВЫХОДА, и это не косметика. Пока две
        // закрывающие строки стояли ниже catch'ей, они обслуживали ровно те три исключения,
        // которые эти catch'и ловят. Любое ЧЕТВЁРТОЕ пролетало мимо них — и обход оставался
        // «живым» НАВСЕГДА: RunEventPublisher снимает запись только по WalkFinished, так что
        // каждая следующая подписка панели получала фантомный обход, переключатель показывал его
        // идущим, WalkFinished по нему не приходил никогда, а список рос с каждым таким сбоем до
        // перезапуска демона.
        //
        // Четвёртое исключение — не выдумка. Живой пример: нода «Добавить тег» с ПУСТЫМ тегом
        // (панель заводит её именно такой) роняет WindowRegistry.AddTag, который начинается с
        // ArgumentException.ThrowIfNullOrWhiteSpace. Собственный SaveAsync панели такой макрос не
        // запишет (TagNodeRowViewModel.GetInputErrors), но правило это живёт ТОЛЬКО там: общий
        // MacroGraphValidator пустого тега не проверяет вовсе, — значит, бандл, попавший в macros/
        // импортом или правкой руками (а .hsm — store-only zip именно затем, чтобы его правили
        // руками), демон прочтёт, вооружит и запустит.
        MacroRunResult? result = null;
        Exception? unexpected = null;
        try
        {
            result = await RunCoreAsync(macro, context, trace, ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            LogCancelled(macro.Name);
            result = MacroRunResult.Cancelled;
            return result;
        }
        catch (MacroVariableException ex)
        {
            LogAborted(macro.Name, ex.Message);
            result = MacroRunResult.Aborted(ex.Message);
            return result;
        }
        catch (MacroRunAbortException ex)
        {
            LogAborted(macro.Name, ex.Message);
            result = MacroRunResult.Aborted(ex.Message);
            return result;
        }
        catch (Exception ex)
        {
            // Неучтённое исключение — это БАГ, а не сбой уровня прогона, поэтому оно летит дальше:
            // его ловит Orchestrator.RunAsync и пишет отдельной строкой «упал непредвиденно».
            // Превратить его здесь в обычный Aborted значило бы стереть разницу между ошибкой
            // автора графа и ошибкой в движке. Единственное, что делает этот catch, — запоминает
            // причину, чтобы finally мог назвать её в событии конца обхода: обрыв без объяснения в
            // полосе лога неотличим от «нода просто ничего не сделала».
            unexpected = ex;
            throw;
        }
        finally
        {
            // Порядок прежний: снятие с учёта у отладчика ДО события о завершении, чтобы панель,
            // которая на WalkFinished перечитывает отладчик, не могла увидеть обход, который
            // одновременно и закончился, и всё ещё зарегистрирован.
            context.Debugger?.WalkFinished(trace.WalkId);
            // У события конца обхода три законных исхода (см. IMacroRunObserver.WalkFinished), и
            // для непойманного исключения верен единственный — Aborted: обход сломался.
            trace.Finished(
                result is { } done ? WalkOutcome(done.Status) : RunOutcomes.Aborted,
                result is { } finished ? finished.Error : unexpected?.Message);
        }
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
        var nodesById = new Dictionary<Guid, MacroNode>();
        foreach (var node in macro.Nodes)
        {
            if (!nodesById.TryAdd(node.Id, node))
            {
                throw new MacroRunAbortException(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Run_Abort_DuplicateNodeId,
                    macro.Name,
                    MacroNodeNames.Display(node)));
            }
        }

        // Переменные, с которыми обход стартует, — на практике сид `cursor` от триггера плюс
        // всё, что спустил родительский обход. Докладываются один раз, чтобы у панели
        // переменных было живое значение той единственной переменной, которую не пишет ни одна
        // нода.
        if (trace.IsTracing)
        {
            foreach (var (name, value) in context.Variables.Entries)
            {
                trace.VariableSet(name, value.DisplayString, node: null);
            }
        }

        var currentId = (Guid?)macro.StartNodeId;
        while (currentId is { } id)
        {
            ct.ThrowIfCancellationRequested();
            if (!nodesById.TryGetValue(id, out var node))
            {
                throw new MacroRunAbortException(string.Format(
                    CultureInfo.CurrentCulture, Strings.Run_Abort_EdgeToMissingNode, macro.Name));
            }

            context.OnNodeEntered?.Invoke(MacroNodeNames.Display(node));
            trace.NodeEntered(node);

            // ЗАТВОР ОТЛАДЧИКА. Между двумя нодами и до того, как пойдут часы ноды, — чтобы
            // пауза не стоила припаркованной ноде ни миллисекунды замеренного времени и чтобы,
            // как сказано в комментарии к классу, пока мы ждём, ни одно игровое окно не сидело
            // разбуженным.
            await GateAsync(context, trace, context.MacroName ?? macro.Name, node, ct).ConfigureAwait(false);

            var nodeStart = MacroWalkTrace.Now;

            NodeStep step;
            try
            {
                step = await ExecuteNodeAsync(macro, node, context, trace, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (trace.IsTracing)
            {
                // Обход в любом случае окончен — это лишь заставляет лог сказать, КАКАЯ нода его
                // прикончила, а это первое, что хочет знать всякий, кто читает полосу. Фильтр
                // держит всю ветку в стороне от пути, когда никто не смотрит.
                trace.NodeExited(
                    node,
                    ex is OperationCanceledException ? RunOutcomes.Cancelled : RunOutcomes.Error,
                    ex is OperationCanceledException ? null : ex.Message,
                    nodeStart);
                throw;
            }

            trace.NodeExited(node, step.Outcome, step.Detail, nodeStart);
            currentId = step.Next;
        }

        LogCompleted(macro.Name);
        return MacroRunResult.Completed;
    }

    /// <summary>
    /// Придерживает обход перед <paramref name="node"/>, если так велел отладчик.
    ///
    /// Устроено как несинхронный быстрый путь плюс асинхронный медленный, чтобы обычный случай
    /// — отладчика нет либо ничего не взведено — сводился к одному volatile-чтению и возврату
    /// <see cref="Task.CompletedTask"/>, без выделения конечного автомата на каждую ноду.
    /// </summary>
    private static Task GateAsync(
        MacroRunContext context,
        MacroWalkTrace trace,
        string macroName,
        MacroNode node,
        CancellationToken ct)
    {
        if (context.Debugger is not { IsActive: true } debugger)
        {
            return Task.CompletedTask;
        }

        // Точки останова ключуются тройкой (макрос, под-макрос, нода) — той же самой, которой их
        // ставит редактор. Третья координата появилась в F4: пары перестало хватать, потому что
        // ноды под-макроса живут в том же макросе, но в другом графе.
        if (debugger.Arm(trace.WalkId, macroName, context.SubmacroId, node.Id, MacroNodeNames.Display(node))
            is not { } gate)
        {
            return Task.CompletedTask;
        }

        return WaitAsync(debugger, gate, trace, ct);

        static async Task WaitAsync(IMacroDebugger debugger, MacroDebugGate gate, MacroWalkTrace trace,
            CancellationToken ct)
        {
            trace.Paused(gate.NodeId, gate.NodeName, gate.Reason);
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

            trace.Resumed(gate.NodeId, gate.NodeName);
        }
    }

    /// <summary>
    /// Что сделала одна нода: куда идти дальше, каким путём она пошла и (только когда идёт
    /// съём показаний) строка с описанием. Readonly-структура, чтобы обход без съёма показаний
    /// ничего не выделял на ноду.
    /// </summary>
    private readonly record struct NodeStep(Guid? Next, string Outcome, string? Detail = null)
    {
        /// <summary>Нода действия: выход ровно один.</summary>
        public static NodeStep Done(Guid? next, string? detail) => new(next, RunOutcomes.Ok, detail);
    }

    private async Task<NodeStep> ExecuteNodeAsync(
        MacroGraph macro,
        MacroNode node,
        MacroRunContext context,
        MacroWalkTrace trace,
        CancellationToken ct)
    {
        switch (node)
        {
            case KeyPressNode n:
            {
                var targets = ResolveTargets(n, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.PressKeyAsync(hwnd, n.Key, ct)))
                    .ConfigureAwait(false);
                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout(n.Key.ToString(), targets.Count)));
            }
            case ClickNode n:
            {
                var point = ResolveClickPoint(n, context.Variables);
                var targets = ResolveTargets(n, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.ClickAsync(hwnd, point, n.DoubleClick, ct)))
                    .ConfigureAwait(false);
                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout(
                    n.DoubleClick
                        ? string.Format(
                            CultureInfo.CurrentCulture, Strings.Run_Detail_ClickDouble, point.X, point.Y)
                        : $"{point.X},{point.Y}",
                    targets.Count)));
            }
            case DelayNode n:
            {
                if (n.Ms > 0)
                {
                    await Task.Delay(n.Ms, ct).ConfigureAwait(false);
                }

                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => string.Format(
                    CultureInfo.CurrentCulture, Strings.Run_Detail_Delay, n.Ms)));
            }
            case AddTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                var targets = ResolveTargets(n, n.Target, context);
                foreach (var hwnd in targets)
                {
                    _windows.AddTag(hwnd, tag);
                }

                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout($"+{tag}", targets.Count)));
            }
            case RemoveTagNode n:
            {
                var tag = context.Variables.Interpolate(n.Tag);
                var targets = ResolveTargets(n, n.Target, context);
                foreach (var hwnd in targets)
                {
                    _windows.RemoveTag(hwnd, tag);
                }

                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout($"−{tag}", targets.Count)));
            }
            case SetIconNode n:
            {
                var iconPath = context.Variables.Interpolate(n.IconPath);
                var targets = ResolveTargets(n, n.Target, context);
                await Task.WhenAll(targets.Select(hwnd => _primitives.SetIconAsync(hwnd, iconPath, ct)))
                    .ConfigureAwait(false);
                return NodeStep.Done(n.Next, DetailIfTracing(trace, () => Fanout(FileNameOf(iconPath), targets.Count)));
            }
            case RunSubmacroNode n:
                return await ExecuteSubmacroAsync(macro, n, context, trace, ct).ConfigureAwait(false);
            case FindElementNode n:
            {
                var hwnd = RequireContext(n, context);
                if (ResolveTemplate(context, macro.Name, n, n.Template) is not { } template)
                {
                    return new NodeStep(n.NotFound, RunOutcomes.NotFound,
                        DetailIfTracing(trace, () => string.Format(
                            CultureInfo.CurrentCulture, Strings.Run_Detail_TemplateNotInMacro, n.Template)));
                }

                var found = await _primitives.FindElementAsync(hwnd, template, n.Region, n.MatchThreshold, ct).ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        SetVariable(trace, context, n, n.FoundPointVar, point);
                    }

                    return new NodeStep(n.Found, RunOutcomes.Found,
                        DetailIfTracing(trace, () => $"{n.Template} @ {point.X},{point.Y}"));
                }

                return new NodeStep(n.NotFound, RunOutcomes.NotFound, DetailIfTracing(trace, () => n.Template));
            }
            case WaitForElementNode n:
            {
                var hwnd = RequireContext(n, context);
                if (ResolveTemplate(context, macro.Name, n, n.Template) is not { } template)
                {
                    return new NodeStep(n.Timeout, RunOutcomes.Timeout,
                        DetailIfTracing(trace, () => string.Format(
                            CultureInfo.CurrentCulture, Strings.Run_Detail_TemplateNotInMacro, n.Template)));
                }

                var found = await _primitives.WaitForElementAsync(hwnd, template, n.Region, n.TimeoutMs, n.MatchThreshold, ct)
                    .ConfigureAwait(false);
                if (found is { } point)
                {
                    if (n.FoundPointVar is not null)
                    {
                        SetVariable(trace, context, n, n.FoundPointVar, point);
                    }

                    return new NodeStep(n.Found, RunOutcomes.Found,
                        DetailIfTracing(trace, () => $"{n.Template} @ {point.X},{point.Y}"));
                }

                return new NodeStep(n.Timeout, RunOutcomes.Timeout,
                    DetailIfTracing(trace, () => string.Format(
                        CultureInfo.CurrentCulture, Strings.Run_Detail_WaitTimedOut, n.Template, n.TimeoutMs)));
            }
            case RecognizeTagNode n:
            {
                var hwnd = RequireContext(n, context);
                var set = ResolveTemplateSet(context, macro.Name, n, n.TemplateSet);
                if (set.Count == 0)
                {
                    return new NodeStep(n.NotMatched, RunOutcomes.NotMatched,
                        DetailIfTracing(trace, () => string.Format(
                            CultureInfo.CurrentCulture, Strings.Run_Detail_TemplateNotInMacro, n.TemplateSet)));
                }

                var tag = await _primitives.RecognizeAsync(hwnd, set, n.Region, n.MatchThreshold, ct).ConfigureAwait(false);
                if (tag is not null)
                {
                    SetVariable(trace, context, n, n.ResultVar, tag);
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
                throw new MacroRunAbortException(string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Run_Abort_UnsupportedNodeType,
                    macro.Name,
                    MacroNodeNames.Display(node),
                    node.GetType().Name));
        }
    }

    /// <summary>
    /// Имя шаблона → байты, из бандла ЭТОГО прогона (волна F2).
    ///
    /// Разрешает обходчик, а не примитивы: с переездом шаблонов внутрь бандла имя без макроса
    /// ничего не значит, а источник живёт в контексте прогона. Промах — не обрыв: нода уходит по
    /// «не найдено», ровно как при отсутствующем файле в прежнем общем дереве, потому что ветка
    /// на этот исход в графе уже нарисована. Валидатор говорит про это заранее и статически
    /// (<c>MacroGraphValidator</c> + <c>MacroTemplateInventory</c>) — здесь остаётся только
    /// строчка в журнале для того случая, когда предупреждение проигнорировали.
    /// </summary>
    private byte[]? ResolveTemplate(MacroRunContext context, string macroName, MacroNode node, string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            LogTemplateMissing(macroName, MacroNodeNames.Display(node), "(пусто)");
            return null;
        }

        if (context.Templates?.TryGetTemplate(templateName) is { } bytes)
        {
            return bytes;
        }

        LogTemplateMissing(macroName, MacroNodeNames.Display(node), templateName);
        return null;
    }

    /// <summary>То же для набора: пустой словарь означает «набора в бандле нет».</summary>
    private IReadOnlyDictionary<string, byte[]> ResolveTemplateSet(MacroRunContext context, string macroName,
        MacroNode node, string setName)
    {
        if (!string.IsNullOrWhiteSpace(setName)
            && context.Templates?.GetSet(setName) is { Count: > 0 } templates)
        {
            return templates;
        }

        LogTemplateSetMissing(macroName, MacroNodeNames.Display(node),
            string.IsNullOrWhiteSpace(setName) ? "(пусто)" : setName);
        return EmptyTemplates;
    }

    private static readonly IReadOnlyDictionary<string, byte[]> EmptyTemplates =
        new Dictionary<string, byte[]>(StringComparer.Ordinal);

    // Всего три места, где нода пишет переменную (§5.3 спеки). Проведены через одного
    // помощника, чтобы в каком-нибудь из них нельзя было забыть доложить наружу: панель
    // переменных, показывающая устаревшее значение {tag}, была бы неотличима от несработавшего
    // распознавания.
    private static void SetVariable(MacroWalkTrace trace, MacroRunContext context, MacroNode node, string name,
        VariableValue value)
    {
        context.Variables.Set(name, value);
        trace.VariableSet(name, value.DisplayString, node);
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

    /// <summary>
    /// Вызов под-макроса (волна F4).
    ///
    /// <b>Плоскость проверяется здесь, и это ЕДИНСТВЕННАЯ страховка от циклов.</b> Раньше их было
    /// две — предел вложенности и детект цикла по цепочке имён, — и обе ловили беду на бегу.
    /// Теперь беды нет: под-макрос не имеет права звать под-макрос, валидатор такой граф
    /// отвергает, а эта строка ловит файл, правленный руками мимо валидатора.
    /// </summary>
    private async Task<NodeStep> ExecuteSubmacroAsync(
        MacroGraph macro,
        RunSubmacroNode node,
        MacroRunContext context,
        MacroWalkTrace trace,
        CancellationToken ct)
    {
        var macroName = context.MacroName ?? macro.Name;

        if (context.SubmacroId is not null)
        {
            throw new MacroRunAbortException(string.Format(
                CultureInfo.CurrentCulture, Strings.Run_Abort_NestedSubmacro, macroName, macro.Name));
        }

        var subMacro = context.Submacros?.GetValueOrDefault(node.SubmacroId)
                       ?? throw new MacroRunAbortException(string.Format(
                           CultureInfo.CurrentCulture,
                           Strings.Run_Abort_SubmacroNotFound,
                           macroName,
                           MacroNodeNames.Display(node)));

        List<MacroRunContext> childContexts = [];
        if (node.Target is { } selector)
        {
            foreach (var window in SelectorEvaluator.Select(_windows.Snapshot(), selector))
            {
                childContexts.Add(BuildChildContext(context, macroName, node.SubmacroId, window.Hwnd));
            }

            if (childContexts.Count == 0)
            {
                LogNoTargets(macroName, MacroNodeNames.Display(node));
            }
        }
        else
        {
            var hwnd = RequireContext(node, context);
            childContexts.Add(BuildChildContext(context, macroName, node.SubmacroId, hwnd));
        }

        var name = subMacro.Name;
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
                    throw new MacroRunAbortException(string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.Run_Abort_SubmacroAborted,
                        macroName,
                        name,
                        result.Error));
                }
            }
        }
        else
        {
            _ = ObserveDetachedSubRunsAsync(name, subRuns);
        }

        return NodeStep.Done(node.Next, DetailIfTracing(trace, () => Fanout(
            node.Await
                ? name
                : string.Format(CultureInfo.CurrentCulture, Strings.Run_Detail_SubmacroDetached, name),
            childContexts.Count)));
    }

    private static MacroRunContext BuildChildContext(
        MacroRunContext parent,
        string macroName,
        Guid submacroId,
        IntPtr contextWindow)
    {
        return new MacroRunContext
        {
            ContextWindow = contextWindow,
            Variables = parent.Variables.Clone(),
            Depth = parent.Depth + 1,
            RunId = parent.RunId,
            // Обход функции по-прежнему принадлежит СВОЕМУ МАКРОСУ: по нему ключуются точки
            // останова и по нему же панель отбирает обходы открытого макроса.
            MacroName = macroName,
            SubmacroId = submacroId,
            // Наследуется, потому что прогон не покидает свой бандл: под-макросы лежат внутри
            // него же, и шаблоны у них ОБЩИЕ с родителем (§5.7) — второго уровня разрешения
            // имён здесь нет и не нужно.
            Templates = parent.Templates,
            // Наследуется ради единообразия, а не ради вложенности: звать отсюда всё равно
            // некого — проверка плоскости выше это и обеспечивает.
            Submacros = parent.Submacros,
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

    // Ноду в сообщениях об ошибках называем ПОДПИСЬЮ: текст читает человек, а guid ему сказать
    // нечего — на канве та же нода подсветится по id, который несёт ValidationIssue.
    private IReadOnlyList<IntPtr> ResolveTargets(MacroNode node, TargetSelector? target, MacroRunContext context)
    {
        if (target is null)
        {
            if (context.ContextWindow is { } hwnd)
            {
                return [hwnd];
            }

            throw new MacroRunAbortException(string.Format(
                CultureInfo.CurrentCulture,
                Strings.Run_Abort_NodeNeedsTargetOrContext,
                MacroNodeNames.Display(node)));
        }

        var matched = SelectorEvaluator.Select(_windows.Snapshot(), target);
        return matched.Select(window => window.Hwnd).ToArray();
    }

    private static IntPtr RequireContext(MacroNode node, MacroRunContext context)
    {
        return context.ContextWindow
               ?? throw new MacroRunAbortException(string.Format(
                   CultureInfo.CurrentCulture,
                   Strings.Run_Abort_NodeNeedsContextWindow,
                   MacroNodeNames.Display(node)));
    }

    private static ScreenPoint ResolveClickPoint(ClickNode node, MacroVariables variables)
    {
        return (node.Point, node.PointVar) switch
        {
            ({ } point, null) => point,
            (null, { } pointVar) => variables.GetPoint(pointVar),
            _ => throw new MacroRunAbortException(string.Format(
                CultureInfo.CurrentCulture,
                Strings.Run_Abort_ClickNeedsExactlyOnePoint,
                MacroNodeNames.Display(node))),
        };
    }

    [LoggerMessage(LogLevel.Information, "Макрос '{MacroName}' завершён")]
    partial void LogCompleted(string macroName);

    [LoggerMessage(LogLevel.Information, "Макрос '{MacroName}' отменён")]
    partial void LogCancelled(string macroName);

    [LoggerMessage(LogLevel.Warning, "Макрос '{MacroName}' оборван: {Reason}")]
    partial void LogAborted(string macroName, string reason);

    [LoggerMessage(LogLevel.Debug,
        "Макрос '{MacroName}': селектор ноды '{NodeName}' не совпал ни с одним окном — ничего не делаем")]
    partial void LogNoTargets(string macroName, string nodeName);

    [LoggerMessage(LogLevel.Warning,
        "Макрос '{MacroName}', нода '{NodeName}': шаблона '{Template}' в бандле нет — уходим по «не найдено»")]
    partial void LogTemplateMissing(string macroName, string nodeName, string template);

    [LoggerMessage(LogLevel.Warning,
        "Макрос '{MacroName}', нода '{NodeName}': набора шаблонов '{TemplateSet}' в бандле нет — уходим по «не совпало»")]
    partial void LogTemplateSetMissing(string macroName, string nodeName, string templateSet);

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
