using SmartMacro.Daemon;

namespace SmartMacro.Tests.Daemon;

// Stage 2A: the daemon refuses to run twice. Everything here uses a throwaway mutex name so
// the tests never collide with a real daemon (or with each other under TUnit's default
// parallelism) — the production name is asserted separately, not acquired.
//
// Win32 mutex ownership is THREAD-affine and re-entrant, which shapes every test below:
// acquire and dispose are kept on one thread with no `await` between them (after an await
// the continuation can land on a different pool thread, making ReleaseMutex a no-op), and
// the contention test uses a dedicated thread rather than Task.Run — the pool would happily
// hand it the very thread that already owns the mutex, and the second acquisition would
// succeed. That's the same reason the daemon takes the mutex on its Main thread.
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
        // Global\, not Local\: the resources the daemon contends for (RegisterHotKey, the
        // WH_MOUSE_LL hook, the game clients) are machine-wide, so a second instance in
        // another terminal-services session must lose the race too.
        await Assert.That(SingleInstanceGuard.DaemonMutexName).StartsWith(@"Global\");
    }
}
