using FakeItEasy;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D4, mockup 1f, fourth state: «уже занят pw-immunity».
//
// There are two ways a bound hotkey does nothing, and both land in the same slot because
// they are the same thing from the user's chair:
//   * another macro in the library already claims the chord — answerable in the panel;
//   * Win32 RegisterHotKey refused it because something else owns it system-wide — only the
//     daemon can see that, and it reports it through GetHotkeyFailures.
public class HotkeyConflictTests
{
    private sealed class Daemon
    {
        public Daemon()
        {
            Client = new FakeIpcClient();
            Client.Respond(IpcMessageTypes.GetMacros, _ => Macros.ToArray());
            Client.Respond(IpcMessageTypes.GetRunningMacros, _ => Array.Empty<RunningMacroDto>());
            Client.Respond(IpcMessageTypes.GetWindows, _ => Array.Empty<WindowDto>());
            Client.Respond(IpcMessageTypes.GetHotkeyFailures, _ => Failures.ToArray());
        }

        public FakeIpcClient Client { get; }

        public List<MacroGraph> Macros { get; } = [];

        public List<HotkeyFailureDto> Failures { get; } = [];
    }

    private static MacroGraph Graph(string name, params MacroTrigger[] triggers) => new()
    {
        Name = name,
        Triggers = [.. triggers],
        StartNodeId = "n1",
        Nodes = [new DelayNode { Id = "n1", Ms = 100 }],
    };

    private static MacroEditorViewModel CreateEditor(Daemon daemon, IHotkeySuspension? hotkeys = null) =>
        new(daemon.Client, null, hotkeys, ImmediateUiDispatcher.Instance, @"C:\smartmacro\macros");

    private static HotkeyTriggerRowViewModel Hotkey(MacroEditorViewModel vm) =>
        vm.Triggers.OfType<HotkeyTriggerRowViewModel>().Single();

    // ---- library conflicts -----------------------------------------------------------------

    [Test]
    public async Task ChordOwnedByAnotherMacro_NamesTheOwner()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Macros.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);
        var row = vm.AddTrigger(MacroTriggerKind.Hotkey) as HotkeyTriggerRowViewModel;

        row!.KeyName = "F23";

        await Assert.That(row.Conflict).IsEqualTo("уже занят pw-immunity");
        await Assert.That(row.HasConflict).IsTrue();
    }

    [Test]
    public async Task FreeChord_HasNoConflict()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Macros.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);
        var row = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);

        row.KeyName = "F22";

        await Assert.That(row.Conflict).IsNull();
    }

    // The macro being edited is represented by its ROWS, not by what is on disk — otherwise
    // opening a macro would report every one of its own hotkeys as taken by itself.
    [Test]
    public async Task OwnChordOnDisk_DoesNotConflictWithItself()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.Control, VirtualKey.F23)));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);

        await Assert.That(Hotkey(vm).Conflict).IsNull();
    }

    [Test]
    public async Task SameChordTwiceInOneMacro_IsReportedOnTheSecondRow()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);
        var first = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);
        var second = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);

        first.KeyName = "F13";
        second.KeyName = "F13";

        await Assert.That(first.Conflict).IsNull();
        await Assert.That(second.Conflict).IsEqualTo("уже задан в этом макросе");
    }

    // Two fresh pickers with nothing bound are not a clash; a macro that is half-authored
    // must not light up red for it.
    [Test]
    public async Task UnboundPickers_DoNotClashWithEachOther()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);
        var first = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);
        var second = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);

        await Assert.That(first.Conflict).IsNull();
        await Assert.That(second.Conflict).IsNull();
    }

    [Test]
    public async Task MouseChordAndKeyboardChord_AreNeverTheSameBinding()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("pw-cursor", new HotkeyTrigger(HotkeyModifiers.None, 0, MouseButton.XButton1)));
        daemon.Macros.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);
        var row = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);

        // Same (empty) modifier flags, but a key rather than a button.
        row.KeyName = "F13";
        await Assert.That(row.Conflict).IsNull();

        row.KeyName = string.Empty;
        row.MouseButton = MouseButton.XButton1;
        await Assert.That(row.Conflict).IsEqualTo("уже занят pw-cursor");
    }

    [Test]
    public async Task ConflictAppearsWhenAnotherMacroTakesTheChord()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);
        var row = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);
        row.KeyName = "F23";
        await Assert.That(row.Conflict).IsNull();

        // Someone edits another macro's file and the daemon pushes the change.
        daemon.Macros.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Client.RaiseEvent(IpcMessageTypes.MacrosChanged);

        await Assert.That(row.Conflict).IsEqualTo("уже занят pw-immunity");
    }

    // ---- registration failures ------------------------------------------------------------

    [Test]
    public async Task ChordRefusedByWindows_SaysSoInThePicker()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.Win, VirtualKey.L)));
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.Win, VirtualKey.L));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);

        await Assert.That(Hotkey(vm).Conflict).IsEqualTo("занят другим приложением");
    }

    // When two macros share a chord the daemon registers one and Windows rejects the other,
    // so the failure carries the LOSER's name. The winner must not be told its own key is
    // taken — hence matching on the macro name and not only on the chord.
    [Test]
    public async Task RegistrationFailureOfAnotherMacro_DoesNotAccuseTheWinner()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("first", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Failures.Add(new HotkeyFailureDto("second", HotkeyModifiers.None, VirtualKey.F23));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);

        await Assert.That(Hotkey(vm).Conflict).IsNull();
    }

    // The library clash is the message that names something the user can fix, so it wins
    // even when the daemon ALSO reports the registration failure for this macro.
    [Test]
    public async Task LibraryConflict_TakesPrecedenceOverTheRegistrationFailure()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Macros.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.None, VirtualKey.F23));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);

        await Assert.That(Hotkey(vm).Conflict).IsEqualTo("уже занят pw-immunity");
    }

    // The trap this exists to close: a hotkey that dies at daemon startup, on a macro
    // nobody opens. The library row is the only place it can be noticed.
    [Test]
    public async Task LibraryRow_MarksAMacroWhoseHotkeyNeverRegistered()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.Win, VirtualKey.L)));
        daemon.Macros.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.Win, VirtualKey.L));

        using var vm = CreateEditor(daemon);

        var flagged = vm.Macros.Single(item => item.HasHotkeyProblem);
        await Assert.That(flagged.Name).IsEqualTo("баг-госта");
        await Assert.That(flagged.HotkeyProblem).IsEqualTo(
            "Win+L не зарегистрирован — сочетание занято другим приложением");
        await Assert.That(vm.Macros.Single(item => item.Name == "pw-immunity").HasHotkeyProblem).IsFalse();
    }

    // While «Макросы» is on screen the daemon holds every chord unregistered, so a hotkey
    // bound in the editor is only ever TRIED when the user leaves the mode. Resume is
    // therefore the moment the verdict exists, and the panel has to go and get it.
    [Test]
    public async Task ResumingHotkeys_RefetchesTheFailureList()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.Win, VirtualKey.L)));
        var suspension = A.Fake<IHotkeySuspension>();

        using var vm = CreateEditor(daemon, suspension);
        vm.LoadGraph(daemon.Macros[0]);
        await Assert.That(Hotkey(vm).Conflict).IsNull();

        // The daemon tries the registration on resume and fails.
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.Win, VirtualKey.L));
        await vm.ResumeHotkeysAsync();

        await Assert.That(Hotkey(vm).Conflict).IsEqualTo("занят другим приложением");
        A.CallTo(() => suspension.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task RemovingATriggerRow_ClearsTheClashItCaused()
    {
        var daemon = new Daemon();
        daemon.Macros.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);
        var first = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);
        var second = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);
        first.KeyName = "F13";
        second.KeyName = "F13";
        await Assert.That(second.Conflict).IsNotNull();

        vm.RemoveTrigger(first);

        await Assert.That(second.Conflict).IsNull();
    }
}
