using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Settings;
using SmartMacro.GameWindows;
using SmartMacro.Input;
using SmartMacro.Native;
using SmartMacro.Native.Mouse;
using SmartMacro.Tests.Settings;

namespace SmartMacro.Tests;

/// <summary>
/// Пара «побудка → заморозка обратно» в <see cref="GameWindow"/>.
///
/// Проверяется ровно одно свойство, и оно не про удобство: раз послав <c>WM_ACTIVATEAPP(TRUE)</c>,
/// мы ОБЯЗАНЫ послать парный <c>FALSE</c> — иначе клиент PW остаётся размороженным (рендерит на
/// полной частоте в фоне) до настоящей смены фокуса пользователем. Веер даёт до десяти тиков
/// зрения разом, то есть до десяти таких клиентов сразу, и починить их некому: обход, который мог
/// бы это сделать, как раз и отменён.
///
/// На этом свойстве держатся два обоснования, записанных в других местах: затвор отладчика стоит
/// между нодами потому, что «к моменту возврата управления в MacroExecutor ни одно игровое окно не
/// остаётся разбуженным», и <c>Orchestrator.StopAsync</c> обещает, что «ни один прогон не бросают
/// посреди активации». До этих тестов оба утверждения были верны только для успешного пути.
///
/// Отмена подаётся УЖЕ ОТМЕНЁННЫМ токеном либо приходит из подделки в известной точке: ни один
/// тест здесь не синхронизируется временем.
/// </summary>
public class GameWindowActivationTests
{
    // Дескриптор заведомо не окна переднего плана: заморозку пропускают только для окна, с
    // которым пользователь работает прямо сейчас (вырывать у него фокус нельзя), а это значение
    // не совпадёт с тем, что вернёт GetForegroundWindow, ни в каком сеансе.
    private static readonly IntPtr Handle = new(0x5A5A_0001);

    private const uint ActivationLParam = 37336;

    /// <summary>Профиль игрового клиента: побудка нужна, обе паузы ненулевые.</summary>
    private static ProcessProfileSettings GameProfile => new()
    {
        ProcessName = "elementclient_64",
        ActivationLParam = ActivationLParam,
        SettleDelayMs = 50,
        DeactivationDelayMs = 20,
    };

    /// <summary>Профиль обычного процесса: <c>ActivationLParam</c> нет — вся пляска пропускается.</summary>
    private static ProcessProfileSettings PlainProfile => new()
    {
        ProcessName = "notepad",
        SettleDelayMs = 50,
        DeactivationDelayMs = 20,
    };

    private static GameWindow Window(FakeNativeWindow native, ProcessProfileSettings profile)
    {
        var settings = new FakeSettingsSource();
        return new GameWindow(
            native,
            profile.ProcessName,
            profile,
            new KeyboardInputResolver(settings, NullLogger<KeyboardInputResolver>.Instance),
            new PostMessageMouseInput(),
            settings,
            NullLogger<GameWindow>.Instance);
    }

    // ---- отмена посреди паузы устаканивания ---------------------------------------------

    [Test]
    public async Task ACancelledVisionTickStillRefreezesTheClient()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, GameProfile);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Это и есть «■ Стоп» (или выключение демона), пришедший ровно в паузу устаканивания:
        // побудку мы уже послали, а до захвата не дошли.
        var found = await window.FindElementAsync([1, 2, 3], default, null, cts.Token);

        await Assert.That(found).IsNull();
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    [Test]
    public async Task ACancelledPollingTickRefreezesBeforeGivingUp()
    {
        var native = new FakeNativeWindow(Handle);
        using var cts = new CancellationTokenSource();

        // Детерминированная точка вместо ожидания по часам: отмена происходит ВНУТРИ захвата, то
        // есть после побудки и до того, как цикл опроса уйдёт на следующий тик.
        native.OnCapture = cts.Cancel;
        native.CaptureFault = () => new InvalidOperationException("PrintWindow отказал");

        var window = Window(native, GameProfile);

        await Assert.That(async () =>
                await window.WaitForElementAsync([1, 2, 3], default, TimeSpan.FromMinutes(1), null, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "capture", "deactivate" });
    }

    // ---- отказ самого захвата -------------------------------------------------------------

    [Test]
    public async Task AFailedCaptureStillRefreezesTheClient()
    {
        var native = new FakeNativeWindow(Handle)
        {
            // Так падает CapturePng на свёрнутом клиенте (нулевая клиентская область) и на отказе
            // PrintWindow — второй из двух бросающих на этом пути.
            CaptureFault = () => new InvalidOperationException("PrintWindow отказал"),
        };
        var window = Window(native, GameProfile);

        await Assert.That(() => window.CaptureScreenshot()).Throws<InvalidOperationException>();
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "capture", "deactivate" });
    }

    // ---- ручная скобка ввода ----------------------------------------------------------------

    [Test]
    public async Task ACancelledDrainStillRefreezesTheClient()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, GameProfile);
        using var cts = new CancellationTokenSource();

        await window.ActivateAsync();
        await cts.CancelAsync();

        // Отменённый слив — потерянный ввод, неприятность. Отменённая заморозка — клиент, который
        // рендерит в фоне до конца сеанса. Поэтому пауза отменяема, а заморозка после неё нет.
        await Assert.That(async () => await window.DeactivateAsync(cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    [Test]
    public async Task ACancelledActivationRefreezesItselfBecauseNobodyElseWill()
    {
        var native = new FakeNativeWindow(Handle);
        var window = Window(native, GameProfile);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Асимметрия относительно успешного пути намеренная: вызывающий (AgentInputDispatcher)
        // держит ActivateAsync ПЕРЕД своим try — иначе в finally нечего было бы деактивировать, —
        // так что бросок отсюда уносит управление мимо его finally, и закрыть скобку больше некому.
        await Assert.That(async () => await window.ActivateAsync(cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(native.Calls).IsEquivalentTo(new[] { "activate", "deactivate" });
    }

    // ---- обычный процесс: ничего лишнего ----------------------------------------------------

    [Test]
    public async Task AProcessWithoutAnActivationSignalIsNeitherWokenNorFrozen()
    {
        var native = new FakeNativeWindow(Handle)
        {
            CaptureFault = () => new InvalidOperationException("PrintWindow отказал"),
        };
        var window = Window(native, PlainProfile);

        await Assert.That(() => window.CaptureScreenshot()).Throws<InvalidOperationException>();

        // Отсутствие ActivationLParam — рабочая семантика («обычный процесс, побудка
        // пропускается»), и заморозка обязана пропускаться вместе с ней: WM_ACTIVATEAPP(FALSE) по
        // окну, которое мы не будили, — это уже вмешательство в чужое приложение.
        await Assert.That(native.Calls).IsEquivalentTo(new[] { "capture" });
    }
}

/// <summary>
/// Подделка <see cref="INativeWindow"/>, записывающая последовательность обращений к Win32.
///
/// Пишутся только три вызова, вокруг которых и строится всё свойство: побудка, захват, заморозка.
/// Порядок в <see cref="Calls"/> и есть предмет проверки — «побудка без заморозки» отличается от
/// правильного поведения именно недостающей записью.
/// </summary>
internal sealed class FakeNativeWindow(IntPtr handle) : INativeWindow
{
    private readonly Lock _lock = new();
    private readonly List<string> _calls = [];

    public IntPtr Handle { get; } = handle;

    public bool IsAlive => true;

    /// <summary>Чем падает <see cref="CapturePng"/>: свёрнутый клиент, отказ PrintWindow.</summary>
    public Func<Exception>? CaptureFault { get; set; }

    /// <summary>Зовётся внутри <see cref="CapturePng"/> — детерминированная точка для отмены.</summary>
    public Action? OnCapture { get; set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public void SendActivationSignal(uint lParam) => Record("activate");

    public void SendDeactivationSignal() => Record("deactivate");

    public byte[] CapturePng()
    {
        Record("capture");
        OnCapture?.Invoke();
        return CaptureFault is { } fault ? throw fault() : [];
    }

    public (int Width, int Height) GetClientSize() => (1920, 1080);

    // Ниже — то, чего этот набор не касается вовсе. Бросаем, а не возвращаем заглушку: молчаливый
    // ноль в тесте про последовательность вызовов был бы ложным зелёным.
    public (int X, int Y) ClientToScreen(int clientX, int clientY) => throw new NotSupportedException();

    public (int X, int Y) ScreenToClient(int screenX, int screenY) => throw new NotSupportedException();

    public bool BringToFront() => throw new NotSupportedException();

    public bool SetIconFromFile(string imagePath) => throw new NotSupportedException();

    private void Record(string call)
    {
        lock (_lock)
        {
            _calls.Add(call);
        }
    }
}
