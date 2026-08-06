using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Settings;
using SmartMacro.GameWindows;
using SmartMacro.Input;
using SmartMacro.Native.Mouse;
using SmartMacro.Tests.Settings;

namespace SmartMacro.Tests;

/// <summary>
/// Скобка пробуждения как ОБЛАСТЬ: счётчик на окно, набор <c>On</c> и то, что закрытие области
/// повторяет прежнюю пару «побудка → заморозка» дословно.
///
/// Соседний набор <see cref="GameWindowActivationTests"/> пиннит ПОЛОВИНЫ скобки — что после
/// <c>WM_ACTIVATEAPP(TRUE)</c> обязательно уходит парный <c>FALSE</c>, чем бы дело ни кончилось.
/// Здесь проверяется то, что появилось вместе с областью и чего до неё не существовало:
/// вложенность (панель и демон захотели одно окно), выборочность (<c>On</c>) и разница между
/// выходом из области ВВОДА и области ЗАХВАТА.
///
/// Ни один тест здесь не синхронизируется временем: там, где важна пауза на слив, проверяется не
/// её длительность, а сам факт — завершилась ли задача выхода СИНХРОННО.
/// </summary>
public class ProcessHookTests
{
    // Заведомо не окно переднего плана: заморозку пропускают только для окна, с которым
    // пользователь работает прямо сейчас.
    private static readonly IntPtr Handle = new(0x5A5A_0002);

    private static ProcessHookSettings Hook(
        int settleMs = 0,
        int deactivateMs = 0,
        HookLifetime scope = HookLifetime.Action,
        params HookOn[] on) => new()
    {
        ActivationLParam = 37336,
        SettleMs = settleMs,
        DeactivateMs = deactivateMs,
        On = on.Length == 0 ? [HookOn.Input, HookOn.Capture] : on,
        Scope = scope,
    };

    internal static GameWindow Window(FakeNativeWindow native, ProcessHookSettings? hook)
    {
        var settings = new FakeSettingsSource();
        return new GameWindow(
            native,
            "elementclient_64",
            hook,
            new KeyboardInputResolver(settings, NullLogger<KeyboardInputResolver>.Instance),
            new PostMessageMouseInput(),
            settings,
            NullLogger<GameWindow>.Instance);
    }

    // ---- счётчик на окно ----------------------------------------------------------------------

    // ⚠️ ЗЕРНИСТОСТЬ ОТ СЧЁТЧИКА НЕ МЕНЯЕТСЯ. Последовательные ноды входят и выходят полностью, и
    // цикл пробуждения остаётся один на ноду — ровно как было до появления области.
    [Test]
    public async Task SequentialScopesStillWakeAndFreezeOncePerScope()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, Hook());

        await using (await window.EnterHookAsync(HookOn.Input))
        {
        }

        await using (await window.EnterHookAsync(HookOn.Input))
        {
        }

        await Assert.That(native.Calls)
            .IsEquivalentTo(new[] { "activate", "deactivate", "activate", "deactivate" });
    }

    // Первый вход будит, последний выход замораживает: одно окно могут захотеть двое — демон во
    // время прогона и панель во время снимка, — и вложенная область не имеет права ни разбудить
    // окно второй раз, ни заморозить его из-под того, кто ещё работает.
    [Test]
    public async Task NestedScopesWakeOnceAndFreezeOnceAtTheLastExit()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, Hook());

        var outer = await window.EnterHookAsync(HookOn.Input);
        var inner = await window.EnterHookAsync(HookOn.Capture);

        await inner.DisposeAsync();
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate" })
            .Because("внутренняя область не имеет права заморозить окно из-под внешней");

        await outer.DisposeAsync();
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    // Лишнее закрытие не уводит счётчик в минус — по тем же соображениям, по каким этого не делает
    // HotkeyListener.ResumeAsync: структуру можно скопировать и закрыть дважды.
    [Test]
    public async Task ClosingTheSameScopeTwiceFreezesOnce()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, Hook());

        var scope = await window.EnterHookAsync(HookOn.Input);
        await scope.DisposeAsync();
        await scope.DisposeAsync();

        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    // ---- набор On -----------------------------------------------------------------------------

    [Test]
    public async Task AHookThatDoesNotFireOnInputLeavesInputAlone()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, Hook(on: HookOn.Capture));

        var input = await window.EnterHookAsync(HookOn.Input);
        await Assert.That(input.IsHolding).IsFalse();
        await input.DisposeAsync();

        await using (var capture = await window.EnterHookAsync(HookOn.Capture))
        {
            await Assert.That(capture.IsHolding).IsTrue();
        }

        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    // Пустой набор законен и от отсутствия хука отличается только в файле: хук есть, числа
    // сохранены, не срабатывает нигде.
    [Test]
    public async Task AnEmptyOnSetNeverWakesTheWindow()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, new ProcessHookSettings { ActivationLParam = 37336, On = [] });

        await using (await window.EnterHookAsync(HookOn.Input))
        {
        }

        window.CaptureScreenshot();

        await Assert.That(native.Calls).IsEquivalentTo(new[] { "capture" });
    }

    // ---- слив принадлежит ВВОДУ ---------------------------------------------------------------

    // Пауза на слив существует ради того, что PostMessage положил в очередь цели. Захват туда не
    // кладёт ничего, и до этой волны оба пути захвата замораживали окно без слива. Проверяется не
    // длительность, а факт: у захвата выход завершается синхронно, у ввода — нет.
    [Test]
    public async Task LeavingACaptureScopeDoesNotPayTheDrain()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, Hook(deactivateMs: 50));

        var scope = await window.EnterHookAsync(HookOn.Capture);
        var exit = scope.DisposeAsync();

        await Assert.That(exit.IsCompleted).IsTrue();
        await exit;
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    [Test]
    public async Task LeavingAnInputScopeDrainsFirst()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, Hook(deactivateMs: 50));

        var scope = await window.EnterHookAsync(HookOn.Input);
        var exit = scope.DisposeAsync();

        // Task.Delay(50) никогда не завершается сразу, так что это детерминированно.
        await Assert.That(exit.IsCompleted).IsFalse();
        await exit;
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    // Область, в которой ввод БЫЛ, платит слив на общем выходе — даже если последней закрылась
    // область захвата: сливаем мы очередь окна, а не «свою».
    [Test]
    public async Task ANestedCaptureInsideInputStillDrainsAtTheLastExit()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, Hook(deactivateMs: 50));

        var input = await window.EnterHookAsync(HookOn.Input);
        var capture = await window.EnterHookAsync(HookOn.Capture);

        await input.DisposeAsync();
        var exit = capture.DisposeAsync();

        await Assert.That(exit.IsCompleted).IsFalse();
        await exit;
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    // ---- Scope: Run ---------------------------------------------------------------------------

    [Test]
    public async Task WakesForWholeRun_IsTrueOnlyForRunScopedHooksAndOnlyForTheirOwnOperations()
    {
        var native = new FakeNativeWindow(Handle);

        var action = Window(native, Hook(scope: HookLifetime.Action));
        await Assert.That(action.WakesForWholeRun(HookOn.Input)).IsFalse();

        var run = Window(native, Hook(scope: HookLifetime.Run, on: HookOn.Capture));
        await Assert.That(run.WakesForWholeRun(HookOn.Capture)).IsTrue();
        await Assert.That(run.WakesForWholeRun(HookOn.Input)).IsFalse();

        var plain = Window(native, null);
        await Assert.That(plain.WakesForWholeRun(HookOn.Capture)).IsFalse();
    }
}
