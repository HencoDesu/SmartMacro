using Avalonia.Headless;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Вход в поток UI: <c>await Ui.RunAsync(() =&gt; …)</c>.
///
/// <b>Почему помощник, а не атрибут.</b> У Avalonia.Headless 11.2.3 есть готовые переходники к
/// NUnit и xUnit и нет переходника к TUnit. Свой можно было бы собрать на
/// <c>TestExecutorAttribute&lt;T&gt;</c>, и тогда всё тело теста ехало бы на поток диспетчера —
/// вместе с машинерией ожиданий самого TUnit. Цена ошибки там — взаимная блокировка на чужом
/// синхронизационном контексте, которую ловят не по сообщению, а по зависшему прогону.
/// Помощник ставит границу там, где она понятна: на диспетчер уезжает только тело, которое
/// трогает контролы, а TUnit остаётся снаружи и работает как во всех остальных 769 тестах.
///
/// <b>Сессия одна на процесс, и это не оптимизация.</b> <see cref="HeadlessUnitTestSession"/>
/// заводит собственный поток диспетчера; второй такой в одном процессе означал бы два
/// <c>AvaloniaLocator</c>'а и гонку за глобальное состояние Avalonia. Тесты TUnit идут
/// параллельно, но <see cref="HeadlessUnitTestSession.Dispatch(Action, CancellationToken)"/>
/// выстраивает их в очередь к одному потоку — то есть UI-тесты сериализуются между собой и не
/// мешают всем прочим.
///
/// ⚠️ Вкладывать <c>RunAsync</c> в <c>RunAsync</c> нельзя: внутренний вызов встанет в очередь к
/// потоку, который его же и ждёт. Нужно из одного тела вызвать другое — выносите общий код в
/// обычный метод, принимающий уже готовые контролы.
/// </summary>
public static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(UiTestApp)), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Выполняет <paramref name="body"/> на потоке UI и дожидается конца.</summary>
    public static Task RunAsync(Action body) => Session.Value.Dispatch(body, CancellationToken.None);

    /// <summary>То же для асинхронного тела.</summary>
    public static Task RunAsync(Func<Task> body) => Session.Value.Dispatch(
        async () =>
        {
            await body();
            return 0;
        },
        CancellationToken.None);

    /// <summary>То же, но со значением: пригодно, когда мерить удобнее уже снаружи.</summary>
    public static Task<T> RunAsync<T>(Func<T> body) => Session.Value.Dispatch(body, CancellationToken.None);

    /// <summary>
    /// Останавливает поток диспетчера в конце прогона. Без этого он остался бы жить фоновым
    /// потоком до выхода процесса — не смертельно, но «сессия закрывается сама» проще
    /// объяснить, чем «сессия живёт вечно и это ничего».
    /// </summary>
    [After(TestSession)]
    public static void StopSession()
    {
        if (!Session.IsValueCreated)
        {
            return;
        }

        // Общие сцены — это настоящие окна на этом же потоке, так что закрывать их надо ДО
        // остановки диспетчера и обязательно из него.
        Session.Value.Dispatch(UiScene.DisposeShared, CancellationToken.None).GetAwaiter().GetResult();
        Session.Value.Dispose();
    }
}
