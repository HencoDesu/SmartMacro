using FakeItEasy;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D4, макет 1f, четвёртое состояние: «уже занят pw-immunity».
//
// Привязанный хоткей может не делать ничего по двум причинам, и обе попадают в одно и то же
// место, потому что со стула пользователя это одно и то же:
//   * аккорд уже застолбил другой макрос в библиотеке — на это панель отвечает сама;
//   * Win32 RegisterHotKey отказал, потому что аккордом владеет что-то ещё на уровне всей
//     системы, — увидеть это способен только демон, и он сообщает об этом через
//     GetHotkeyFailures.
public class HotkeyConflictTests
{
    // С волны F3 «демон» здесь распался надвое, и это ровно то, что проверяет этот файл:
    // библиотека — это папка, а список сочетаний, в которых отказала Windows, — это всё, что осталось от
    // демона.
    private sealed class Daemon : IDisposable
    {
        public Daemon()
        {
            Client = new FakeIpcClient();
            Client.Respond(IpcMessageTypes.GetRunningMacros, _ => Array.Empty<RunningMacroDto>());
            Client.Respond(IpcMessageTypes.GetWindows, _ => Array.Empty<WindowDto>());
            Client.Respond(IpcMessageTypes.GetHotkeyFailures, _ => Failures.ToArray());
        }

        public FakeIpcClient Client { get; }

        public TempLibrary Library { get; } = new();

        public List<HotkeyFailureDto> Failures { get; } = [];

        /// <summary>Графы в порядке добавления — так тесты открывают нужный из них.</summary>
        public List<MacroGraph> Macros { get; } = [];

        /// <summary>Кладёт макрос в папку и перечитывает снимок.</summary>
        public void Add(MacroGraph graph)
        {
            Macros.Add(graph);
            Library.WriteExternally(graph);
        }

        public void Dispose() => Library.Dispose();
    }

    private static MacroGraph Graph(string name, params MacroTrigger[] triggers) => new()
    {
        Name = name,
        Triggers = [.. triggers],
        StartNodeId = Ids.Of("n1"),
        Nodes = [new DelayNode { Id = Ids.Of("n1"), DisplayName = "n1", Ms = 100 }],
    };

    private static MacroEditorViewModel CreateEditor(Daemon daemon, IHotkeySuspension? hotkeys = null) =>
        new(daemon.Client, daemon.Library.Library, null, hotkeys, ImmediateUiDispatcher.Instance);

    private static HotkeyTriggerRowViewModel Hotkey(MacroEditorViewModel vm) =>
        vm.Triggers.OfType<HotkeyTriggerRowViewModel>().Single();

    // ---- конфликты внутри библиотеки ---------------------------------------------------------

    [Test]
    public async Task ChordOwnedByAnotherMacro_NamesTheOwner()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);
        var row = vm.AddTrigger(MacroTriggerKind.Hotkey) as HotkeyTriggerRowViewModel;

        row!.KeyName = "F23";

        // Столкновение В БИБЛИОТЕКЕ называет виновника — по имени его и искать. Отказ Windows
        // выглядел бы иначе, и различить их обязательно: чинят их по-разному.
        await Assert.That(Msg.Arg(row.Conflict, Strings.Editor_Hotkey_ConflictOther)).IsEqualTo("pw-immunity");
        await Assert.That(row.Conflict).IsNotEqualTo(Strings.Editor_Hotkey_ConflictSystem);
        await Assert.That(row.HasConflict).IsTrue();
    }

    [Test]
    public async Task FreeChord_HasNoConflict()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);
        var row = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);

        row.KeyName = "F22";

        await Assert.That(row.Conflict).IsNull();
    }

    // Правящийся макрос представлен своими СТРОКАМИ, а не тем, что лежит на диске, — иначе,
    // открыв макрос, мы бы отрапортовали, что каждый его собственный хоткей занят им же самим.
    [Test]
    public async Task OwnChordOnDisk_DoesNotConflictWithItself()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.Control, VirtualKey.F23)));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);

        await Assert.That(Hotkey(vm).Conflict).IsNull();
    }

    [Test]
    public async Task SameChordTwiceInOneMacro_IsReportedOnTheSecondRow()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);
        var first = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);
        var second = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);

        first.KeyName = "F13";
        second.KeyName = "F13";

        await Assert.That(first.Conflict).IsNull();
        // Свой же макрос — отдельный ключ: «уже занят X» отправило бы искать чужой файл.
        await Assert.That(second.Conflict).IsEqualTo(Strings.Editor_Hotkey_ConflictSelf);
        await Assert.That(Msg.Is(second.Conflict, Strings.Editor_Hotkey_ConflictOther)).IsFalse();
    }

    // Два свежих поля выбора, в которых ничего не привязано, — это не столкновение; макрос,
    // написанный наполовину, не имеет права из-за такого краснеть.
    [Test]
    public async Task UnboundPickers_DoNotClashWithEachOther()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("баг-госта"));

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
        using var daemon = new Daemon();
        daemon.Add(Graph("pw-cursor", new HotkeyTrigger(HotkeyModifiers.None, 0, MouseButton.XButton1)));
        daemon.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);
        var row = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);

        // Флаги модификаторов те же (пустые), но клавиша, а не кнопка.
        row.KeyName = "F13";
        await Assert.That(row.Conflict).IsNull();

        row.KeyName = string.Empty;
        row.MouseButton = MouseButton.XButton1;
        await Assert.That(Msg.Arg(row.Conflict, Strings.Editor_Hotkey_ConflictOther)).IsEqualTo("pw-cursor");
    }

    [Test]
    public async Task ConflictAppearsWhenAnotherMacroTakesTheChord()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("баг-госта"));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);
        var row = (HotkeyTriggerRowViewModel)vm.AddTrigger(MacroTriggerKind.Hotkey);
        row.KeyName = "F23";
        await Assert.That(row.Conflict).IsNull();

        // Кто-то правит файл другого макроса, и демон присылает пуш об изменении.
        daemon.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Client.RaiseEvent(IpcMessageTypes.MacrosChanged);

        await Assert.That(Msg.Arg(row.Conflict, Strings.Editor_Hotkey_ConflictOther)).IsEqualTo("pw-immunity");
    }

    // ---- отказы при регистрации --------------------------------------------------------------

    [Test]
    public async Task ChordRefusedByWindows_SaysSoInThePicker()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.Win, VirtualKey.L)));
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.Win, VirtualKey.L));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);

        // Отказ RegisterHotKey — НЕ библиотечное столкновение: виновника назвать нечем, чинится
        // он перепривязкой, и ключ поэтому другой.
        await Assert.That(Hotkey(vm).Conflict).IsEqualTo(Strings.Editor_Hotkey_ConflictSystem);
        await Assert.That(Msg.Is(Hotkey(vm).Conflict, Strings.Editor_Hotkey_ConflictOther)).IsFalse();
    }

    // Когда аккорд делят два макроса, демон регистрирует один, а второму Windows отказывает, —
    // значит, отказ несёт имя ПРОИГРАВШЕГО. Победителю нельзя говорить, что его собственная
    // клавиша занята, — отсюда и сопоставление по имени макроса, а не по одному лишь аккорду.
    [Test]
    public async Task RegistrationFailureOfAnotherMacro_DoesNotAccuseTheWinner()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("first", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Failures.Add(new HotkeyFailureDto("second", HotkeyModifiers.None, VirtualKey.F23));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[0]);

        await Assert.That(Hotkey(vm).Conflict).IsNull();
    }

    // Столкновение внутри библиотеки — это сообщение, которое называет то, что пользователь в
    // силах починить, поэтому оно побеждает даже тогда, когда демон ЗАОДНО сообщает и об отказе
    // регистрации для этого макроса.
    [Test]
    public async Task LibraryConflict_TakesPrecedenceOverTheRegistrationFailure()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.None, VirtualKey.F23));

        using var vm = CreateEditor(daemon);
        vm.LoadGraph(daemon.Macros[1]);

        await Assert.That(Msg.Arg(Hotkey(vm).Conflict, Strings.Editor_Hotkey_ConflictOther)).IsEqualTo("pw-immunity");
    }

    // Ловушка, ради закрытия которой это и существует: хоткей, умерший при старте демона, на
    // макросе, который никто не открывает. Строка в библиотеке — единственное место, где это
    // вообще можно заметить.
    [Test]
    public async Task LibraryRow_MarksAMacroWhoseHotkeyNeverRegistered()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.Win, VirtualKey.L)));
        daemon.Add(Graph("pw-immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.Win, VirtualKey.L));

        using var vm = CreateEditor(daemon);

        var flagged = vm.Macros.Single(item => item.HasHotkeyProblem);
        await Assert.That(flagged.Name).IsEqualTo("баг-госта");
        await Assert.That(flagged.HotkeyProblem).IsEqualTo(
            "Win+L не зарегистрирован — сочетание занято другим приложением");
        await Assert.That(vm.Macros.Single(item => item.Name == "pw-immunity").HasHotkeyProblem).IsFalse();
    }

    // Пока на экране «Макросы», демон держит все аккорды незарегистрированными, так что хоткей,
    // привязанный в редакторе, ПРОБУЮТ только тогда, когда пользователь уходит из этого режима.
    // Возобновление, стало быть, и есть тот момент, когда приговор существует, — и панель обязана
    // за ним сходить.
    [Test]
    public async Task ResumingHotkeys_RefetchesTheFailureList()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("баг-госта", new HotkeyTrigger(HotkeyModifiers.Win, VirtualKey.L)));
        var suspension = A.Fake<IHotkeySuspension>();

        using var vm = CreateEditor(daemon, suspension);
        vm.LoadGraph(daemon.Macros[0]);
        await Assert.That(Hotkey(vm).Conflict).IsNull();

        // При возобновлении демон пробует зарегистрировать — и не может.
        daemon.Failures.Add(new HotkeyFailureDto("баг-госта", HotkeyModifiers.Win, VirtualKey.L));
        await vm.ResumeHotkeysAsync();

        // Отказ RegisterHotKey — НЕ библиотечное столкновение: виновника назвать нечем, чинится
        // он перепривязкой, и ключ поэтому другой.
        await Assert.That(Hotkey(vm).Conflict).IsEqualTo(Strings.Editor_Hotkey_ConflictSystem);
        await Assert.That(Msg.Is(Hotkey(vm).Conflict, Strings.Editor_Hotkey_ConflictOther)).IsFalse();
        A.CallTo(() => suspension.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task RemovingATriggerRow_ClearsTheClashItCaused()
    {
        using var daemon = new Daemon();
        daemon.Add(Graph("баг-госта"));

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
