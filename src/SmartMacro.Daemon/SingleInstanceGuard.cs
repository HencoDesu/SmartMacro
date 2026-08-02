namespace SmartMacro.Daemon;

/// <summary>
/// Named-mutex lock that lets exactly one daemon process run at a time.
/// </summary>
/// <remarks>
/// <para>
/// Two daemons on one machine would each register the same global hotkeys (the second
/// silently losing the race), each poll for game processes, and each drive input into the
/// same windows — so the second instance must not merely warn, it must exit.
/// </para>
/// <para>
/// The name is deliberately <c>Global\</c>-prefixed rather than <c>Local\</c>. <c>Local\</c>
/// scopes the mutex to a terminal-services session, which is exactly the guarantee we do NOT
/// want: the daemon's contended resources — <c>RegisterHotKey</c>, the WH_MOUSE_LL hook, the
/// game clients themselves — are machine-wide, and an elevated instance started from a
/// different session (a scheduled task, a second RDP login, "run as another user") would slip
/// past a session-scoped guard and fight the first one. <c>Global\</c> requires no special
/// privilege to create, and the daemon runs elevated anyway.
/// </para>
/// <para>
/// Public rather than internal so the daemon's tests can exercise the acquire/release
/// semantics against a throwaway name.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Mutex name the daemon actually uses.</summary>
    public const string DaemonMutexName = @"Global\SmartMacro.Daemon";

    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// Tries to become the single instance identified by <paramref name="mutexName"/>.
    /// </summary>
    /// <returns>
    /// The guard when this process won the race (dispose it to release), or <c>null</c> when
    /// another process already holds it.
    /// </returns>
    public static SingleInstanceGuard? TryAcquire(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var mutex = new Mutex(initiallyOwned: false, mutexName);
        bool acquired;
        try
        {
            // Zero timeout: this is a race we either win outright or concede.
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing (crash, kill). The mutex is ours
            // now — that's precisely the state we want, and there's no shared data to
            // distrust because the mutex protects a process identity, not a data structure.
            acquired = true;
        }

        if (acquired)
        {
            return new SingleInstanceGuard(mutex);
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
            // Not the owning thread (finalisation order, or Dispose from a pool thread).
            // Disposing the handle releases it anyway when the process exits.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
