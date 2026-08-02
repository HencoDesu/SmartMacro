namespace SmartMacro.App;

/// <summary>
/// Named-mutex lock that lets exactly one panel process run at a time.
///
/// Unlike the daemon's guard this one is <c>Local\</c>-scoped, and deliberately so: what it
/// protects is a WINDOW the user is looking at, which belongs to one interactive session.
/// Two panels in two sessions are not a conflict — two daemons would be, because their
/// contended resources (global hotkeys, the mouse hook, the game clients) are machine-wide.
///
/// A losing instance does not merely exit: it asks the daemon to push
/// <c>ActivateWindow</c> so the panel already on screen comes forward, which is what makes
/// re-launching the shortcut feel like "show me the panel" instead of nothing happening.
///
/// It is a near-copy of <c>SmartMacro.Daemon.SingleInstanceGuard</c>, and stays one on
/// purpose — sharing it would mean either a reference from the UI process to the daemon
/// assembly (which drags Core, OpenCV and Tesseract back in — the whole point of stage 3)
/// or a process-lifecycle utility in Contracts, which is a vocabulary assembly.
/// </summary>
internal sealed class SingleInstanceLock : IDisposable
{
    /// <summary>Mutex name the panel actually uses.</summary>
    public const string AppMutexName = @"Local\SmartMacro.App";

    private Mutex? _mutex;

    private SingleInstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Tries to become the single instance identified by <paramref name="mutexName"/>.
    /// </summary>
    /// <returns>The lock when this process won, or <c>null</c> when another already holds it.</returns>
    public static SingleInstanceLock? TryAcquire(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var mutex = new Mutex(initiallyOwned: false, mutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous panel died without releasing. The mutex is ours now, which is
            // exactly the state we want — it guards a process identity, not shared data.
            acquired = true;
        }

        if (acquired)
        {
            return new SingleInstanceLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread. The handle's disposal releases it anyway.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
