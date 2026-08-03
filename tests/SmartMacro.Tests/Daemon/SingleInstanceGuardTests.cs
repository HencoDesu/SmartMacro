using SmartMacro.Daemon;

namespace SmartMacro.Tests.Daemon;

// Стадия 2A: демон отказывается запускаться дважды. Всё здесь работает на одноразовом имени
// мьютекса, чтобы тесты никогда не столкнулись ни с настоящим демоном, ни друг с другом при
// параллелизме TUnit по умолчанию, — боевое имя проверяется отдельно, а не захватывается.
//
// Владение мьютексом Win32 привязано к ПОТОКУ и реентерабельно, и это определяет форму каждого
// теста ниже: захват и освобождение держатся на одном потоке без `await` между ними (после await
// продолжение может уехать на другой поток пула, и тогда ReleaseMutex превращается в пустышку),
// а тест на конкуренцию берёт выделенный поток, а не Task.Run, — пул с удовольствием выдал бы
// ровно тот поток, который мьютексом уже владеет, и второй захват прошёл бы успешно. По той же
// причине демон берёт мьютекс на своём потоке Main.
public class SingleInstanceGuardTests
{
    private static string UniqueName() => $@"Local\SmartMacro.Tests.{Guid.NewGuid():N}";

    private static SingleInstanceGuard? AcquireOnAnotherThread(string name)
    {
        SingleInstanceGuard? result = null;
        var thread = new Thread(() =>
        {
            result = SingleInstanceGuard.TryAcquire(name);
            result?.Dispose();
        })
        {
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        return result;
    }

    [Test]
    public async Task TryAcquire_WhenFree_ReturnsGuard()
    {
        var guard = SingleInstanceGuard.TryAcquire(UniqueName());
        guard?.Dispose();

        await Assert.That(guard).IsNotNull();
    }

    [Test]
    public async Task TryAcquire_WhenAlreadyHeld_ReturnsNull()
    {
        var name = UniqueName();

        var first = SingleInstanceGuard.TryAcquire(name);
        var second = AcquireOnAnotherThread(name);
        first?.Dispose();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Dispose_ReleasesTheName_SoTheNextInstanceCanStart()
    {
        var name = UniqueName();

        var first = SingleInstanceGuard.TryAcquire(name);
        first?.Dispose();
        var second = SingleInstanceGuard.TryAcquire(name);
        second?.Dispose();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
    }

    [Test]
    public async Task Dispose_IsIdempotent()
    {
        var guard = SingleInstanceGuard.TryAcquire(UniqueName());
        guard?.Dispose();
        guard?.Dispose();

        await Assert.That(guard).IsNotNull();
    }

    [Test]
    public async Task TryAcquire_RejectsBlankNames()
    {
        await Assert.That(() => SingleInstanceGuard.TryAcquire("")).Throws<ArgumentException>();
        await Assert.That(() => SingleInstanceGuard.TryAcquire("   ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task DaemonMutexName_IsMachineWide()
    {
        // Global\, а не Local\: ресурсы, за которые демон конкурирует (RegisterHotKey, хук
        // WH_MOUSE_LL, клиенты игры), общемашинные, так что второй экземпляр в другом сеансе
        // служб терминалов обязан проиграть гонку тоже.
        await Assert.That(SingleInstanceGuard.DaemonMutexName).StartsWith(@"Global\");
    }
}
