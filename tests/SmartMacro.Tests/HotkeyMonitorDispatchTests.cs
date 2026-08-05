using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Native;
using SmartMacro.Native.Hotkey;
using SmartMacro.Native.Internal;

namespace SmartMacro.Tests;

// Колбэк низкоуровневого мышиного хука зовётся ОС на каждое движение мыши во всей системе, и пока
// он не вернулся, ввод мышью придушен везде. Выйдя за LowLevelHooksTimeout (по умолчанию 300 мс),
// хук снимает сама Windows — молча: ни строки в журнале, ни отметки в интерфейсе, а
// RejectedBindings этого не поймает, потому что регистрация-то удалась.
//
// А ниже подписчика синхронно лежит весь запуск макроса: слушатель → оркестратор → веер по целевым
// окнам, и внутри — блокирующие SendMessage(WM_ACTIVATEAPP) в замороженные клиенты игры. Десять
// клиентов — десять блокирующих отправок ВНУТРИ колбэка; с SetIconNode первой нодой — тридцать.
// Поэтому оба монитора отдают срабатывание в пул потоков, как это давно делает
// TrayController.OnItemClicked со своим модальным циклом меню.
//
// Проверяется это по ЛИЧНОСТИ ПОТОКА, а не по времени: измерять «колбэк вернулся быстро» значило
// бы завести плавающий тест. Подписчик садится на воротах, а тест смотрит, что колбэк вернулся,
// пока тот ещё внутри, и что сидит он на чужом потоке. Ворота с потолком по времени, а не вечные:
// без правки этот тест обязан покраснеть, а не повиснуть.
public class HotkeyMonitorDispatchTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    private sealed class Subscriber
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _mayFinish = new(false);
        private readonly ManualResetEventSlim _finished = new(false);

        public int ThreadId { get; private set; }

        public bool Finished => _finished.IsSet;

        public void Handle(int id)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            _entered.Set();
            _mayFinish.Wait(Ceiling);
            _finished.Set();
        }

        public bool WaitUntilEntered() => _entered.Wait(Ceiling);

        public bool ReleaseAndWait()
        {
            _mayFinish.Set();
            return _finished.Wait(Ceiling);
        }
    }

    [Test]
    public async Task MouseHookCallback_ReturnsWithoutRunningTheSubscriber()
    {
        using var monitor = new Win32MouseHookMonitor(NullLogger<Win32MouseHookMonitor>.Instance);
        var subscriber = new Subscriber();
        monitor.HotkeyPressed += subscriber.Handle;

        var callbackThreadId = Environment.CurrentManagedThreadId;
        monitor.InvokeHookForTests(
            [new MouseHookBinding(7, HotkeyModifiers.None, MouseButton.Middle)],
            User32Native.WM_MBUTTONDOWN);

        // Колбэк уже вернулся — а подписчик ещё внутри и на другом потоке. Это и есть передача.
        await Assert.That(subscriber.WaitUntilEntered()).IsTrue();
        await Assert.That(subscriber.Finished).IsFalse();
        await Assert.That(subscriber.ThreadId).IsNotEqualTo(callbackThreadId);

        await Assert.That(subscriber.ReleaseAndWait()).IsTrue();
    }

    // Клавиатурный монитор — близнец, и правило у них одно. Цена промаха здесь мягче (общесистемного
    // ввода этот поток не держит), но не местная: через ту же очередь приходят следующие WM_HOTKEY и
    // WM_QUIT из StopAsync, то есть приостановка хоткеев, перерегистрация по изменению библиотеки и
    // выключение демона встали бы в очередь за прогоном макроса.
    [Test]
    public async Task KeyboardMonitor_RaisesOnThePool_ForTheSameReason()
    {
        using var monitor = new Win32HotkeyMonitor(NullLogger<Win32HotkeyMonitor>.Instance);
        var subscriber = new Subscriber();
        monitor.HotkeyPressed += subscriber.Handle;

        var loopThreadId = Environment.CurrentManagedThreadId;
        monitor.InvokeHotkeyForTests(3);

        await Assert.That(subscriber.WaitUntilEntered()).IsTrue();
        await Assert.That(subscriber.Finished).IsFalse();
        await Assert.That(subscriber.ThreadId).IsNotEqualTo(loopThreadId);

        await Assert.That(subscriber.ReleaseAndWait()).IsTrue();
    }

    // Исключение подписчика обязано остаться внутри задания: на потоке пула оно иначе становится
    // необработанным. Колбэк при этом не должен ни бросить, ни изменить решение по цепочке хуков.
    [Test]
    public async Task ASubscriberThatThrows_DoesNotEscapeTheCallback()
    {
        using var monitor = new Win32MouseHookMonitor(NullLogger<Win32MouseHookMonitor>.Instance);
        var thrown = new ManualResetEventSlim(false);
        monitor.HotkeyPressed += _ =>
        {
            thrown.Set();
            throw new InvalidOperationException("подписчик сломался");
        };

        monitor.InvokeHookForTests(
            [new MouseHookBinding(1, HotkeyModifiers.None, MouseButton.Middle)],
            User32Native.WM_MBUTTONDOWN);

        await Assert.That(thrown.Wait(Ceiling)).IsTrue();
    }
}
