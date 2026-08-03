namespace SmartMacro.Daemon;

/// <summary>
/// Замок на именованном мьютексе, позволяющий работать ровно одному процессу демона за раз.
/// </summary>
/// <remarks>
/// <para>
/// Два демона на одной машине зарегистрировали бы каждый одни и те же глобальные горячие
/// клавиши (второй молча проиграл бы гонку), каждый опрашивал бы процессы игры и каждый гнал
/// бы ввод в одни и те же окна, — поэтому второму экземпляру мало предупредить, он обязан
/// выйти.
/// </para>
/// <para>
/// Префикс <c>Global\</c>, а не <c>Local\</c>, выбран намеренно. <c>Local\</c> ограничивает
/// мьютекс сессией terminal services, а это ровно та гарантия, которая нам НЕ нужна:
/// ресурсы, за которые демон конкурирует, — <c>RegisterHotKey</c>, хук WH_MOUSE_LL, сами
/// клиенты игры — общемашинные, и экземпляр с повышенными правами, запущенный из другой
/// сессии (запланированная задача, второй вход по RDP, «запуск от имени другого
/// пользователя»), проскользнул бы мимо защиты уровня сессии и подрался с первым. Создание
/// <c>Global\</c>-имени не требует особых привилегий, а демон и так работает с повышенными
/// правами.
/// </para>
/// <para>
/// Public, а не internal, чтобы тесты демона могли гонять семантику захвата и освобождения на
/// одноразовом имени.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Имя мьютекса, которым демон пользуется на самом деле.</summary>
    public const string DaemonMutexName = @"Global\SmartMacro.Daemon";

    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// Пытается стать единственным экземпляром, обозначенным именем <paramref name="mutexName"/>.
    /// </summary>
    /// <returns>
    /// Захваченный замок, если этот процесс выиграл гонку (освободить — через Dispose), либо
    /// <c>null</c>, если замком уже владеет другой процесс.
    /// </returns>
    public static SingleInstanceGuard? TryAcquire(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var mutex = new Mutex(initiallyOwned: false, mutexName);
        bool acquired;
        try
        {
            // Нулевой таймаут: эту гонку мы либо выигрываем сразу, либо уступаем.
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Предыдущий владелец умер, не освободив мьютекс (упал, прибили). Теперь мьютекс
            // наш — ровно то состояние, которое нам и нужно, и не доверять тут нечему: мьютекс
            // защищает идентичность процесса, а не структуру данных.
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
            // Поток не владеющий (порядок финализации или Dispose из потока пула).
            // Освобождение хендла всё равно снимет мьютекс при выходе из процесса.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
