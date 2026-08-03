using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;

namespace SmartMacro.Windows;

// ЕДИНСТВЕННЫЙ владелец тегов окон И таблицы соответствия «hwnd → IGameWindow». Все, кому нужно
// знать, «какие окна есть, какими тегами они помечены и как одним из них управлять» —
// оркестратор, слой примитивов макроса, WindowLifetimeMonitor, UI, — спрашивают реестр; больше
// состояние тегов не держит никто. Теги
// живут только во время работы (hwnd эфемерны), это свободные строки с учётом регистра, которые
// проставляют ноды макроса или руками из UI.
//
// Дескриптор IGameWindow регистрируется рядом с тегами (W0.2b), потому что слой примитивов
// макроса видит один только hwnd — модель графа адресует окна дескрипторами, — и реестр есть
// естественное место, где дескриптор превращается обратно в управляемое окно.
//
// Все изменения атомарны под одной блокировкой; события поднимаются СНАРУЖИ блокировки (снимок
// при этом считается внутри), чтобы подписчики могли вызывать реестр обратно без взаимной
// блокировки. Синглтон в DI.
public sealed partial class WindowRegistry
{
    private sealed class Entry
    {
        public required string ProcessName { get; init; }

        /// <summary>Фасад для управления окном; <c>null</c> у записей, зарегистрированных без него (тесты, строки только для UI).</summary>
        public IGameWindow? Window { get; init; }

        // Порядок вставки сохраняется, чтобы «первый тег» (он и есть отображаемое имя окна в
        // панели) был стабилен. Тегов на окно всё равно единицы, так что List.Contains выигрывает у
        // накладных расходов множества.
        public List<string> Tags { get; } = [];
    }

    private static readonly IReadOnlySet<string> EmptyTags = new HashSet<string>(StringComparer.Ordinal);

    private readonly Lock _lock = new();
    private readonly Dictionary<IntPtr, Entry> _windows = [];
    private readonly ILogger<WindowRegistry> _logger;

    public WindowRegistry(ILogger<WindowRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>Поднимается после регистрации окна. Нагрузка — свежий снимок (пока без тегов).</summary>
    public event Action<ManagedWindowInfo>? WindowAppeared;

    /// <summary>Поднимается после добавления или снятия тега у окна. Нагрузка отражает набор тегов уже после изменения.</summary>
    public event Action<ManagedWindowInfo>? WindowTagsChanged;

    /// <summary>Поднимается после снятия окна с регистрации. Нагрузка несёт тот набор тегов, с которым окно ушло.</summary>
    public event Action<ManagedWindowInfo>? WindowClosed;

    /// <summary>
    /// Добавляет окно в реестр с пустым набором тегов и поднимает <see cref="WindowAppeared"/>.
    /// </summary>
    /// <param name="hwnd">Нативный дескриптор; ключ идентичности записи.</param>
    /// <param name="processName">Имя владеющего процесса, как его сообщил ProcessMonitor.</param>
    /// <param name="window">
    /// Фасад для управления окном, который потом достаётся через <see cref="TryGetWindow"/>.
    /// Необязателен, чтобы тестам про одни только теги и будущим регистрациям со стороны UI он
    /// был не нужен; ноды макроса, нацеленные на окно, зарегистрированное без фасада, падают
    /// во время исполнения.
    /// </param>
    /// <returns><c>true</c>, если окно добавлено; <c>false</c>, если hwnd уже зарегистрирован (события не будет).</returns>
    public bool Register(IntPtr hwnd, string processName, IGameWindow? window = null)
    {
        ArgumentNullException.ThrowIfNull(processName);

        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (_windows.ContainsKey(hwnd))
            {
                LogAlreadyRegistered(hwnd.ToInt64());
                return false;
            }

            var entry = new Entry { ProcessName = processName, Window = window };
            _windows.Add(hwnd, entry);
            snapshot = ToSnapshot(hwnd, entry);
        }

        LogRegistered(hwnd.ToInt64(), processName);
        WindowAppeared?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Убирает окно (и его теги) из реестра и поднимает <see cref="WindowClosed"/> с финальным
    /// набором тегов.
    /// </summary>
    /// <returns><c>true</c>, если окно убрано; <c>false</c>, если такой hwnd зарегистрирован не был (события не будет).</returns>
    public bool Unregister(IntPtr hwnd)
    {
        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (!_windows.Remove(hwnd, out var entry))
            {
                return false;
            }

            snapshot = ToSnapshot(hwnd, entry);
        }

        LogUnregistered(hwnd.ToInt64(), snapshot.ProcessName);
        WindowClosed?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Добавляет тег зарегистрированному окну и поднимает <see cref="WindowTagsChanged"/>.
    /// Регистр в тегах важен; добавление уже имеющегося тега ничего не делает.
    /// </summary>
    /// <returns><c>true</c>, если набор тегов изменился; <c>false</c> при неизвестном hwnd или повторном теге (события не будет).</returns>
    public bool AddTag(IntPtr hwnd, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (!_windows.TryGetValue(hwnd, out var entry))
            {
                LogTagForUnknownWindow(tag, hwnd.ToInt64());
                return false;
            }

            if (entry.Tags.Contains(tag, StringComparer.Ordinal))
            {
                return false;
            }

            entry.Tags.Add(tag);
            snapshot = ToSnapshot(hwnd, entry);
        }

        LogTagAdded(tag, hwnd.ToInt64());
        WindowTagsChanged?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Снимает тег с зарегистрированного окна и поднимает <see cref="WindowTagsChanged"/>.
    /// </summary>
    /// <returns><c>true</c>, если набор тегов изменился; <c>false</c> при неизвестном hwnd или отсутствующем теге (события не будет).</returns>
    public bool RemoveTag(IntPtr hwnd, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        ManagedWindowInfo snapshot;
        lock (_lock)
        {
            if (!_windows.TryGetValue(hwnd, out var entry))
            {
                return false;
            }

            if (!entry.Tags.Remove(tag))
            {
                return false;
            }

            snapshot = ToSnapshot(hwnd, entry);
        }

        LogTagRemoved(tag, hwnd.ToInt64());
        WindowTagsChanged?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Снимок текущих тегов окна. Изолирован — возвращённое множество не меняется, даже если
    /// теги потом изменятся. Неизвестный hwnd даёт пустое множество.
    /// </summary>
    public IReadOnlySet<string> GetTags(IntPtr hwnd)
    {
        lock (_lock)
        {
            return _windows.TryGetValue(hwnd, out var entry)
                ? new HashSet<string>(entry.Tags, StringComparer.Ordinal)
                : EmptyTags;
        }
    }

    /// <summary>
    /// Превращает дескриптор обратно в зарегистрированный вместе с ним фасад управления окном.
    /// Единственная для слоя примитивов макроса дорога от hwnd к настоящему вводу и зрению.
    /// </summary>
    /// <returns>Фасад или <c>null</c> при неизвестном hwnd либо у записи, зарегистрированной без фасада.</returns>
    public IGameWindow? TryGetWindow(IntPtr hwnd)
    {
        lock (_lock)
        {
            return _windows.TryGetValue(hwnd, out var entry) ? entry.Window : null;
        }
    }

    /// <summary>Проверка наличия тега с учётом регистра. Для неизвестных hwnd — <c>false</c>.</summary>
    public bool HasTag(IntPtr hwnd, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        lock (_lock)
        {
            return _windows.TryGetValue(hwnd, out var entry)
                   && entry.Tags.Contains(tag, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Атомарный снимок всех зарегистрированных окон с их тегами. Изолирован от последующих
    /// изменений — по нему можно спокойно ходить, не держа никакой блокировки.
    /// </summary>
    public IReadOnlyList<ManagedWindowInfo> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<ManagedWindowInfo>(_windows.Count);
            foreach (var (hwnd, entry) in _windows)
            {
                result.Add(ToSnapshot(hwnd, entry));
            }

            return result;
        }
    }

    // Вызывающий обязан держать _lock.
    private static ManagedWindowInfo ToSnapshot(IntPtr hwnd, Entry entry) =>
        new(hwnd, entry.ProcessName, new HashSet<string>(entry.Tags, StringComparer.Ordinal));

    [LoggerMessage(LogLevel.Information, "Окно зарегистрировано: hwnd=0x{Hwnd:X} процесс='{ProcessName}'")]
    partial void LogRegistered(long hwnd, string processName);

    [LoggerMessage(LogLevel.Warning, "Окно hwnd=0x{Hwnd:X} уже зарегистрировано — повторный Register проигнорирован")]
    partial void LogAlreadyRegistered(long hwnd);

    [LoggerMessage(LogLevel.Information, "Окно снято с регистрации: hwnd=0x{Hwnd:X} процесс='{ProcessName}'")]
    partial void LogUnregistered(long hwnd, string processName);

    [LoggerMessage(LogLevel.Information, "Тег '{Tag}' добавлен окну hwnd=0x{Hwnd:X}")]
    partial void LogTagAdded(string tag, long hwnd);

    [LoggerMessage(LogLevel.Information, "Тег '{Tag}' снят с окна hwnd=0x{Hwnd:X}")]
    partial void LogTagRemoved(string tag, long hwnd);

    [LoggerMessage(LogLevel.Warning, "Тег '{Tag}' адресован незарегистрированному hwnd=0x{Hwnd:X} — проигнорирован")]
    partial void LogTagForUnknownWindow(string tag, long hwnd);
}
