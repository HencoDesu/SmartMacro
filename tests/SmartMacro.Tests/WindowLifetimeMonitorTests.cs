using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.Windows;

namespace SmartMacro.Tests;

// W0.4: слежение за временем жизни окон стало одной службой вместо CharacterAgent'а на клиента.
// Проверяется то, ради чего опрос вообще существует, — мёртвый hwnd обязан выпасть из реестра
// (иначе он продолжит подходить под теговые селекторы, и каждое разветвление будет впустую
// тратить на него цикл активации), — плюс выключение, которое обнуляет реестр целиком.
public class WindowLifetimeMonitorTests
{
    private sealed class Harness
    {
        public WindowRegistry Registry { get; } = new(NullLogger<WindowRegistry>.Instance);

        public WindowLifetimeMonitor Monitor { get; }

        public Harness(int pollIntervalSeconds = 2)
        {
            Monitor = new WindowLifetimeMonitor(
                Registry,
                Options.Create(new AgentOptions { AgentPollIntervalSeconds = pollIntervalSeconds }),
                NullLogger<WindowLifetimeMonitor>.Instance);
        }

        /// <summary>Регистрирует окно с подделанным фасадом и возвращает ручку его живости.</summary>
        public IGameWindow AddWindow(nint hwnd, string process = "elementclient_64", bool alive = true)
        {
            var window = A.Fake<IGameWindow>();
            A.CallTo(() => window.Handle).Returns(hwnd);
            A.CallTo(() => window.IsAlive).Returns(alive);
            Registry.Register(hwnd, process, window);
            return window;
        }
    }

    [Test]
    public async Task Sweep_LeavesLiveWindowsAlone()
    {
        var harness = new Harness();
        harness.AddWindow(0x10);
        harness.AddWindow(0x20);

        var removed = harness.Monitor.Sweep();

        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(harness.Registry.Snapshot()).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Sweep_UnregistersADeadWindow_AndRaisesWindowClosed_WithItsTags()
    {
        var harness = new Harness();
        var window = harness.AddWindow(0x10);
        harness.Registry.AddTag(0x10, "Лучник");
        var closed = new List<ManagedWindowInfo>();
        harness.Registry.WindowClosed += closed.Add;

        A.CallTo(() => window.IsAlive).Returns(false);
        var removed = harness.Monitor.Sweep();

        await Assert.That(removed).IsEqualTo(1);
        await Assert.That(harness.Registry.Snapshot()).IsEmpty();
        await Assert.That(closed).Count().IsEqualTo(1);
        await Assert.That(closed[0].Hwnd).IsEqualTo(new IntPtr(0x10));
        await Assert.That(closed[0].Tags.Contains("Лучник")).IsTrue();
        // И, что важнее всего: селекторы про него забыли.
        await Assert.That(harness.Registry.GetTags(0x10)).IsEmpty();
        await Assert.That(harness.Registry.TryGetWindow(0x10)).IsNull();
    }

    [Test]
    public async Task Sweep_TakesOutOnlyTheDeadOne_OutOfMany()
    {
        var harness = new Harness();
        harness.AddWindow(0x10);
        var doomed = harness.AddWindow(0x20);
        harness.AddWindow(0x30);

        A.CallTo(() => doomed.IsAlive).Returns(false);
        var removed = harness.Monitor.Sweep();

        await Assert.That(removed).IsEqualTo(1);
        await Assert.That(harness.Registry.Snapshot().Select(w => w.Hwnd))
            .IsEquivalentTo(new[] { new IntPtr(0x10), new IntPtr(0x30) });
    }

    [Test]
    public async Task Sweep_IsIdempotent_AfterTheWindowIsAlreadyGone()
    {
        var harness = new Harness();
        var window = harness.AddWindow(0x10);
        A.CallTo(() => window.IsAlive).Returns(false);

        await Assert.That(harness.Monitor.Sweep()).IsEqualTo(1);
        await Assert.That(harness.Monitor.Sweep()).IsEqualTo(0);
    }

    [Test]
    public async Task Sweep_IgnoresEntriesRegisteredWithoutAFacade()
    {
        // Про живость такой записи нам никто не рассказывает (тесты, будущие регистрации со
        // стороны UI), поэтому монитор её не трогает — а не сносит из-за отсутствия ответа.
        var harness = new Harness();
        harness.Registry.Register(0x10, "elementclient_64");

        var removed = harness.Monitor.Sweep();

        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(harness.Registry.Snapshot()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task StopAsync_UnregistersEverything_EvenLiveWindows()
    {
        // Выключение демона: окна перестают быть управляемыми независимо от того, живы ли их
        // процессы. Раньше это делал каждый CharacterAgent в своём finally.
        var harness = new Harness();
        harness.AddWindow(0x10);
        harness.AddWindow(0x20);
        harness.Registry.Register(0x30, "ui-only");
        var closed = new List<ManagedWindowInfo>();
        harness.Registry.WindowClosed += closed.Add;

        await harness.Monitor.StartAsync(CancellationToken.None);
        await harness.Monitor.StopAsync(CancellationToken.None);

        await Assert.That(harness.Registry.Snapshot()).IsEmpty();
        await Assert.That(closed).Count().IsEqualTo(3);
    }

    [Test]
    public async Task StopAsync_WithoutStart_StillClearsTheRegistry()
    {
        var harness = new Harness();
        harness.AddWindow(0x10);

        await harness.Monitor.StopAsync(CancellationToken.None);

        await Assert.That(harness.Registry.Snapshot()).IsEmpty();
    }

    [Test]
    public async Task StartAsync_Twice_Throws()
    {
        var harness = new Harness();
        await harness.Monitor.StartAsync(CancellationToken.None);

        await Assert.That(async () => await harness.Monitor.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await harness.Monitor.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task TheLoopSweepsOnItsOwn_WithoutAnybodyCallingSweep()
    {
        // Один таймер на все окна — ровно тот пункт, ради которого N объектов с N таймерами
        // схлопнули в службу. Интервал ниже плинтуса, чтобы тест не спал две секунды.
        var harness = new Harness(pollIntervalSeconds: 0);
        var window = harness.AddWindow(0x10);
        var closed = new TaskCompletionSource();
        harness.Registry.WindowClosed += _ => closed.TrySetResult();

        await harness.Monitor.StartAsync(CancellationToken.None);
        A.CallTo(() => window.IsAlive).Returns(false);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Monitor.StopAsync(CancellationToken.None);

        await Assert.That(harness.Registry.Snapshot()).IsEmpty();
    }
}
