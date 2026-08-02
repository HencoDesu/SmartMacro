using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Macros.Execution;
using SmartMacro.Windows;

namespace SmartMacro.Tests.ViewModels;

// W0.3: the main window's view-model — windows with tag chips, and the running-macros
// panel. Both registries are the real (logger-only) implementations; the VM's Avalonia
// dependency is confined to IUiDispatcher, which the tests replace with an inline one so
// registry events are observable without a message pump.
public class MainWindowViewModelTests
{
    private static readonly IntPtr HwndA = new(0x1111);
    private static readonly IntPtr HwndB = new(0x2222);

    private static WindowRegistry CreateRegistry() => new(NullLogger<WindowRegistry>.Instance);

    private static MacroRunRegistry CreateRuns() => new(NullLogger<MacroRunRegistry>.Instance);

    private static MainWindowViewModel CreateVm(WindowRegistry registry, MacroRunRegistry runs) =>
        new(registry, runs, ImmediateUiDispatcher.Instance);

    // ---- windows ------------------------------------------------------------------------

    [Test]
    public async Task ExistingWindows_AreListedAtConstruction()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        registry.Register(HwndA, "elementclient_64");
        registry.AddTag(HwndA, "Лучник");

        using var vm = CreateVm(registry, runs);

        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows[0].ProcessName).IsEqualTo("elementclient_64");
        await Assert.That(vm.Windows[0].HwndHex).IsEqualTo("0x1111");
        await Assert.That(vm.Windows[0].Tags.Select(t => t.Text)).IsEquivalentTo(new[] { "Лучник" });
        await Assert.That(vm.Windows[0].HasTags).IsTrue();
    }

    [Test]
    public async Task WindowAppeared_AddsARow()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        using var vm = CreateVm(registry, runs);

        registry.Register(HwndA, "proc");

        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows[0].HasTags).IsFalse();
        await Assert.That(vm.WindowCountText).Contains("1");
    }

    [Test]
    public async Task WindowTagsChanged_UpdatesTheExistingRowInPlace()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        registry.Register(HwndA, "proc");
        using var vm = CreateVm(registry, runs);
        var row = vm.Windows.Single();

        registry.AddTag(HwndA, "Жрец");
        registry.AddTag(HwndA, "мул");
        registry.RemoveTag(HwndA, "Жрец");

        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows.Single()).IsSameReferenceAs(row);
        await Assert.That(row.Tags.Select(t => t.Text)).IsEquivalentTo(new[] { "мул" });
    }

    [Test]
    public async Task WindowClosed_RemovesTheRow()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        registry.Register(HwndA, "proc");
        registry.Register(HwndB, "proc");
        using var vm = CreateVm(registry, runs);

        registry.Unregister(HwndA);

        await Assert.That(vm.Windows).Count().IsEqualTo(1);
        await Assert.That(vm.Windows[0].Hwnd).IsEqualTo(HwndB);
    }

    [Test]
    public async Task AddTag_GoesThroughTheRegistry_AndClearsTheInputOnSuccess()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        registry.Register(HwndA, "proc");
        using var vm = CreateVm(registry, runs);
        var row = vm.Windows.Single();

        row.NewTagText = "  Лучник  ";
        var added = vm.AddTag(row);

        await Assert.That(added).IsTrue();
        await Assert.That(registry.HasTag(HwndA, "Лучник")).IsTrue();
        await Assert.That(row.NewTagText).IsEmpty();
        await Assert.That(row.Tags.Select(t => t.Text)).IsEquivalentTo(new[] { "Лучник" });
    }

    [Test]
    public async Task AddTag_KeepsTheTextWhenTheTagDoesNotLand()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        registry.Register(HwndA, "proc");
        registry.AddTag(HwndA, "Лучник");
        using var vm = CreateVm(registry, runs);
        var row = vm.Windows.Single();

        row.NewTagText = "Лучник";
        var added = vm.AddTag(row);

        await Assert.That(added).IsFalse();
        await Assert.That(row.NewTagText).IsEqualTo("Лучник");
    }

    [Test]
    public async Task AddTag_IgnoresBlankInput()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        registry.Register(HwndA, "proc");
        using var vm = CreateVm(registry, runs);
        var row = vm.Windows.Single();

        row.NewTagText = "   ";

        await Assert.That(vm.AddTag(row)).IsFalse();
        await Assert.That(registry.GetTags(HwndA)).IsEmpty();
    }

    [Test]
    public async Task RemovingAChip_GoesThroughTheRegistry()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        registry.Register(HwndA, "proc");
        registry.AddTag(HwndA, "Шаман");
        using var vm = CreateVm(registry, runs);

        vm.Windows.Single().Tags.Single().Remove();

        await Assert.That(registry.HasTag(HwndA, "Шаман")).IsFalse();
        await Assert.That(vm.Windows.Single().Tags).IsEmpty();
        await Assert.That(vm.Windows.Single().HasTags).IsFalse();
    }

    // ---- running macros --------------------------------------------------------------------

    [Test]
    public async Task RunsAlreadyTracked_AreRenderedAtConstruction()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        var handle = runs.TryBegin("pw-boot")!;
        handle.CurrentNodeId = "wait-in-world";

        using var vm = CreateVm(registry, runs);

        await Assert.That(vm.Runs).Count().IsEqualTo(1);
        await Assert.That(vm.Runs[0].MacroName).IsEqualTo("pw-boot");
        await Assert.That(vm.Runs[0].CurrentNodeText).IsEqualTo("wait-in-world");
        await Assert.That(vm.HasRuns).IsTrue();
    }

    [Test]
    public async Task RunsChanged_AddsAndRemovesRows()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        using var vm = CreateVm(registry, runs);

        await Assert.That(vm.Runs).IsEmpty();
        await Assert.That(vm.HasRuns).IsFalse();

        var handle = runs.TryBegin("pw-assist")!;
        await Assert.That(vm.Runs).Count().IsEqualTo(1);

        runs.Complete(handle.RunId);
        await Assert.That(vm.Runs).IsEmpty();
        await Assert.That(vm.HasRuns).IsFalse();
    }

    [Test]
    public async Task SurvivingRuns_KeepTheirRowAcrossARefresh()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        using var vm = CreateVm(registry, runs);
        var first = runs.TryBegin("первый")!;
        var row = vm.Runs.Single();

        var second = runs.TryBegin("второй")!;

        await Assert.That(vm.Runs).Count().IsEqualTo(2);
        await Assert.That(vm.Runs.Single(r => r.RunId == first.RunId)).IsSameReferenceAs(row);

        runs.Complete(second.RunId);
        await Assert.That(vm.Runs.Single()).IsSameReferenceAs(row);
    }

    [Test]
    public async Task StopRun_CancelsThatRunOnly()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        using var vm = CreateVm(registry, runs);
        var first = runs.TryBegin("первый")!;
        var second = runs.TryBegin("второй")!;

        vm.StopRun(vm.Runs.Single(r => r.RunId == first.RunId));

        await Assert.That(first.Token.IsCancellationRequested).IsTrue();
        await Assert.That(second.Token.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task StopAllRuns_CancelsEverything()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        using var vm = CreateVm(registry, runs);
        var first = runs.TryBegin("первый")!;
        var second = runs.TryBegin("второй")!;

        vm.StopAllRuns();

        await Assert.That(first.Token.IsCancellationRequested).IsTrue();
        await Assert.That(second.Token.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task Elapsed_IsRenderedFromTheRunStart()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        using var vm = CreateVm(registry, runs);
        runs.TryBegin("долгий");
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
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        using var vm = CreateVm(registry, runs);
        runs.TryBegin("тик");

        vm.RefreshElapsed();

        await Assert.That(vm.Runs.Single().Elapsed).IsEqualTo("0:00");
    }

    [Test]
    public async Task Dispose_StopsTrackingBothRegistries()
    {
        var registry = CreateRegistry();
        using var runs = CreateRuns();
        var vm = CreateVm(registry, runs);
        vm.Dispose();

        registry.Register(HwndA, "proc");
        runs.TryBegin("после-dispose");

        await Assert.That(vm.Windows).IsEmpty();
        await Assert.That(vm.Runs).IsEmpty();
    }
}
