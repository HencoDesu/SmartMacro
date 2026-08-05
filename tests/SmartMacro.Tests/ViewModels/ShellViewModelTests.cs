using FakeItEasy;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D2: оболочка 1b. Всё здесь — это выведение значений и очерчивание области действия: своих
// данных у оболочки нет, поэтому проверять стоит вот что — цифры на боковой панели следуют за
// коллекциями, сводка по тегам следует за пушем WindowTagsChanged без повторного запроса, у
// панели прогонов два честных состояния, а глобальные хоткеи выключаются и включаются обратно в
// нужные моменты (ошибись ИМЕННО ТУТ — и все хоткеи приложения молча умрут).
//
// Один FakeIpcClient обслуживает view-model обоих режимов — ровно так же, как это делает одно
// настоящее соединение.
public class ShellViewModelTests
{
    private const long HwndA = 0x1111;
    private const long HwndB = 0x2222;
    private const long HwndC = 0x3333;

    private static WindowDto Window(long hwnd, params string[] tags) =>
        new(hwnd, "elementclient_64", tags);

    private static RunningMacroDto Run(Guid id, string name, string? node = null) =>
        new(id, name, DateTimeOffset.UtcNow, node);

    private static MacroGraph Macro(string name) => new()
    {
        Name = name,
        StartNodeId = Ids.Of("n1"),
        Nodes = [new DelayNode { Id = Ids.Of("n1"), DisplayName = "n1", Ms = 1 }],
    };

    private static ShellViewModel CreateShell(
        FakeIpcClient client,
        IMacroLauncher? launcher = null,
        IHotkeySuspension? hotkeys = null,
        TempLibrary? library = null) =>
        new(
            new WorkspaceViewModel(client, ImmediateUiDispatcher.Instance),
            new MacroEditorViewModel(client, library?.Library ?? TempLibrary.Shared, launcher, hotkeys,
                ImmediateUiDispatcher.Instance),
            new LogViewModel(client, ImmediateUiDispatcher.Instance),
            new SettingsViewModel(client, ImmediateUiDispatcher.Instance),
            launcher);

    private static LogEntryDto Entry(long seq, LogLevelDto level, string message = "строка") =>
        new(seq, DateTimeOffset.UtcNow, level, "SmartMacro.Windows.WindowRegistry", message, null);

    private static ShellModeViewModel Row(ShellViewModel shell, ShellMode mode) =>
        shell.Modes.Single(row => row.Mode == mode);

    // ---- режимы -----------------------------------------------------------------------------

    [Test]
    public async Task Shell_OpensOnWindows_WithTheRailInMockupOrder()
    {
        using var shell = CreateShell(new FakeIpcClient());

        await Assert.That(shell.Modes.Select(m => m.Title))
            .IsEquivalentTo(new[] { "Окна", "Макросы", "Прогоны", "Лог" });
        await Assert.That(shell.CurrentMode).IsEqualTo(ShellMode.Windows);
        await Assert.That(shell.IsWindowsMode).IsTrue();
        await Assert.That(Row(shell, ShellMode.Windows).IsSelected).IsTrue();
    }

    [Test]
    public async Task SelectMode_MovesTheSelectionAndTheVisibleBody()
    {
        using var shell = CreateShell(new FakeIpcClient());

        shell.SelectMode(ShellMode.Runs);

        await Assert.That(shell.CurrentMode).IsEqualTo(ShellMode.Runs);
        await Assert.That(shell.IsRunsMode).IsTrue();
        await Assert.That(shell.IsWindowsMode).IsFalse();
        await Assert.That(Row(shell, ShellMode.Runs).IsSelected).IsTrue();
        await Assert.That(Row(shell, ShellMode.Windows).IsSelected).IsFalse();
    }

    [Test]
    public async Task SelectedMode_IgnoresTheNullATransientListBoxPushes()
    {
        using var shell = CreateShell(new FakeIpcClient());
        shell.SelectMode(ShellMode.Log);

        shell.SelectedMode = null!;

        // Оболочка без режима нарисовала бы пустую рабочую область, так что последний остаётся.
        await Assert.That(shell.CurrentMode).IsEqualTo(ShellMode.Log);
    }

    // ---- счётчики ---------------------------------------------------------------------------

    [Test]
    public async Task Counters_FollowTheCollectionsTheyCountFrom()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "Лучник"), Window(HwndB) })
            .Respond(IpcMessageTypes.GetRunningMacros, Array.Empty<RunningMacroDto>());
        // Счётчик «Макросов» с волны F3 считает ФАЙЛЫ в папке, а не ответ демона.
        using var library = new TempLibrary();
        library.WriteExternally(Macro("pw-boot"));
        library.WriteExternally(Macro("pw-assist"));

        using var shell = CreateShell(client, library: library);

        await Assert.That(Row(shell, ShellMode.Windows).CounterText).IsEqualTo("2");
        await Assert.That(Row(shell, ShellMode.Macros).CounterText).IsEqualTo("2");
        await Assert.That(Row(shell, ShellMode.Runs).CounterText).IsEqualTo("0");

        client.RaiseEvent(IpcMessageTypes.WindowAppeared, Window(HwndC));
        await Assert.That(Row(shell, ShellMode.Windows).CounterText).IsEqualTo("3");
    }

    [Test]
    public async Task LogCounter_CountsProblems_NotEntries()
    {
        // Единственный счётчик рейки, который считает не все свои строки. Число записей упёрлось
        // бы в потолок ленты и стало константой; «сколько раз что-то пошло не так» — сообщение.
        // Раньше на этом месте стоял тест, придерживавший пустоту: за «Логом» не было ни одного
        // сообщения IPC. Теперь есть SubscribeLog, и счётчик обязан быть настоящим.
        var client = new FakeIpcClient().Respond(IpcMessageTypes.SubscribeLog, new[]
        {
            Entry(1, LogLevelDto.Information),
            Entry(2, LogLevelDto.Debug),
            Entry(3, LogLevelDto.Warning),
            Entry(4, LogLevelDto.Error),
        });

        using var shell = CreateShell(client);

        await Assert.That(Row(shell, ShellMode.Log).HasCounter).IsTrue();
        await Assert.That(Row(shell, ShellMode.Log).CounterText).IsEqualTo("2");
    }

    [Test]
    public async Task LogCounter_IsZeroRatherThanBlank_WhenNothingHasGoneWrong()
    {
        // Ноль — это ответ демона («в ленте проблем нет»), а пустота была бы признанием, что
        // спросить не у кого. Спросить теперь есть у кого.
        using var shell = CreateShell(new FakeIpcClient());

        await Assert.That(Row(shell, ShellMode.Log).HasCounter).IsTrue();
        await Assert.That(Row(shell, ShellMode.Log).CounterText).IsEqualTo("0");
    }

    [Test]
    public async Task LogCounter_StaysLive_WhileAnotherModeIsOnScreen()
    {
        // Ровно то, ради чего лента подписана на всю жизнь панели, а не на время своего режима:
        // сигнал «что-то пошло не так» задают из любого места, и счётчик, который обновляется,
        // только когда на него смотрят, — не сигнал.
        var client = new FakeIpcClient();
        using var shell = CreateShell(client);
        await Assert.That(shell.IsWindowsMode).IsTrue();

        client.RaiseEvent(
            IpcMessageTypes.LogEntries,
            new LogEntryBatch([Entry(1, LogLevelDto.Error, "нода упала")], 0));

        await Assert.That(Row(shell, ShellMode.Log).CounterText).IsEqualTo("1");
        await Assert.That(shell.IsWindowsMode).IsTrue();
    }

    [Test]
    public async Task Log_IsSubscribedOnce_AtStartup_AndModeChangesDoNotToggleIt()
    {
        // В отличие от событий прогона, которые ложатся и поднимаются вместе с «Макросами».
        var client = new FakeIpcClient();
        using var shell = CreateShell(client);

        await Assert.That(client.CountOf(IpcMessageTypes.SubscribeLog)).IsEqualTo(1);
        await Assert.That(client.PayloadsOf<SubscribeLogRequest>(IpcMessageTypes.SubscribeLog).Single().Enabled)
            .IsTrue();

        shell.SelectMode(ShellMode.Log);
        shell.SelectMode(ShellMode.Windows);

        await Assert.That(client.CountOf(IpcMessageTypes.SubscribeLog)).IsEqualTo(1);
    }

    // Строки «Шаблоны» на рейке больше нет (F2), и три теста про её счётчик ушли вместе с ней:
    // числа «сколько всего шаблонов» не существует — общего дерева нет, есть только «сколько их
    // у ЭТОГО макроса», и это число живёт в инспекторе редактора.
    [Test]
    public async Task Rail_HasNoTemplatesRow_BecauseATemplateIsNotAnEntityAnyMore()
    {
        using var shell = CreateShell(new FakeIpcClient());

        await Assert.That(shell.Modes.Select(row => row.Mode).ToList()).IsEquivalentTo(new List<ShellMode>
        {
            ShellMode.Windows,
            ShellMode.Macros,
            ShellMode.Runs,
            ShellMode.Log,
        });
        await Assert.That(shell.Modes.Select(row => row.Title)).DoesNotContain("Шаблоны");
    }

    // Ни один режим, кроме «Макросов», не спрашивает шаблоны: браузер наводится на ОТКРЫТЫЙ
    // макрос, а не на вход в режим.
    [Test]
    public async Task SwitchingModes_DoesNotAskForTemplates()
    {
        var client = new FakeIpcClient();
        using var shell = CreateShell(client);

        shell.SelectMode(ShellMode.Runs);
        shell.SelectMode(ShellMode.Log);
        shell.SelectMode(ShellMode.Windows);

        // С волны F3 шаблоны вообще не ходят по трубе, поэтому утверждение стало сильнее:
        // переключение режимов не шлёт НИЧЕГО, кроме того, что принадлежит демону.
        await Assert.That(client.Requests.Select(r => r.Type).Distinct().Order().ToList())
            .DoesNotContain("GetTemplates");
    }

    [Test]
    public async Task RunsCounter_CarriesTheActivityDot_AndStaysAccentFromAnotherMode()
    {
        var client = new FakeIpcClient();
        using var shell = CreateShell(client);
        var runs = Row(shell, ShellMode.Runs);

        await Assert.That(runs.ShowsActivityDot).IsFalse();
        await Assert.That(runs.CounterIsAccent).IsFalse();

        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, new[] { Run(Guid.NewGuid(), "pw-boot") });

        await Assert.That(runs.CounterText).IsEqualTo("1");
        await Assert.That(runs.ShowsActivityDot).IsTrue();
        await Assert.That(runs.CounterIsAccent).IsTrue();
    }

    [Test]
    public async Task SelectedRow_RendersItsCounterInAccent()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Macro("pw-boot"));
        using var shell = CreateShell(new FakeIpcClient(), library: library);

        await Assert.That(Row(shell, ShellMode.Macros).CounterIsAccent).IsFalse();
        shell.SelectMode(ShellMode.Macros);
        await Assert.That(Row(shell, ShellMode.Macros).CounterIsAccent).IsTrue();
    }

    // ---- сводка по тегам -----------------------------------------------------------------

    [Test]
    public async Task TagSummary_AggregatesTheRoster_BusiestFirst()
    {
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetWindows, new[]
        {
            Window(0x10, "Лучник", "Мастер"),
            Window(0x20, "Жрец"),
            Window(0x30, "Лучник"),
            Window(0x40),
        });

        using var shell = CreateShell(client);

        await Assert.That(shell.HasTagSummary).IsTrue();
        await Assert.That(shell.TagSummary.Select(t => $"{t.Tag} {t.CountText}"))
            .IsEquivalentTo(new[] { "Лучник 2", "Жрец 1", "Мастер 1" });
        // «Самые многолюдные сверху» — это ПОРЯДОК, а не набор: боковая панель показывает форму
        // партии.
        await Assert.That(shell.TagSummary[0].Tag).IsEqualTo("Лучник");
    }

    [Test]
    public async Task TagSummary_UpdatesOnATagsChangedPush_WithoutRefetching()
    {
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA) });
        using var shell = CreateShell(client);
        var fetchesSoFar = client.CountOf(IpcMessageTypes.GetWindows);

        await Assert.That(shell.HasTagSummary).IsFalse();

        client.RaiseEvent(IpcMessageTypes.WindowTagsChanged, Window(HwndA, "Шаман"));

        await Assert.That(shell.TagSummary.Select(t => t.Tag)).IsEquivalentTo(new[] { "Шаман" });
        await Assert.That(shell.TagSummary[0].Count).IsEqualTo(1);
        await Assert.That(client.CountOf(IpcMessageTypes.GetWindows)).IsEqualTo(fetchesSoFar);
    }

    [Test]
    public async Task TagSummary_ForgetsATagWhenItsLastWindowCloses()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "Склад") });
        using var shell = CreateShell(client);
        await Assert.That(shell.TagSummary).Count().IsEqualTo(1);

        client.RaiseEvent(IpcMessageTypes.WindowClosed, new WindowClosedEvent(HwndA));

        await Assert.That(shell.TagSummary).IsEmpty();
        await Assert.That(shell.HasTagSummary).IsFalse();
    }

    // ---- панель прогонов ---------------------------------------------------------------------

    [Test]
    public async Task RunBar_IsIdleWithNothingRunning()
    {
        using var shell = CreateShell(new FakeIpcClient());

        await Assert.That(shell.HasRuns).IsFalse();
        await Assert.That(shell.PrimaryRun).IsNull();
        await Assert.That(shell.HasOtherRuns).IsFalse();
        await Assert.That(shell.IdleText).IsNotEmpty();
    }

    [Test]
    public async Task RunBar_NamesTheFirstRun_AndCountsTheRest()
    {
        var client = new FakeIpcClient();
        using var shell = CreateShell(client);

        client.RaiseEvent(
            IpcMessageTypes.RunningMacrosChanged,
            new[] { Run(Guid.NewGuid(), "pw-boot", "wait-in-world"), Run(Guid.NewGuid(), "pw-assist") });

        await Assert.That(shell.HasRuns).IsTrue();
        await Assert.That(shell.PrimaryRun!.MacroName).IsEqualTo("pw-boot");
        await Assert.That(shell.PrimaryRun.CurrentNodeText).IsEqualTo("wait-in-world");
        await Assert.That(shell.HasOtherRuns).IsTrue();
        await Assert.That(shell.OtherRunsText).IsEqualTo("+1");

        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, Array.Empty<RunningMacroDto>());

        await Assert.That(shell.HasRuns).IsFalse();
        await Assert.That(shell.PrimaryRun).IsNull();
        await Assert.That(shell.OtherRunsText).IsEmpty();
    }

    // ---- «Опознать все» --------------------------------------------------------------------

    [Test]
    public async Task IdentifyAll_RunsPwIdentify_OnlyWhenTheLibraryHasIt()
    {
        var launcher = A.Fake<IMacroLauncher>();
        var client = new FakeIpcClient();
        using var library = new TempLibrary();
        library.WriteExternally(Macro("pw-boot"));
        using var shell = CreateShell(client, launcher, library: library);

        await Assert.That(shell.CanIdentifyAll).IsFalse();
        shell.IdentifyAll();
        A.CallTo(() => launcher.RunMacro(A<string>._)).MustNotHaveHappened();

        // Макрос появляется в папке — этого достаточно, демон здесь ни при чём.
        library.WriteExternally(Macro(ShellViewModel.IdentifyMacroName));

        await Assert.That(shell.CanIdentifyAll).IsTrue();
        shell.IdentifyAll();
        A.CallTo(() => launcher.RunMacro(ShellViewModel.IdentifyMacroName)).MustHaveHappenedOnceExactly();
    }

    // ---- область действия хоткеев --------------------------------------------------------------

    [Test]
    public async Task Hotkeys_AreSuspendedWhileMacrosIsOnScreen_AndResumedOnTheWayOut()
    {
        var hotkeys = A.Fake<IHotkeySuspension>();
        using var shell = CreateShell(new FakeIpcClient(), hotkeys: hotkeys);

        await Assert.That(shell.HotkeysSuspended).IsFalse();
        A.CallTo(() => hotkeys.SuspendAsync(A<CancellationToken>._)).MustNotHaveHappened();

        shell.SelectMode(ShellMode.Macros);
        await Assert.That(shell.HotkeysSuspended).IsTrue();
        A.CallTo(() => hotkeys.SuspendAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        shell.SelectMode(ShellMode.Windows);
        await Assert.That(shell.HotkeysSuspended).IsFalse();
        A.CallTo(() => hotkeys.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task Hotkeys_AreNotToggledByModeChangesThatDoNotTouchTheEditor()
    {
        var hotkeys = A.Fake<IHotkeySuspension>();
        using var shell = CreateShell(new FakeIpcClient(), hotkeys: hotkeys);

        shell.SelectMode(ShellMode.Runs);
        shell.SelectMode(ShellMode.Log);

        await Assert.That(shell.HotkeysSuspended).IsFalse();
        A.CallTo(() => hotkeys.SuspendAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => hotkeys.ResumeAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task ResumeHotkeysIfSuspended_IsTheExitPath_AndIsIdempotent()
    {
        // При отключении клиента демон заново ничего не регистрирует, так что закрытие окна
        // изнутри «Макросов» обязано вернуть хоткеи на место — иначе они так и останутся мёртвыми.
        var hotkeys = A.Fake<IHotkeySuspension>();
        using var shell = CreateShell(new FakeIpcClient(), hotkeys: hotkeys);
        shell.SelectMode(ShellMode.Macros);

        await shell.ResumeHotkeysIfSuspendedAsync();
        await shell.ResumeHotkeysIfSuspendedAsync();

        await Assert.That(shell.HotkeysSuspended).IsFalse();
        A.CallTo(() => hotkeys.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task ResumeHotkeysIfSuspended_DoesNothingWhenNothingWasSuspended()
    {
        var hotkeys = A.Fake<IHotkeySuspension>();
        using var shell = CreateShell(new FakeIpcClient(), hotkeys: hotkeys);

        await shell.ResumeHotkeysIfSuspendedAsync();

        A.CallTo(() => hotkeys.ResumeAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    // ---- разборка ---------------------------------------------------------------------------------

    [Test]
    public async Task Dispose_UnsubscribesBothModeViewModels()
    {
        var client = new FakeIpcClient();
        var shell = CreateShell(client);
        shell.Dispose();

        client.RaiseEvent(IpcMessageTypes.WindowAppeared, Window(HwndA));
        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, new[] { Run(Guid.NewGuid(), "после-dispose") });

        await Assert.That(shell.Workspace.Windows).IsEmpty();
        await Assert.That(shell.Workspace.Runs).IsEmpty();
        await Assert.That(shell.HasRuns).IsFalse();
    }
}
