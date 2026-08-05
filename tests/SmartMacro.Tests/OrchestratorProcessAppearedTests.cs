using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Settings;
using SmartMacro.GameWindows;
using SmartMacro.Hotkeys;
using SmartMacro.Input;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;
using SmartMacro.Native.Hotkey;
using SmartMacro.Orchestration;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Tests.Macros;
using SmartMacro.Tests.Settings;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Tests;

/// <summary>
/// Ветка «появился процесс» у оркестратора — второй и последний способ вооружить макрос.
///
/// Главное здесь одно: источником служит <see cref="MacroGraphStore.Armed"/>, а не <c>All</c>, —
/// то же требование F3, которое <c>HotkeyListener</c> выполняет во всех трёх своих местах.
/// Отдельный список и заведён потому, что «забыли отфильтровать в одном из мест» выглядит как
/// макрос, который иногда работает; на этой ветке цена ошибки выше, чем на хоткее: у макроса с
/// ошибкой хоткей честно мёртв и строка библиотеки честно ругается, так что пользователь считает
/// его инертным — а каждый запуск клиента стартовал бы его на десяти окнах и обрывал на битой
/// ноде где-то посреди логин-последовательности.
///
/// Тест не синхронизируется временем и не ждёт: <c>OnProcessAppeared</c> запускает прогоны
/// синхронно (у <c>_ = RunAsync(...)</c> первый await случается уже после
/// <c>MacroWalkTrace.Begin</c>), поэтому к моменту возврата управления все обходы, которые
/// собирались начаться, уже объявлены наблюдателю.
/// </summary>
public class OrchestratorProcessAppearedTests
{
    private const string Process = "elementclient_64";
    private const string Broken = "a-битый-старт";
    private const string Whole = "b-целый-старт";

    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness(string dir)
        {
            _dir = dir;
            Store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance);
            Windows = new WindowRegistry(NullLogger<WindowRegistry>.Instance);
            Templates = new MacroTemplateCache(Store, NullLogger<MacroTemplateCache>.Instance);
            Runs = new MacroRunRegistry(NullLogger<MacroRunRegistry>.Instance);

            var settings = new FakeSettingsSource();
            var monitor = new ProcessMonitor(settings, NullLogger<ProcessMonitor>.Instance);
            // Слушатель хоткеев собирается настоящим, но не запускается: до StartAsync мониторы
            // Win32 безжизненны и RegisterHotKey не трогают (тот же приём, что в
            // HotkeyListenerTests). Оркестратору он нужен только затем, что тот подписывается на
            // его событие в конструкторе.
            var hotkeys = new HotkeyListener(
                Store,
                new Win32HotkeyMonitor(NullLogger<Win32HotkeyMonitor>.Instance),
                new Win32MouseHookMonitor(NullLogger<Win32MouseHookMonitor>.Instance),
                NullLogger<HotkeyListener>.Instance);

            Orchestrator = new Orchestrator(
                monitor,
                hotkeys,
                Factory,
                Windows,
                Store,
                new MacroExecutor(Primitives, Windows, NullLogger<MacroExecutor>.Instance),
                Runs,
                new CursorPositionProvider(Windows, NullLogger<CursorPositionProvider>.Instance),
                Templates,
                NullLogger<Orchestrator>.Instance,
                Observer);
        }

        public IGameWindowFactory Factory { get; } = A.Fake<IGameWindowFactory>();

        public MacroGraphStore Store { get; }

        public WindowRegistry Windows { get; }

        public MacroTemplateCache Templates { get; }

        public MacroRunRegistry Runs { get; }

        public Orchestrator Orchestrator { get; }

        public RecordingPrimitives Primitives { get; } = new();

        public RecordingObserver Observer { get; } = new();

        /// <summary>Подделывает фасад окна так, чтобы оркестратор согласился взять его под управление.</summary>
        public ProcessInfo Appear(nint hwnd)
        {
            var window = A.Fake<IGameWindow>();
            A.CallTo(() => window.Handle).Returns(hwnd);
            A.CallTo(() => window.IsAlive).Returns(true);
            // Ненулевой размер обязателен: окна, с которых нечего захватывать, оркестратор
            // отсеивает (это лаунчеры PW), и тогда до макросов дело не дойдёт вовсе.
            A.CallTo(() => window.ClientSize).Returns((1920, 1080));
            A.CallTo(() => Factory.Create(A<ProcessInfo>._)).Returns(window);
            return new ProcessInfo(4242, Process, hwnd);
        }

        public void Dispose()
        {
            Orchestrator.Dispose();
            Templates.Dispose();
            Runs.Dispose();
            Store.Dispose();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-orchestrator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Граф, который валидатор примет: одна нода, ребёр наружу нет.</summary>
    private static MacroGraph WholeGraph() => new()
    {
        Name = Whole,
        Triggers = [new ProcessAppearedTrigger(Process)],
        StartNodeId = Ids.Of("ok"),
        Nodes = [new KeyPressNode { Id = Ids.Of("ok"), DisplayName = "ok", Key = VirtualKey.F1 }],
    };

    /// <summary>
    /// Граф, который ПАРСИТСЯ, но валидацию не проходит: ребро ведёт в ноду, которой в графе нет.
    /// Разница принципиальна — непарсимый бандл вообще не попал бы в библиотеку, и тест зеленел бы
    /// сам собой, ничего не проверив.
    /// </summary>
    private static MacroGraph BrokenGraph() => new()
    {
        Name = Broken,
        Triggers = [new ProcessAppearedTrigger(Process)],
        StartNodeId = Ids.Of("bad"),
        Nodes =
        [
            new KeyPressNode
            {
                Id = Ids.Of("bad"),
                DisplayName = "bad",
                Key = VirtualKey.F2,
                Next = Ids.Of("такой-ноды-нет"),
            },
        ],
    };

    [Test]
    public async Task AnInvalidMacroIsNotStartedByTheProcessAppearedTrigger()
    {
        var dir = CreateTempDir();
        // Библиотека кладётся на диск ДО хранилища — тем же путём, каким её кладёт панель, и без
        // единого ожидания наблюдателя: конструктор хранилища читает папку сам.
        MacroBundleFolder.Save(MacroBundleFolder.In(dir), WholeGraph());
        MacroBundleFolder.Save(MacroBundleFolder.In(dir), BrokenGraph());
        using var harness = new Harness(dir);

        // Предусловие, без которого тест ничего не значит: оба бандла ПРОЧИТАНЫ, и разошлись они
        // именно на вердикте валидатора.
        await Assert.That(harness.Store.All.Select(graph => graph.Name))
            .IsEquivalentTo(new[] { Broken, Whole });
        await Assert.That(harness.Store.Armed.Select(graph => graph.Name))
            .IsEquivalentTo(new[] { Whole });

        harness.Orchestrator.OnProcessAppeared(harness.Appear(0x140804));

        // Окно взято под управление — то есть до запуска макросов дело дошло.
        await Assert.That(harness.Windows.Snapshot()).Count().IsEqualTo(1);
        // …и стартовал ровно целый макрос. Битый не начал даже первую ноду: на живой игре он
        // прошёл бы полпути логин-последовательности на каждом из десяти клиентов.
        await Assert.That(harness.Observer.Walks.Select(walk => walk.MacroName))
            .IsEquivalentTo(new[] { Whole });
        await Assert.That(harness.Primitives.Calls.Select(call => call.A?.ToString() ?? "?"))
            .IsEquivalentTo(new[] { VirtualKey.F1.ToString() });
    }
}
