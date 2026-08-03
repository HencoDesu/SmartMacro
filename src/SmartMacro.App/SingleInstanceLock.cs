namespace SmartMacro.App;

/// <summary>
/// Замок на именованном мьютексе, дающий работать ровно одному процессу панели за раз.
///
/// В отличие от стража демона, этот живёт в области <c>Local\</c>, и это осознанно: защищает он
/// ОКНО, на которое смотрит пользователь, а окно принадлежит одному интерактивному сеансу. Две
/// панели в двух сеансах — не конфликт; два демона были бы конфликтом, потому что ресурсы, за
/// которые они дерутся (глобальные хоткеи, мышиный хук, клиенты игры), общемашинные.
///
/// Проигравший экземпляр не просто завершается: он просит демона разослать
/// <c>ActivateWindow</c>, чтобы уже открытая панель вышла на передний план. Именно от этого
/// повторный запуск ярлыка ощущается как «покажи мне панель», а не как «ничего не произошло».
///
/// Это почти копия <c>SmartMacro.Daemon.SingleInstanceGuard</c>, и остаётся ею намеренно:
/// обобщить их значило бы либо сослаться из процесса UI на сборку демона (а она тянет назад
/// Core, OpenCV и Tesseract — то самое, ради избавления от чего делалась стадия 3), либо
/// положить утилиту жизненного цикла процесса в Contracts, который является сборкой словаря.
/// </summary>
internal sealed class SingleInstanceLock : IDisposable
{
    /// <summary>Имя мьютекса, которым панель пользуется на самом деле.</summary>
    public const string AppMutexName = @"Local\SmartMacro.App";

    private Mutex? _mutex;

    private SingleInstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Пытается стать единственным экземпляром, обозначенным именем <paramref name="mutexName"/>.
    /// </summary>
    /// <returns>Замок, если этот процесс выиграл, или <c>null</c>, если им уже владеет другой.</returns>
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
            // Предыдущая панель умерла, не освободив мьютекс. Теперь он наш, и это ровно то
            // состояние, которое нам нужно: он стережёт идентичность процесса, а не общие данные.
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
            // Поток не тот, что владеет мьютексом. Освобождение дескриптора всё равно его отпустит.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
