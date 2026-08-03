using SmartMacro.Contracts.Settings;
using SmartMacro.Settings;

namespace SmartMacro.Tests.Settings;

/// <summary>
/// Настройки без файла: подделка <see cref="ISettingsSource"/> для тех потребителей, которым
/// нужны только значения.
///
/// Существует ровно затем, зачем и сам интерфейс: <c>ProcessMonitor</c>,
/// <c>WindowLifetimeMonitor</c> и <c>ClassMatcher</c> читают снимок по месту, и проверять их
/// поведение через настоящее хранилище значило бы заводить временную папку и наблюдателя за
/// файлом ради двух чисел.
///
/// <see cref="Set"/> поднимает событие — тем же способом, каким это делает настоящее хранилище
/// после записи или правки файла блокнотом, — так что «живость» настройки проверяется тут же.
/// </summary>
internal sealed class FakeSettingsSource : ISettingsSource
{
    private AppSettings _current;

    public FakeSettingsSource(AppSettings? initial = null) => _current = initial ?? AppSettings.Default;

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Current => _current;

    /// <summary>Подменяет снимок и поднимает событие — ровно как настоящее хранилище.</summary>
    public void Set(AppSettings settings)
    {
        _current = settings;
        SettingsChanged?.Invoke(settings);
    }
}
