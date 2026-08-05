using Avalonia;
using Avalonia.Headless;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Точка сборки приложения Avalonia для headless-прогона.
///
/// <b>Приложение здесь — НАСТОЯЩЕЕ <see cref="App.App"/>, а не подделка.</b> Всё, что меряют
/// тесты ниже, приезжает из его композиции: <c>Tokens → FluentBridge → Controls</c> в ресурсах,
/// <c>FluentTheme</c> плюс стиль <c>Window</c> в стилях. Собери здесь свой
/// <c>Application</c> с теми же тремя словарями — и получишь ВТОРОЙ экземпляр правила, которое
/// разъедется молча: отступ поля поправят в теме, а тест продолжит мерить свою копию. Ровно та
/// же беда, что у бейджа целей из D4.
///
/// <see cref="App.App.OnFrameworkInitializationCompleted"/> здесь безвреден: он делает что-либо
/// только при <c>IClassicDesktopStyleApplicationLifetime</c>, а headless-сессия такого цикла не
/// заводит. Проверяется это не рассуждением, а тестом
/// <c>TheHeadlessSessionDoesNotBringUpTheRealMainWindow</c>.
///
/// Три звена сборщика и почему каждое:
/// <list type="bullet">
///   <item><c>UseSkia</c> — настоящая отрисовка. Без неё <c>CaptureRenderedFrame</c> вернёт
///   пустоту, а шаг с чернилами глифов невозможен вовсе.</item>
///   <item><c>WithInterFont</c> — ровно то же звено, что в <c>Program.BuildAvaloniaApp</c>.
///   Токен <c>NocturneFontFamily</c> — это <c>fonts:Inter#Inter</c>, встроенная коллекция
///   пакета; без этого вызова адрес не разрешается, и всякий замер ширины мерит запасной
///   шрифт. Что он разрешился именно в Inter, проверяет <c>UiFontTests</c>.</item>
///   <item><c>UseHeadlessDrawing = false</c> — иначе платформа подсовывает пустышку вместо
///   рисовальщика, и кадр не с чем сравнивать.</item>
/// </list>
/// </summary>
public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
