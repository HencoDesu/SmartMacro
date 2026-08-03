using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Settings;
using SmartMacro.Input;
using SmartMacro.Native.Keyboard;

namespace SmartMacro.Tests.Settings;

// Главное обещание волны: настройка применяется БЕЗ перезапуска демона.
//
// Держится оно на одном правиле — потребитель читает ISettingsSource.Current в момент
// использования и никогда не запоминает значение в поле. Правило структурное, и сломать его легко
// (достаточно «оптимизировать» чтение в конструктор), поэтому здесь оно закреплено на том
// потребителе, которого можно прогнать без Win32 и без OpenCV: на выборе способа ввода.
//
// Остальные читатели — ProcessMonitor (интервал и список имён на каждом тике), GameWindow (порог
// и темп опроса на каждом сопоставлении), WindowLifetimeMonitor, ClassMatcher — устроены точно так
// же, но проверяются живьём: у них за спиной таймеры, окна и OpenCV.
public class LiveSettingsTests
{
    [Test]
    public async Task KeyboardResolver_FollowsTheSettingsWithoutBeingRecreated()
    {
        var settings = new FakeSettingsSource(AppSettings.Default with
        {
            Input = new InputSettings { DefaultMethod = InputMethod.SendMessage },
        });
        var resolver = new KeyboardInputResolver(settings, NullLogger<KeyboardInputResolver>.Instance);

        await Assert.That(resolver.For("elementclient_64")).IsTypeOf<SendMessageKeyboardInput>();

        settings.Set(AppSettings.Default with
        {
            Input = new InputSettings { DefaultMethod = InputMethod.PostMessage },
        });

        // Тот же объект резолвера — новый способ. Ровно это и означает «применяется со следующего
        // цикла активации», а не «со следующего запуска демона».
        await Assert.That(resolver.For("elementclient_64")).IsTypeOf<PostMessageKeyboardInput>();
    }

    [Test]
    public async Task KeyboardResolver_PrefersTheProfileOverTheDefault()
    {
        var settings = new FakeSettingsSource(new AppSettings
        {
            Input = new InputSettings { DefaultMethod = InputMethod.SendMessage },
            Profiles =
            [
                new ProcessProfileSettings { ProcessName = "быстрый", InputMethod = InputMethod.PostMessage },
            ],
        });
        var resolver = new KeyboardInputResolver(settings, NullLogger<KeyboardInputResolver>.Instance);

        await Assert.That(resolver.For("быстрый")).IsTypeOf<PostMessageKeyboardInput>();
        await Assert.That(resolver.For("обычный")).IsTypeOf<SendMessageKeyboardInput>();
    }

    // SendInput объявлен в модели, но не реализован; интерфейс его не предлагает, так что попасть
    // сюда можно только правкой файла руками. Подменять способ молча нельзя — симптомом была бы
    // «настройка, которая ни на что не влияет», — поэтому откат идёт на SendMessage и с записью в
    // журнал (о ней говорит комментарий у резолвера; здесь закреплён сам откат).
    [Test]
    public async Task KeyboardResolver_FallsBackWhenTheFileNamesTheUnimplementedMethod()
    {
        var settings = new FakeSettingsSource(AppSettings.Default with
        {
            Input = new InputSettings { DefaultMethod = InputMethod.SendInput },
        });
        var resolver = new KeyboardInputResolver(settings, NullLogger<KeyboardInputResolver>.Instance);

        await Assert.That(resolver.For("elementclient_64")).IsTypeOf<SendMessageKeyboardInput>();
    }
}
