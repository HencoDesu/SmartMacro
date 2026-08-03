using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D5: отладчик таким, каким его видит панель, — без окон, против поддельного демона.
//
// Закреплять здесь стоит не «кнопка отправляет запрос», а тот автомат состояний, который вокруг
// неё живёт: какие органы управления живы в какой момент, что ГОВОРИТ панель инструментов и два
// места, где панель могла бы втихую соврать, — кнопка остановки, не признающаяся, что
// останавливает N обходов, и пауза, о которой попросили, но которая ещё не наступила.
public class MacroDebuggerViewModelTests
{
    private static MacroEditorViewModel CreateEditor(FakeIpcClient client) =>
        new(client, null, null, ImmediateUiDispatcher.Instance, @"C:\smartmacro\macros");

    /// <summary>Три задержки и запись тега — достаточно, чтобы были и середина, и конец, и переменная.</summary>
    private static MacroGraph Sample() => new()
    {
        Name = "pw-boot",
        StartNodeId = "a",
        Nodes =
        [
            new DelayNode { Id = "a", Ms = 100, Next = "b" },
            new DelayNode { Id = "b", Ms = 100, Next = "c" },
            new RecognizeTagNode
            {
                Id = "c",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 1, 1),
                Matched = "d",
            },
            new SetIconNode { Id = "d", IconPath = "icons/{tag}.png" },
        ],
    };

    private static FakeIpcClient Daemon(params BreakpointSetDto[] breakpoints) =>
        new FakeIpcClient()
            .Respond(IpcMessageTypes.GetMacros, new[] { Sample() })
            .Respond(IpcMessageTypes.GetBreakpoints, breakpoints)
            .Respond(IpcMessageTypes.SubscribeRunEvents, Array.Empty<RunWalkDto>())
            .Respond(IpcMessageTypes.DebugCommand, new DebugAckDto(true, false, false));

    private static MacroEditorViewModel Opened(FakeIpcClient daemon)
    {
        var editor = CreateEditor(daemon);
        editor.SelectedMacro = editor.Macros.Single(m => m.Name == "pw-boot");
        return editor;
    }

    // ---- когда панель инструментов вообще существует -------------------------------------

    [Test]
    public async Task WithNoWalkOnRecord_ThereIsNoDebuggerToolbar()
    {
        var editor = Opened(Daemon());

        // Четыре вечно мёртвые кнопки — вот так панель инструментов и перестают читать.
        await Assert.That(editor.HasDebugTarget).IsFalse();
        await Assert.That(editor.CanPause).IsFalse();
        await Assert.That(editor.CanStop).IsFalse();
        await Assert.That(editor.RunProgressText).IsEmpty();
    }

    [Test]
    public async Task AWalkOfAnotherMacroDoesNotArmThisEditorsToolbar()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);

        var other = RunEvents.Walk("pw-assist", hwnd: 0xB);
        daemon.Push(RunEvents.Started(other), RunEvents.Entered(other, 0, "n1"));

        await Assert.That(editor.HasDebugTarget).IsFalse();
    }

    [Test]
    public async Task ALiveWalkArmsPauseAndStop_ButNotStepUntilItIsParked()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x140804);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"));

        await Assert.That(editor.HasDebugTarget).IsTrue();
        await Assert.That(editor.CanPause).IsTrue();
        await Assert.That(editor.CanStop).IsTrue();
        await Assert.That(editor.CanResume).IsFalse();
        await Assert.That(editor.DebugStateText).IsEqualTo("выполняется");
    }

    [Test]
    public async Task AFinishedWalkLeavesEveryControlDead()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"));
        daemon.Push(RunEvents.Finished(walk, 500, RunOutcomes.Completed));

        // «Обход, за которым я смотрел, только что кончился» не имеет права оставить после себя
        // горящую кнопку «Пауза».
        await Assert.That(editor.CanPause).IsFalse();
        await Assert.That(editor.CanResume).IsFalse();
        await Assert.That(editor.CanStop).IsFalse();
        await Assert.That(editor.SelectedRunIsPaused).IsFalse();
        // Фишка остаётся, так что лог завершившегося обхода по-прежнему читаем.
        await Assert.That(editor.HasDebugTarget).IsTrue();
    }

    // ---- пауза: сперва просьба, потом подтверждение ---------------------------------------

    [Test]
    public async Task PauseIsShownAsPENDING_UntilTheDaemonConfirmsIt()
    {
        var daemon = Daemon();
        daemon.Respond(IpcMessageTypes.DebugCommand, new DebugAckDto(Accepted: true, Paused: false, PauseRequested: true));
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"));

        await editor.PauseAsync();

        // «Паузу» уважают на ближайшей границе нод, а WaitForElement способен оттянуть эту границу
        // на минуту. Без отдельного состояния кнопка просто сереет, и больше не меняется ничего, —
        // а нашлось это, когда на такое посмотрели вживую.
        await Assert.That(editor.DebugStateText).IsEqualTo("пауза…");
        await Assert.That(editor.SelectedRunPauseVisible).IsTrue();
        await Assert.That(editor.PauseNotice).IsEqualTo("пауза запрошена — ждём конца ноды");
        await Assert.That(editor.SelectedRunIsPaused).IsFalse();
        await Assert.That(editor.CanResume).IsFalse();
        // И попросить об этом дважды нельзя.
        await Assert.That(editor.CanPause).IsFalse();

        daemon.Push(RunEvents.Entered(walk, 900, "b"), RunEvents.Paused(walk, 900, "b"));

        await Assert.That(editor.SelectedRunIsPaused).IsTrue();
        await Assert.That(editor.PauseNotice).IsEqualTo("пауза: b");
        await Assert.That(editor.DebugStateText).IsEqualTo("на паузе · пауза");
        await Assert.That(editor.CanResume).IsTrue();
    }

    [Test]
    public async Task ABreakpointHitIsDistinguishedFromAnOrdinaryPause()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "c"), RunEvents.Breakpoint(walk, 0, "c"));

        // Состояние парковки то же самое, рисуется иначе — красная пилюля из макета.
        await Assert.That(editor.SelectedRunIsPaused).IsTrue();
        await Assert.That(editor.SelectedRunAtBreakpoint).IsTrue();
        await Assert.That(editor.PauseNotice).IsEqualTo("брейкпоинт: c");
    }

    [Test]
    public async Task ResumingClearsThePausedState()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "c"), RunEvents.Breakpoint(walk, 0, "c"));
        daemon.Push(RunEvents.Resumed(walk, 10, "c"));

        await Assert.That(editor.SelectedRunIsPaused).IsFalse();
        await Assert.That(editor.SelectedRunAtBreakpoint).IsFalse();
        await Assert.That(editor.CanPause).IsTrue();
    }

    [Test]
    public async Task ARejectedCommandSaysTheWalkIsGone_RatherThanLeavingTheButtonLit()
    {
        var daemon = Daemon();
        daemon.Respond(IpcMessageTypes.DebugCommand, new DebugAckDto(Accepted: false, Paused: false, PauseRequested: false));
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"));

        await editor.PauseAsync();

        await Assert.That(editor.StatusMessage).IsEqualTo("Обход уже завершился.");
        await Assert.That(editor.DebugStateText).IsEqualTo("выполняется");
    }

    [Test]
    public async Task StepAndRunToNodeAddressTheSELECTEDWalk()
    {
        var daemon = Daemon();
        daemon.Respond(IpcMessageTypes.DebugCommand, new DebugAckDto(true, true, false));
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"), RunEvents.Paused(walk, 0, "a"));

        editor.SelectedNode = editor.Nodes.Single(n => n.NodeId == "c");
        await editor.StepAsync();
        await editor.RunToCursorAsync();

        var sent = daemon.PayloadsOf<DebugCommandRequest>(IpcMessageTypes.DebugCommand);
        await Assert.That(sent.Select(r => r.Command)).IsEquivalentTo(new[] { DebugCommand.Step, DebugCommand.RunToNode });
        await Assert.That(sent.All(r => r.WalkId == walk.WalkId)).IsTrue();
        // «До курсора» целится в ноду, выбранную на канве, а не в смещение относительно обхода.
        await Assert.That(sent[1].NodeId).IsEqualTo("c");
    }

    [Test]
    public async Task RunToCursorNeedsASelectedNode()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"), RunEvents.Paused(walk, 0, "a"));

        editor.SelectedNode = null;
        await Assert.That(editor.CanRunToCursor).IsFalse();

        editor.SelectedNode = editor.Nodes[2];
        await Assert.That(editor.CanRunToCursor).IsTrue();
    }

    // ---- опасность 3: «Стоп» обязан сказать, что именно он останавливает ---------------------

    [Test]
    public async Task StopCancelsTheRUN_AndTheLabelSaysHowManyWalksThatIs()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);

        var runId = Guid.NewGuid();
        var walks = Enumerable.Range(0, 3)
            .Select(i => RunEvents.Walk("pw-boot", hwnd: 0x100 + i, runId: runId))
            .ToList();
        foreach (var walk in walks)
        {
            daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"));
        }

        // Пауза и шаг работают по ОБХОДУ, а «Стоп» — по ПРОГОНУ, и предложить вместо него отмену
        // одного обхода нечем. Счётчик — это то, чем кнопка признаётся в этой несимметричности.
        await Assert.That(editor.StopLabel).IsEqualTo("■ Стоп ×3");
        await Assert.That(editor.StopTooltip).Contains("все 3");

        await editor.StopSelectedRunAsync();

        var stopped = daemon.PayloadsOf<StopMacroRequest>(IpcMessageTypes.StopMacro).Single();
        await Assert.That(stopped.RunId).IsEqualTo(runId);
    }

    [Test]
    public async Task ASingleWalkStopsWithoutACount()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"));

        await Assert.That(editor.StopLabel).IsEqualTo("■ Стоп");
        await Assert.That(editor.StopTooltip).IsEqualTo("Остановить прогон");
    }

    [Test]
    public async Task FinishedSiblingsAreNotCounted()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var runId = Guid.NewGuid();
        var first = RunEvents.Walk("pw-boot", hwnd: 0x1, runId: runId);
        var second = RunEvents.Walk("pw-boot", hwnd: 0x2, runId: runId);
        daemon.Push(RunEvents.Started(first), RunEvents.Entered(first, 0, "a"));
        daemon.Push(RunEvents.Started(second), RunEvents.Entered(second, 0, "a"));
        daemon.Push(RunEvents.Finished(second, 10, RunOutcomes.Completed));

        // Пообещать остановить два, когда один уже закончился, — это была бы своя маленькая ложь.
        await Assert.That(editor.StopLabel).IsEqualTo("■ Стоп");
    }

    // ---- состояние канвы --------------------------------------------------------------------

    [Test]
    public async Task PassedNodesCarryTheirTimeAndOutcome()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(
            RunEvents.Started(walk),
            RunEvents.Entered(walk, 0, "a"),
            RunEvents.Exited(walk, 100, "a", RunOutcomes.Ok, "100 мс", 1200),
            RunEvents.Entered(walk, 100, "b"));

        var a = editor.Nodes.Single(n => n.NodeId == "a");
        var b = editor.Nodes.Single(n => n.NodeId == "b");

        await Assert.That(a.IsPassed).IsTrue();
        await Assert.That(a.PassedTime).IsEqualTo("1.2 с");
        await Assert.That(a.PassedOutcome).IsEqualTo("ок");
        await Assert.That(a.IsExecuting).IsFalse();
        await Assert.That(b.IsExecuting).IsTrue();
        await Assert.That(b.IsPassed).IsFalse();
    }

    [Test]
    public async Task ARunStampHidesTheTargetsChip_TheyShareOneSlot()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var icon = editor.Nodes.Single(n => n.NodeId == "d");
        icon.Target!.UseSelector = true;
        await Assert.That(icon.ShowsTargetChip).IsTrue();

        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(
            RunEvents.Started(walk),
            RunEvents.Entered(walk, 0, "d"),
            RunEvents.Exited(walk, 5, "d", RunOutcomes.Ok, null, 5));

        // «✓ 2 мс», напечатанное поверх «нет окон», не читалось ни как то, ни как другое, — нашлось
        // при взгляде на живой экран.
        await Assert.That(icon.ShowsRunStamp).IsTrue();
        await Assert.That(icon.ShowsTargetChip).IsFalse();
    }

    [Test]
    public async Task OnlyTheParkedNodeIsMarkedPaused()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "b"), RunEvents.Paused(walk, 0, "b"));

        await Assert.That(editor.Nodes.Count(n => n.IsPaused)).IsEqualTo(1);
        await Assert.That(editor.Nodes.Single(n => n.IsPaused).NodeId).IsEqualTo("b");
    }

    [Test]
    public async Task SwitchingWalksRepaintsTheGraphForTheNewOne()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var first = RunEvents.Walk("pw-boot", hwnd: 0x1);
        var second = RunEvents.Walk("pw-boot", hwnd: 0x2);
        daemon.Push(
            RunEvents.Started(first),
            RunEvents.Entered(first, 0, "a"),
            RunEvents.Exited(first, 5, "a", RunOutcomes.Ok, null, 5),
            RunEvents.Entered(first, 5, "b"));
        daemon.Push(RunEvents.Started(second), RunEvents.Entered(second, 0, "a"));

        editor.SelectedRun = editor.Runs.Single(r => r.WalkId == second.WalkId);

        // Галочки первого обхода не имеют права задерживаться на графе, который следует уже за
        // вторым.
        await Assert.That(editor.Nodes.Single(n => n.NodeId == "a").IsPassed).IsFalse();
        await Assert.That(editor.Nodes.Single(n => n.NodeId == "a").IsExecuting).IsTrue();
    }

    [Test]
    public async Task ProgressCountsNodesEntered_OverTheGraphSize()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"), RunEvents.Entered(walk, 5, "b"));

        await Assert.That(editor.RunProgressText).IsEqualTo("2 / 4");
    }

    [Test]
    public async Task AnUnknownEventKindIsIgnored()
    {
        // Панель постарее своего демона обязана потерять возможность, а не лог. Закреплено потому,
        // что на этом свойстве ветки по умолчанию у трекера держится весь протокол D5.
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(RunEvents.Started(walk), RunEvents.Entered(walk, 0, "a"));
        daemon.Push(new RunEventDto(walk.WalkId, (RunEventKind)999, 10, "a"));

        await Assert.That(editor.Nodes.Single(n => n.NodeId == "a").IsExecuting).IsTrue();
        await Assert.That(editor.RunProgressText).IsEqualTo("1 / 4");
    }

    // ---- точки останова -----------------------------------------------------------------------

    [Test]
    public async Task TickingABreakpointSendsTheWholeSetForThatMacro()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);

        editor.ToggleBreakpoint(editor.Nodes.Single(n => n.NodeId == "b"));
        editor.ToggleBreakpoint(editor.Nodes.Single(n => n.NodeId == "d"));

        var sent = daemon.PayloadsOf<SetBreakpointsRequest>(IpcMessageTypes.SetBreakpoints);
        await Assert.That(sent[^1].MacroName).IsEqualTo("pw-boot");
        await Assert.That(sent[^1].NodeIds).IsEquivalentTo(new[] { "b", "d" });
    }

    [Test]
    public async Task BreakpointsFromTheDaemonAreAppliedWhenTheGraphIsOpened()
    {
        // Демон переживает панель, так что это И ЕСТЬ тот самый случай «мои точки останова на
        // месте» — без файла, без сохранения, без диффа.
        var daemon = Daemon(new BreakpointSetDto("pw-boot", ["c"]));
        var editor = Opened(daemon);

        await Assert.That(editor.Nodes.Single(n => n.NodeId == "c").HasBreakpoint).IsTrue();
        await Assert.That(editor.Nodes.Count(n => n.HasBreakpoint)).IsEqualTo(1);
    }

    [Test]
    public async Task ApplyingTheDaemonsSetDoesNotEchoItStraightBack()
    {
        var daemon = Daemon(new BreakpointSetDto("pw-boot", ["c"]));
        var editor = Opened(daemon);

        await Assert.That(editor.HasBreakpoints).IsTrue();
        await Assert.That(daemon.CountOf(IpcMessageTypes.SetBreakpoints)).IsEqualTo(0);
    }

    [Test]
    public async Task AnotherMacrosBreakpointsAreNotAppliedToThisGraph()
    {
        var daemon = Daemon(new BreakpointSetDto("pw-assist", ["c"]));
        var editor = Opened(daemon);

        await Assert.That(editor.Nodes.Any(n => n.HasBreakpoint)).IsFalse();
    }

    [Test]
    public async Task RenamingANodeCarriesItsBreakpoint()
    {
        var daemon = Daemon(new BreakpointSetDto("pw-boot", ["b"]));
        var editor = Opened(daemon);

        editor.Nodes.Single(n => n.NodeId == "b").NodeId = "задержка";

        // Набор выводится из СТРОК, поэтому всё работает без всякого учёта переименований, — ради
        // этого он из строк и выводится.
        var sent = daemon.PayloadsOf<SetBreakpointsRequest>(IpcMessageTypes.SetBreakpoints);
        await Assert.That(sent[^1].NodeIds).IsEquivalentTo(new[] { "задержка" });
    }

    [Test]
    public async Task DeletingANodeDropsItsBreakpoint()
    {
        var daemon = Daemon(new BreakpointSetDto("pw-boot", ["b", "d"]));
        var editor = Opened(daemon);

        editor.DeleteNode(editor.Nodes.Single(n => n.NodeId == "b"));

        var sent = daemon.PayloadsOf<SetBreakpointsRequest>(IpcMessageTypes.SetBreakpoints);
        await Assert.That(sent[^1].NodeIds).IsEquivalentTo(new[] { "d" });
    }

    [Test]
    public async Task ClearingRemovesThemAll()
    {
        var daemon = Daemon(new BreakpointSetDto("pw-boot", ["b", "d"]));
        var editor = Opened(daemon);

        editor.ClearBreakpoints();

        await Assert.That(editor.HasBreakpoints).IsFalse();
        await Assert.That(daemon.PayloadsOf<SetBreakpointsRequest>(IpcMessageTypes.SetBreakpoints)[^1].NodeIds).IsEmpty();
    }

    // ---- панель переменных ---------------------------------------------------------------

    [Test]
    public async Task TheVariablesPanelIsBuiltFromTheGraphBeforeAnythingHasRun()
    {
        var editor = Opened(Daemon());

        var tag = editor.Variables.Single(v => v.RawName == "tag");
        await Assert.That(tag.KindText).IsEqualTo("строка");
        await Assert.That(tag.WrittenBy).IsEqualTo("c");
        await Assert.That(tag.ReadBy).IsEqualTo("d");
        await Assert.That(tag.ReadWhere).IsEqualTo("в пути к иконке");
        await Assert.That(tag.HasValue).IsFalse();

        var cursor = editor.Variables.Single(v => v.RawName == "cursor");
        await Assert.That(cursor.WrittenBy).IsEqualTo("триггер (сид)");
        await Assert.That(cursor.ReadBy).IsEqualTo("никто");
        await Assert.That(editor.VariableCountText).IsEqualTo("2");
    }

    [Test]
    public async Task AMixedSlotReadDropsTheFieldName_RatherThanNamingTheWrongOne()
    {
        var daemon = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetBreakpoints, Array.Empty<BreakpointSetDto>())
            .Respond(IpcMessageTypes.GetMacros, new[]
            {
                new MacroGraph
                {
                    Name = "смешанное",
                    StartNodeId = "click",
                    Nodes =
                    [
                        new ClickNode { Id = "click", PointVar = "cursor", Next = "tag" },
                        new AddTagNode { Id = "tag", Tag = "проба-{cursor}" },
                    ],
                },
            });
        var editor = CreateEditor(daemon);
        editor.SelectedMacro = editor.Macros.Single();

        var cursor = editor.Variables.Single(v => v.RawName == "cursor");

        // «click, tag · в точке клика» утверждало, будто нода tag читала точку клика. Нашлось при
        // взгляде на живой экран; разбивка по читателям переехала во всплывающую подсказку.
        await Assert.That(cursor.ReadBy).IsEqualTo("click, tag");
        await Assert.That(cursor.ReadWhere).IsNull();
        await Assert.That(cursor.ReadDetail).Contains("click — в точке клика");
        await Assert.That(cursor.ReadDetail).Contains("tag — в теге");
    }

    [Test]
    public async Task LiveValuesComeFromTheSelectedWalk()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var walk = RunEvents.Walk("pw-boot", hwnd: 0x1);
        daemon.Push(
            RunEvents.Started(walk),
            RunEvents.Variable(walk, 0, "cursor", "1804, 902"),
            RunEvents.Entered(walk, 0, "c"),
            RunEvents.Variable(walk, 50, "tag", "Жрец", nodeId: "c"));

        await Assert.That(editor.Variables.Single(v => v.RawName == "tag").Value).IsEqualTo("Жрец");
        await Assert.That(editor.Variables.Single(v => v.RawName == "cursor").Value).IsEqualTo("1804, 902");
    }

    [Test]
    public async Task SwitchingWalksSwitchesTheValues()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        var first = RunEvents.Walk("pw-boot", hwnd: 0x1);
        var second = RunEvents.Walk("pw-boot", hwnd: 0x2);
        daemon.Push(RunEvents.Started(first), RunEvents.Entered(first, 0, "c"), RunEvents.Variable(first, 1, "tag", "Жрец", "c"));
        daemon.Push(RunEvents.Started(second), RunEvents.Entered(second, 0, "c"), RunEvents.Variable(second, 1, "tag", "Лучник", "c"));

        editor.SelectedRun = editor.Runs.Single(r => r.WalkId == first.WalkId);
        await Assert.That(editor.Variables.Single(v => v.RawName == "tag").Value).IsEqualTo("Жрец");

        editor.SelectedRun = editor.Runs.Single(r => r.WalkId == second.WalkId);
        await Assert.That(editor.Variables.Single(v => v.RawName == "tag").Value).IsEqualTo("Лучник");
    }

    [Test]
    public async Task EditingANodeReRunsTheAnalysis()
    {
        var editor = Opened(Daemon());
        await Assert.That(editor.Variables.Any(v => v.RawName == "новая")).IsFalse();

        ((SetIconNodeRowViewModel)editor.Nodes.Single(n => n.NodeId == "d")).IconPath = "icons/{новая}.png";

        // Набранная подстановка обязана добавить читателя ещё до того, как макрос хоть раз
        // запускали.
        var added = editor.Variables.Single(v => v.RawName == "новая");
        await Assert.That(added.ReadBy).IsEqualTo("d");
        await Assert.That(added.IsUndefined).IsTrue();
    }

    [Test]
    public async Task HoveringAVariableLightsItsWriterAndItsReaders()
    {
        var editor = Opened(Daemon());

        editor.HighlightVariable(editor.Variables.Single(v => v.RawName == "tag"));

        await Assert.That(editor.Nodes.Single(n => n.NodeId == "c").IsVariableSource).IsTrue();
        await Assert.That(editor.Nodes.Single(n => n.NodeId == "d").IsVariableConsumer).IsTrue();
        await Assert.That(editor.Nodes.Single(n => n.NodeId == "a").IsVariableSource).IsFalse();
        await Assert.That(editor.VariableLinks).Count().IsEqualTo(1);

        editor.HighlightVariable(null);

        // Это подсказка при наведении, а не часть графа: она не имеет права задерживаться и
        // соперничать с настоящими рёбрами.
        await Assert.That(editor.Nodes.Any(n => n.IsVariableSource || n.IsVariableConsumer)).IsFalse();
        await Assert.That(editor.VariableLinks).IsEmpty();
    }

    [Test]
    public async Task ATriggerSeededVariableDrawsNoLink()
    {
        var editor = Opened(Daemon());

        editor.HighlightVariable(editor.Variables.Single(v => v.RawName == "cursor"));

        // На канве её не пишет ничто, так что и рисовать не от чего — исходного конца нет.
        await Assert.That(editor.VariableLinks).IsEmpty();
    }

    // ---- переподключение ---------------------------------------------------------------------

    [Test]
    public async Task ReconnectingReReadsTheBreakpoints()
    {
        var daemon = Daemon();
        var editor = Opened(daemon);
        await Assert.That(editor.Nodes.Any(n => n.HasBreakpoint)).IsFalse();

        daemon.Respond(IpcMessageTypes.GetBreakpoints, new[] { new BreakpointSetDto("pw-boot", ["b"]) });
        daemon.RaiseConnected();

        // Ни подписки, ни панельная копия набора переподключение не переживают, — а вот собственная
        // копия демона переживает, поэтому точки возвращает именно повторное чтение.
        await Assert.That(editor.Nodes.Single(n => n.NodeId == "b").HasBreakpoint).IsTrue();
    }
}
