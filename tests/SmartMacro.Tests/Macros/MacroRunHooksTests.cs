using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Settings;
using SmartMacro.GameWindows;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Macros;

/// <summary>
/// <c>Scope: Run</c> — побудка, которая держится весь прогон, и то, чем за неё платят.
///
/// Умолчание (<see cref="HookLifetime.Action"/>) сюда не заходит вовсе: его область живёт внутри
/// ноды и до контекста прогона не доходит. Здесь проверяется второй уровень — ссылка, которую
/// берёт ОБХОДЧИК, и три её свойства:
///
///   · ленивость: набор окон в начале прогона неизвестен, ссылка появляется на первой же ноде,
///     которая до окна дотянулась;
///   · окно не засыпает МЕЖДУ нодами — ровно то, ради чего режим и заведён;
///   · ⚠️ припарковавшийся обход ссылки ОТПУСКАЕТ. Без этого <c>Scope: Run</c> ломает довод, на
///     котором стоит затвор отладчика: пауза между нодами потому и безопасна, что к возврату в
///     исполнитель ни одно окно не разбужено. Таймаута бездействия у паузы намеренно нет, так что
///     «человек ушёл за чаем» означало бы до десяти клиентов, рендерящих в фоне сколько угодно
///     долго.
/// </summary>
public class MacroRunHooksTests
{
    private static readonly IntPtr Hwnd = new(0x5A5A_0003);

    private static ProcessHookSettings RunHook => new()
    {
        ActivationLParam = 37336,
        On = [HookOn.Input, HookOn.Capture],
        Scope = HookLifetime.Run,
    };

    private static ProcessHookSettings ActionHook => RunHook with { Scope = HookLifetime.Action };

    /// <summary>Реестр с одним настоящим <see cref="GameWindow"/> поверх подделки Win32.</summary>
    private static (WindowRegistry Registry, FakeNativeWindow Native) Registry(ProcessHookSettings? hook)
    {
        var native = new FakeNativeWindow(Hwnd);
        var registry = new WindowRegistry(NullLogger<WindowRegistry>.Instance);
        registry.Register(Hwnd, "elementclient_64", ProcessHookTests.Window(native, hook));
        return (registry, native);
    }

    // ---- сама корзина -------------------------------------------------------------------------

    [Test]
    public async Task EnsureAsync_WakesARunScopedWindowOnceAndKeepsIt()
    {
        var (registry, native) = Registry(RunHook);
        await using var hooks = new MacroRunHooks(registry);

        await hooks.EnsureAsync([Hwnd], HookOn.Input, CancellationToken.None);
        await hooks.EnsureAsync([Hwnd], HookOn.Input, CancellationToken.None);

        await Assert.That(hooks.HeldCount).IsEqualTo(1);
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate" });
    }

    // Обычный хук в корзину не попадает: его скобка живёт внутри ноды, и вторая ссылка на то же
    // окно означала бы, что оно не заснёт после ноды — то есть молча включённый Scope: Run.
    [Test]
    public async Task EnsureAsync_IgnoresAnActionScopedWindow()
    {
        var (registry, native) = Registry(ActionHook);
        await using var hooks = new MacroRunHooks(registry);

        await hooks.EnsureAsync([Hwnd], HookOn.Input, CancellationToken.None);

        await Assert.That(hooks.HeldCount).IsEqualTo(0);
        await Assert.That(native.Calls).IsEmpty();
    }

    [Test]
    public async Task DisposeAsync_FreezesEverythingTheRunHeld()
    {
        var (registry, native) = Registry(RunHook);
        var hooks = new MacroRunHooks(registry);
        await hooks.EnsureAsync([Hwnd], HookOn.Input, CancellationToken.None);

        await hooks.DisposeAsync();

        await Assert.That(hooks.HeldCount).IsEqualTo(0);
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    // Роспуск на паузе — не конец прогона: корзина остаётся рабочей, и следующая нода берёт
    // ссылку заново. Захват ленивый, поэтому «взять обратно» ничего специального не требует.
    [Test]
    public async Task ReleaseAllAsync_FreezesButLeavesTheBasketUsable()
    {
        var (registry, native) = Registry(RunHook);
        await using var hooks = new MacroRunHooks(registry);
        await hooks.EnsureAsync([Hwnd], HookOn.Input, CancellationToken.None);

        await hooks.ReleaseAllAsync();
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });

        await hooks.EnsureAsync([Hwnd], HookOn.Input, CancellationToken.None);
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate", "activate" });
    }

    // ---- через обходчик -----------------------------------------------------------------------

    private static MacroGraph Chain() => ExecutorHarness.Graph(
        "цепочка",
        Ids.Of("a"),
        new KeyPressNode { Id = Ids.Of("a"), DisplayName = "a", Key = VirtualKey.F1, Next = Ids.Of("b") },
        new KeyPressNode { Id = Ids.Of("b"), DisplayName = "b", Key = VirtualKey.F2 });

    // Записывающая подделка примитивов настоящих окон не трогает, так что каждая запись в
    // native.Calls здесь — это ссылка ПРОГОНА, и никакая другая.
    private static ExecutorHarness HarnessWith(ProcessHookSettings? hook, out FakeNativeWindow native)
    {
        var harness = new ExecutorHarness();
        var fake = new FakeNativeWindow(Hwnd);
        harness.Registry.Register(Hwnd, "elementclient_64", ProcessHookTests.Window(fake, hook));
        native = fake;
        return harness;
    }

    [Test]
    public async Task ARunScopedWindowStaysAwakeBetweenNodes()
    {
        var harness = HarnessWith(RunHook, out var native);
        await using var hooks = new MacroRunHooks(harness.Registry);

        var result = await harness.Executor.RunAsync(Chain(), harness.Context(Hwnd, hooks: hooks),
            CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(MacroRunStatus.Completed);
        // Две ноды ввода — одна побудка: в этом весь режим. Заморозка сюда ещё не приходила,
        // потому что корзину закрывает оркестратор, а не обходчик.
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate" });
    }

    [Test]
    public async Task WithoutARunScopedHookTheWalkerTouchesNoWindow()
    {
        var harness = HarnessWith(ActionHook, out var native);
        await using var hooks = new MacroRunHooks(harness.Registry);

        await harness.Executor.RunAsync(Chain(), harness.Context(Hwnd, hooks: hooks), CancellationToken.None);

        await Assert.That(native.Calls).IsEmpty();
    }

    // ⚠️ Тот самый случай, ради которого правка отладчика шла тем же заходом.
    [Test]
    public async Task AParkedWalkReleasesItsRunScopedWakeAndTakesItBackOnResume()
    {
        var harness = HarnessWith(RunHook, out var native);
        await using var hooks = new MacroRunHooks(harness.Registry);
        var observer = new RecordingObserver();
        var session = new MacroDebugSession(NullLogger<MacroDebugSession>.Instance);
        session.Acquire();
        session.SetBreakpoints("цепочка", null, [Ids.Of("b")]);

        var run = harness.Executor.RunAsync(
            Chain(),
            harness.Context(Hwnd, observer: observer, debugger: session, hooks: hooks),
            CancellationToken.None);

        await WaitFor(() => observer.OfKind(RecordingObserver.PausedKind).Count > 0, "обход должен встать на паузу");
        await WaitFor(() => hooks.HeldCount == 0, "припарковавшийся обход обязан отпустить ссылки прогона");
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" })
            .Because("к моменту паузы ни одно игровое окно не должно оставаться разбуженным");

        session.Command(observer.Walks[0].WalkId, DebugCommand.Resume, nodeId: null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // Возобновление ссылку не восстанавливает — её берёт ленивый захват на той же ноде, ради
        // которой обход и остановился.
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate", "activate" });
        await Assert.That(hooks.HeldCount).IsEqualTo(1);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        await Assert.That(condition()).IsTrue().Because(what);
    }
}
