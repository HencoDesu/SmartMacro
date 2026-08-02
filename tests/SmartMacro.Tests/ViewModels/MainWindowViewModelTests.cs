using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// Stage 3: the main window's view-model, now a pure IPC client — windows with tag chips and
// the running-macros panel, all of it seeded by requests and kept current by daemon pushes.
//
// The fake client answers synchronously and ImmediateUiDispatcher runs posted work inline,
// so the constructor's fire-and-forget refresh has already landed by the time a test looks.
public class MainWindowViewModelTests
{
    private const long HwndA = 0x1111;
    private const long HwndB = 0x2222;

    private static WindowDto Window(long hwnd, string process = "elementclient_64", params string[] tags) =>
        new(hwnd, process, tags);

    private static RunningMacroDto Run(Guid id, string name, string? node = null) =>
        new(id, name, DateTimeOffset.UtcNow, node);

    private static MainWindowViewModel CreateVm(FakeIpcClient client) =>
        new(client, ImmediateUiDispatcher.Instance);

    // ---- initial fetch ---------------------------------------------------------------------

    [Test]
    public async Task Construction_OnALiveConnection_FetchesBothSnapshots()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, tags: "Лучник") })
            .Respond(IpcMessageTypes.GetRunningMacros, new[] { Run(Guid.NewGuid(), "pw-boot", "wait-in-world") });

        using var vm = CreateVm(client);

        await Assert.That(client.CountOf(IpcMessageTypes.GetWindows)).IsEqualTo(1);
        await Assert.That(client.CountOf(IpcMessageTypes.GetRunningMacros)).IsEqualTo(1);
        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows[0].ProcessName).IsEqualTo("elementclient_64");
        await Assert.That(vm.Windows[0].HwndHex).IsEqualTo("0x1111");
        await Assert.That(vm.Windows[0].Tags.Select(t => t.Text)).IsEquivalentTo(new[] { "Лучник" });
        await Assert.That(vm.Windows[0].HasTags).IsTrue();
        await Assert.That(vm.Runs[0].MacroName).IsEqualTo("pw-boot");
        await Assert.That(vm.Runs[0].CurrentNodeText).IsEqualTo("wait-in-world");
        await Assert.That(vm.HasRuns).IsTrue();
    }

    [Test]
    public async Task Construction_WhileDisconnected_AsksForNothing()
    {
        var client = new FakeIpcClient { IsConnected = false };

        using var vm = CreateVm(client);

        await Assert.That(client.Requests).IsEmpty();
        await Assert.That(vm.Windows).IsEmpty();
    }

    [Test]
    public async Task Reconnect_RefetchesAndDropsWindowsTheDaemonNoLongerReports()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA), Window(HwndB) });
        using var vm = CreateVm(client);
        await Assert.That(vm.Windows).Count().IsEqualTo(2);

        // While we were away one client closed and another got tagged. A reconnect has no
        // way to replay those pushes, so the fresh snapshot has to win outright.
        client.Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndB, tags: "Жрец") });
        client.RaiseConnected();

        await Assert.That(client.CountOf(IpcMessageTypes.GetWindows)).IsEqualTo(2);
        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows[0].Hwnd).IsEqualTo(HwndB);
        await Assert.That(vm.Windows[0].Tags.Select(t => t.Text)).IsEquivalentTo(new[] { "Жрец" });
    }

    // ---- window events -----------------------------------------------------------------------

    [Test]
    public async Task WindowAppeared_AddsARow()
    {
        var client = new FakeIpcClient();
        using var vm = CreateVm(client);

        client.RaiseEvent(IpcMessageTypes.WindowAppeared, Window(HwndA, "proc"));

        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows[0].HasTags).IsFalse();
        await Assert.That(vm.WindowCountText).Contains("1");
    }

    [Test]
    public async Task WindowTagsChanged_UpdatesTheExistingRowInPlace()
    {
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "proc") });
        using var vm = CreateVm(client);
        var row = vm.Windows.Single();

        client.RaiseEvent(IpcMessageTypes.WindowTagsChanged, Window(HwndA, "proc", "Жрец", "мул"));
        client.RaiseEvent(IpcMessageTypes.WindowTagsChanged, Window(HwndA, "proc", "мул"));

        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows.Single()).IsSameReferenceAs(row);
        await Assert.That(row.Tags.Select(t => t.Text)).IsEquivalentTo(new[] { "мул" });
    }

    [Test]
    public async Task WindowClosed_RemovesTheRow()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "proc"), Window(HwndB, "proc") });
        using var vm = CreateVm(client);

        client.RaiseEvent(IpcMessageTypes.WindowClosed, new WindowClosedEvent(HwndA));

        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows[0].Hwnd).IsEqualTo(HwndB);
    }

    // ---- tagging -------------------------------------------------------------------------------

    [Test]
    public async Task AddTag_SendsAddTag_AndClearsTheInput()
    {
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "proc") });
        using var vm = CreateVm(client);
        var row = vm.Windows.Single();

        row.NewTagText = "  Лучник  ";
        var added = await vm.AddTagAsync(row);

        await Assert.That(added).IsTrue();
        await Assert.That(client.PayloadsOf<AddTagRequest>(IpcMessageTypes.AddTag).Single())
            .IsEqualTo(new AddTagRequest(HwndA, "Лучник"));
        await Assert.That(row.NewTagText).IsEmpty();
    }

    [Test]
    public async Task AddTag_KeepsTheTextWhenTheWindowAlreadyCarriesTheTag()
    {
        // The daemon treats a duplicate as a silent no-op, so only the row can tell the
        // difference — and it must not clear the box as if something happened.
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "proc", "Лучник") });
        using var vm = CreateVm(client);
        var row = vm.Windows.Single();

        row.NewTagText = "Лучник";

        await Assert.That(await vm.AddTagAsync(row)).IsFalse();
        await Assert.That(client.CountOf(IpcMessageTypes.AddTag)).IsEqualTo(0);
        await Assert.That(row.NewTagText).IsEqualTo("Лучник");
    }

    [Test]
    public async Task AddTag_IgnoresBlankInput()
    {
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "proc") });
        using var vm = CreateVm(client);
        var row = vm.Windows.Single();

        row.NewTagText = "   ";

        await Assert.That(await vm.AddTagAsync(row)).IsFalse();
        await Assert.That(client.CountOf(IpcMessageTypes.AddTag)).IsEqualTo(0);
    }

    [Test]
    public async Task AddTag_KeepsTheTextWhenTheDaemonRefuses()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "proc") })
            .Fail(IpcMessageTypes.AddTag, "окна больше нет");
        using var vm = CreateVm(client);
        var row = vm.Windows.Single();

        row.NewTagText = "Шаман";

        await Assert.That(await vm.AddTagAsync(row)).IsFalse();
        await Assert.That(row.NewTagText).IsEqualTo("Шаман");
    }

    [Test]
    public async Task RemovingAChip_SendsRemoveTag()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetWindows, new[] { Window(HwndA, "proc", "Шаман") });
        using var vm = CreateVm(client);

        await vm.RemoveTagAsync(vm.Windows.Single(), "Шаман");

        await Assert.That(client.PayloadsOf<RemoveTagRequest>(IpcMessageTypes.RemoveTag).Single())
            .IsEqualTo(new RemoveTagRequest(HwndA, "Шаман"));
        // The chip stays until the daemon confirms with WindowTagsChanged — the registry is
        // the owner, and an optimistic removal would lie when the call fails.
        await Assert.That(vm.Windows.Single().Tags).Count().IsEqualTo(1);

        client.RaiseEvent(IpcMessageTypes.WindowTagsChanged, Window(HwndA, "proc"));
        await Assert.That(vm.Windows.Single().Tags).IsEmpty();
        await Assert.That(vm.Windows.Single().HasTags).IsFalse();
    }

    // ---- running macros ------------------------------------------------------------------------

    [Test]
    public async Task RunningMacrosChanged_AddsAndRemovesRows()
    {
        var client = new FakeIpcClient();
        using var vm = CreateVm(client);

        await Assert.That(vm.Runs).IsEmpty();
        await Assert.That(vm.HasRuns).IsFalse();

        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, new[] { Run(Guid.NewGuid(), "pw-assist") });
        await Assert.That(vm.Runs).Count().IsEqualTo(1);

        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, Array.Empty<RunningMacroDto>());
        await Assert.That(vm.Runs).IsEmpty();
        await Assert.That(vm.HasRuns).IsFalse();
    }

    [Test]
    public async Task SurvivingRuns_KeepTheirRowAcrossARefresh()
    {
        var first = Run(Guid.NewGuid(), "первый");
        var second = Run(Guid.NewGuid(), "второй");
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetRunningMacros, new[] { first });
        using var vm = CreateVm(client);
        var row = vm.Runs.Single();

        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, new[] { first, second });
        await Assert.That(vm.Runs).Count().IsEqualTo(2);
        await Assert.That(vm.Runs.Single(r => r.RunId == first.RunId)).IsSameReferenceAs(row);

        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, new[] { first });
        await Assert.That(vm.Runs.Single()).IsSameReferenceAs(row);
    }

    [Test]
    public async Task StopRun_SendsStopMacroForThatRunOnly()
    {
        var first = Run(Guid.NewGuid(), "первый");
        var second = Run(Guid.NewGuid(), "второй");
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetRunningMacros, new[] { first, second });
        using var vm = CreateVm(client);

        await vm.StopRunAsync(vm.Runs.Single(r => r.RunId == first.RunId));

        await Assert.That(client.PayloadsOf<StopMacroRequest>(IpcMessageTypes.StopMacro).Select(p => p.RunId))
            .IsEquivalentTo(new[] { first.RunId });
    }

    [Test]
    public async Task StopAllRuns_SendsStopMacroForEveryRun()
    {
        var first = Run(Guid.NewGuid(), "первый");
        var second = Run(Guid.NewGuid(), "второй");
        var client = new FakeIpcClient().Respond(IpcMessageTypes.GetRunningMacros, new[] { first, second });
        using var vm = CreateVm(client);

        await vm.StopAllRunsAsync();

        await Assert.That(client.PayloadsOf<StopMacroRequest>(IpcMessageTypes.StopMacro).Select(p => p.RunId))
            .IsEquivalentTo(new[] { first.RunId, second.RunId });
    }

    [Test]
    public async Task Elapsed_IsRenderedFromTheRunStart()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetRunningMacros, new[] { Run(Guid.NewGuid(), "долгий") });
        using var vm = CreateVm(client);
        var row = vm.Runs.Single();

        row.Refresh(row.StartedUtc.AddSeconds(65));
        await Assert.That(row.Elapsed).IsEqualTo("1:05");

        row.Refresh(row.StartedUtc.AddHours(1).AddMinutes(2).AddSeconds(3));
        await Assert.That(row.Elapsed).IsEqualTo("1:02:03");

        // A clock that has gone backwards must not render a negative duration.
        row.Refresh(row.StartedUtc.AddSeconds(-30));
        await Assert.That(row.Elapsed).IsEqualTo("0:00");
    }

    [Test]
    public async Task RefreshElapsed_TicksEveryRow()
    {
        var client = new FakeIpcClient()
            .Respond(IpcMessageTypes.GetRunningMacros, new[] { Run(Guid.NewGuid(), "тик") });
        using var vm = CreateVm(client);

        vm.RefreshElapsed();

        await Assert.That(vm.Runs.Single().Elapsed).IsEqualTo("0:00");
    }

    [Test]
    public async Task Dispose_StopsListeningToTheDaemon()
    {
        var client = new FakeIpcClient();
        var vm = CreateVm(client);
        vm.Dispose();

        client.RaiseEvent(IpcMessageTypes.WindowAppeared, Window(HwndA, "proc"));
        client.RaiseEvent(IpcMessageTypes.RunningMacrosChanged, new[] { Run(Guid.NewGuid(), "после-dispose") });
        client.RaiseConnected();

        await Assert.That(vm.Windows).IsEmpty();
        await Assert.That(vm.Runs).IsEmpty();
    }
}
