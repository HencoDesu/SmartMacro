using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Шаг 1: сессия поднимается, меряет и никого не будит.
///
/// Проверок ровно две, и обе — про саму площадку, а не про интерфейс. Тест на измерение
/// доказывает, что поток диспетчера жив и что <c>Measure</c> возвращает не ноль (в headless без
/// шрифтов он вернул бы именно ноль). Тест на цикл приложения закрывает единственную неприятную
/// возможность настоящего <see cref="App.App"/> в роли тестового: если бы headless заводил
/// классический настольный цикл, панель принялась бы поднимать собственное <c>MainWindow</c> —
/// с таймером, подпиской на IPC и режимом завершения по закрытию окна.
/// </summary>
public class UiSessionTests
{
    [Test]
    public async Task ATextBlockMeasuresToANonZeroSize()
    {
        var size = await Ui.RunAsync(() =>
        {
            var text = new TextBlock { Text = "Горячая клавиша", FontFamily = FontFamily.Default, FontSize = 12 };
            text.Measure(Size.Infinity);
            return text.DesiredSize;
        });

        await Assert.That(size.Width).IsGreaterThan(0);
        await Assert.That(size.Height).IsGreaterThan(0);
    }

    [Test]
    public async Task TheHeadlessSessionDoesNotBringUpTheRealMainWindow()
    {
        var (application, lifetime) = await Ui.RunAsync(
            () => (Application.Current, Application.Current?.ApplicationLifetime));

        // Приложение — есть (иначе не было бы ни ресурсов, ни стилей), цикла — НЕТ вовсе. Это
        // и делает настоящее App безопасным в роли тестового: весь его
        // OnFrameworkInitializationCompleted спрятан за проверкой на
        // IClassicDesktopStyleApplicationLifetime, которая при null не проходит. Сорвись это
        // условие — панель на каждом Dispatch поднимала бы своё MainWindow со всеми пятью
        // видами и секундным таймером.
        await Assert.That(application).IsNotNull();
        await Assert.That(lifetime).IsNull();
    }
}
