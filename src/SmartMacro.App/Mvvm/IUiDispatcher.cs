using Avalonia.Threading;

namespace SmartMacro.App.Mvvm;

/// <summary>
/// Шов «перенеси меня в поток UI». Core поднимает свои события (окно зарегистрировано, теги
/// изменились, библиотека макросов перечитана, состав прогонов изменился) из произвольных
/// потоков пула, а менять <c>ObservableCollection</c> позволено только из потока UI — значит,
/// каждая VM, подписанная на Core, обязана переложить вызов.
///
/// Интерфейсом он сделан исключительно ради тестируемости: headless-тест VM хочет, чтобы эти
/// обработчики выполнялись здесь же и синхронно, а у <see cref="Dispatcher.UIThread"/> вне
/// работающего приложения Avalonia нет насоса, так что отправленная работа просто никогда бы
/// не выполнилась.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Ставит <paramref name="action"/> в очередь на выполнение в потоке UI. Никогда не блокирует.</summary>
    void Post(Action action);
}

/// <summary>Боевая реализация — отправляет в поток UI Avalonia.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <summary>
    /// Общий экземпляр. Намеренно не трогает <see cref="Dispatcher.UIThread"/> до вызова
    /// <see cref="Post"/>, так что само его создание (как значения по умолчанию у VM)
    /// безопасно в процессе, где приложения Avalonia нет.
    /// </summary>
    public static AvaloniaUiDispatcher Instance { get; } = new();

    private AvaloniaUiDispatcher()
    {
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// Выполняет действие здесь же, в вызывающем потоке. Нужен headless-тестам VM (и безопасен как
/// значение по умолчанию на этапе проектирования) — но не работающему приложению, где он менял
/// бы observable-коллекции вне потока UI.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static ImmediateUiDispatcher Instance { get; } = new();

    public void Post(Action action) => action();
}
