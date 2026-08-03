using SmartMacro.Contracts.Settings;

namespace SmartMacro.Tests.Settings;

// Модель настроек: круг по JSON, поиск профиля и объединение отслеживаемых имён — то, на чём
// стоят ProcessMonitor и GameWindowFactory, — плюс правило разрешения способа ввода.
//
// Пришли на смену ProcessProfileOptionsTests: секции "ProcessProfiles" в appsettings.json больше
// нет, профили живут в settings.json, а привязка через IConfiguration — в хранилище. Проверяемые
// правила при этом те же самые.
public class AppSettingsTests
{
    [Test]
    public async Task Json_RoundTripsEveryField()
    {
        var settings = new AppSettings
        {
            Watch = new WatchSettings { ProcessPollIntervalSeconds = 3, WindowPollIntervalSeconds = 5 },
            Input = new InputSettings { DefaultMethod = InputMethod.PostMessage },
            Vision = new VisionSettings
            {
                MatchThreshold = 0.55,
                PollIntervalMs = 250,
                ClassMatcher = new ClassMatcherSettings { LuminanceThreshold = 180, MatchThreshold = 0.42 },
            },
            Startup = new StartupSettings { RunAtLogon = true, RunElevated = false },
            Profiles =
            [
                new ProcessProfileSettings
                {
                    ProcessName = "elementclient_64",
                    ActivationLParam = 37336,
                    SettleDelayMs = 200,
                    DeactivationDelayMs = 100,
                },
                new ProcessProfileSettings { ProcessName = "notepad", InputMethod = InputMethod.SendMessage },
            ],
        };

        var restored = AppSettingsJson.Deserialize(AppSettingsJson.Serialize(settings));

        await Assert.That(restored.Watch.ProcessPollIntervalSeconds).IsEqualTo(3);
        await Assert.That(restored.Watch.WindowPollIntervalSeconds).IsEqualTo(5);
        await Assert.That(restored.Input.DefaultMethod).IsEqualTo(InputMethod.PostMessage);
        await Assert.That(restored.Vision.MatchThreshold).IsEqualTo(0.55);
        await Assert.That(restored.Vision.PollIntervalMs).IsEqualTo(250);
        await Assert.That(restored.Vision.ClassMatcher.LuminanceThreshold).IsEqualTo(180);
        await Assert.That(restored.Startup.RunAtLogon).IsTrue();
        await Assert.That(restored.Startup.RunElevated).IsFalse();
        await Assert.That(restored.Profiles).Count().IsEqualTo(2);
        await Assert.That(restored.Profiles[0].ActivationLParam).IsEqualTo(37336u);
        await Assert.That(restored.Profiles[1].InputMethod).IsEqualTo(InputMethod.SendMessage);
    }

    // Отсутствие ActivationLParam — рабочая семантика («обычный процесс, пробуждение
    // пропускается»), а не «поле забыли». Круг по JSON обязан её сохранять: потеряй мы null и
    // подставь что-нибудь — профили не-игровых процессов начали бы слать окнам WM_ACTIVATEAPP.
    [Test]
    public async Task Json_KeepsMissingActivationSignalAsNull()
    {
        var settings = new AppSettings { Profiles = [new ProcessProfileSettings { ProcessName = "notepad" }] };

        var restored = AppSettingsJson.Deserialize(AppSettingsJson.Serialize(settings));

        await Assert.That(restored.Profiles[0].ActivationLParam).IsNull();
    }

    [Test]
    public async Task FindProfile_IgnoresCase()
    {
        var settings = new AppSettings
        {
            Profiles = [new ProcessProfileSettings { ProcessName = "ElementClient_64" }],
        };

        await Assert.That(settings.FindProfile("elementclient_64")?.ProcessName).IsEqualTo("ElementClient_64");
        await Assert.That(settings.FindProfile("notepad")).IsNull();
        await Assert.That(settings.FindProfile(null)).IsNull();
    }

    [Test]
    public async Task WatchedProcessNames_DeduplicatesAndSkipsBlanks()
    {
        var settings = new AppSettings
        {
            Profiles =
            [
                new ProcessProfileSettings { ProcessName = "elementclient_64" },
                new ProcessProfileSettings { ProcessName = "  " },
                new ProcessProfileSettings { ProcessName = "ELEMENTCLIENT_64" },
                new ProcessProfileSettings { ProcessName = "notepad" },
            ],
        };

        await Assert.That(settings.WatchedProcessNames()).IsEquivalentTo(new[] { "elementclient_64", "notepad" });
    }

    [Test]
    public async Task InputMethodFor_PrefersProfileOverDefault()
    {
        var settings = new AppSettings
        {
            Input = new InputSettings { DefaultMethod = InputMethod.SendMessage },
            Profiles =
            [
                new ProcessProfileSettings { ProcessName = "custom", InputMethod = InputMethod.PostMessage },
                new ProcessProfileSettings { ProcessName = "inherits" },
            ],
        };

        await Assert.That(settings.InputMethodFor("custom")).IsEqualTo(InputMethod.PostMessage);
        await Assert.That(settings.InputMethodFor("inherits")).IsEqualTo(InputMethod.SendMessage);
        // Процесс без профиля вообще — тоже умолчание.
        await Assert.That(settings.InputMethodFor("stranger")).IsEqualTo(InputMethod.SendMessage);
    }

    // Умолчание должно повторять то, что лежало в appsettings.json до этой волны: появление
    // экрана настроек не имеет права менять поведение работающей установки.
    [Test]
    public async Task Default_MatchesThePreviousAppsettingsContent()
    {
        var settings = AppSettings.Default;

        await Assert.That(settings.Watch.ProcessPollIntervalSeconds).IsEqualTo(1);
        await Assert.That(settings.Watch.WindowPollIntervalSeconds).IsEqualTo(2);
        await Assert.That(settings.Input.DefaultMethod).IsEqualTo(InputMethod.SendMessage);
        await Assert.That(settings.Profiles).Count().IsEqualTo(1);
        await Assert.That(settings.Profiles[0].ProcessName).IsEqualTo("elementclient_64");
        await Assert.That(settings.Profiles[0].ActivationLParam).IsEqualTo(37336u);
        await Assert.That(settings.Profiles[0].SettleDelayMs).IsEqualTo(200);
        await Assert.That(settings.Profiles[0].DeactivationDelayMs).IsEqualTo(100);
    }

    // Таблица известных сигналов применяется ПРИ СОЗДАНИИ и только к узнанному имени. Это и есть
    // то, что позволило убрать число из интерфейса, не потеряв смысл его отсутствия.
    [Test]
    public async Task NewProfile_FillsTheWakeSignalOnlyForKnownNames()
    {
        var game = KnownActivationSignals.NewProfile("elementclient_64");
        var other = KnownActivationSignals.NewProfile("notepad");

        await Assert.That(game.ActivationLParam).IsEqualTo(37336u);
        await Assert.That(game.SettleDelayMs).IsEqualTo(200);
        await Assert.That(game.DeactivationDelayMs).IsEqualTo(100);

        await Assert.That(other.ActivationLParam).IsNull();
        // Паузы без сигнала побудки не нужны: GameWindow пропускает их целиком.
        await Assert.That(other.SettleDelayMs).IsEqualTo(0);
        await Assert.That(other.DeactivationDelayMs).IsEqualTo(0);
    }

    [Test]
    public async Task Validator_AcceptsDefaults()
    {
        await Assert.That(AppSettingsValidator.Validate(AppSettings.Default)).IsEmpty();
    }

    [Test]
    public async Task Validator_RejectsOutOfRangeIntervalsAndThresholds()
    {
        var settings = AppSettings.Default with
        {
            Watch = new WatchSettings { ProcessPollIntervalSeconds = 0, WindowPollIntervalSeconds = 900 },
            Vision = new VisionSettings { MatchThreshold = 1.5, PollIntervalMs = 5 },
        };

        var issues = AppSettingsValidator.Validate(settings);

        await Assert.That(issues.Select(i => i.Field)).Contains("Watch.ProcessPollIntervalSeconds");
        await Assert.That(issues.Select(i => i.Field)).Contains("Watch.WindowPollIntervalSeconds");
        await Assert.That(issues.Select(i => i.Field)).Contains("Vision.MatchThreshold");
        await Assert.That(issues.Select(i => i.Field)).Contains("Vision.PollIntervalMs");
    }

    // «notepad.exe» отвергается, а не срезается молча: Process.GetProcessesByName с расширением
    // не находит ничего, и профиль, который выглядит рабочим, не следил бы ни за чем.
    [Test]
    public async Task Validator_RejectsProcessNameWithExtension()
    {
        var settings = AppSettings.Default with
        {
            Profiles = [new ProcessProfileSettings { ProcessName = "notepad.exe" }],
        };

        var issues = AppSettingsValidator.Validate(settings);

        await Assert.That(issues).IsNotEmpty();
        await Assert.That(issues[0].Message).Contains("notepad");
    }

    [Test]
    public async Task Validator_RejectsDuplicateProfilesRegardlessOfCase()
    {
        var settings = AppSettings.Default with
        {
            Profiles =
            [
                new ProcessProfileSettings { ProcessName = "elementclient_64" },
                new ProcessProfileSettings { ProcessName = "ELEMENTCLIENT_64" },
            ],
        };

        var issues = AppSettingsValidator.Validate(settings);

        await Assert.That(issues.Select(i => i.Field)).Contains("Profiles[1].ProcessName");
    }

    // Текст подсказки про способ ввода обязан жить в ОДНОМ месте: копия в разметке разъехалась бы
    // незаметно. Тест закрепляет и это, и то, что нереализованный способ интерфейсу не предлагают.
    [Test]
    public async Task InputMethodInfo_DescribesEveryMethodAndHidesTheUnimplementedOne()
    {
        await Assert.That(InputMethodInfo.All).Count().IsEqualTo(3);
        await Assert.That(InputMethodInfo.All.All(info => info.Detail.Length > 0)).IsTrue();
        await Assert.That(InputMethodInfo.All.All(info => info.Badge.Length > 0)).IsTrue();

        await Assert.That(InputMethodInfo.Available.Select(info => info.Method))
            .IsEquivalentTo(new[] { InputMethod.SendMessage, InputMethod.PostMessage });
        await Assert.That(InputMethodInfo.Of(InputMethod.SendInput).IsAvailable).IsFalse();
    }
}
