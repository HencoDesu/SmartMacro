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
                new ProcessProfileSettings { ProcessName = "elementclient_64" },
                new ProcessProfileSettings { ProcessName = "notepad", InputMethod = InputMethod.SendMessage },
            ],
            Hooks = new Dictionary<string, ProcessHookSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["elementclient_64"] = new()
                {
                    ActivationLParam = 37336,
                    SettleMs = 200,
                    DeactivateMs = 100,
                    On = [HookOn.Capture],
                    Scope = HookLifetime.Run,
                },
            },
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
        await Assert.That(restored.Profiles[1].InputMethod).IsEqualTo(InputMethod.SendMessage);
        await Assert.That(restored.Hooks).Count().IsEqualTo(1);
        await Assert.That(restored.Hooks["elementclient_64"].ActivationLParam).IsEqualTo(37336u);
        await Assert.That(restored.Hooks["elementclient_64"].SettleMs).IsEqualTo(200);
        await Assert.That(restored.Hooks["elementclient_64"].DeactivateMs).IsEqualTo(100);
        await Assert.That(restored.Hooks["elementclient_64"].On).IsEquivalentTo(new[] { HookOn.Capture });
        await Assert.That(restored.Hooks["elementclient_64"].Scope).IsEqualTo(HookLifetime.Run);
    }

    // Отсутствие ХУКА — рабочая семантика («обычный процесс, пробуждение пропускается»), а не
    // «блок забыли». Круг по JSON обязан её сохранять: заведись у notepad хук сам собой — его окна
    // начали бы получать WM_ACTIVATEAPP.
    [Test]
    public async Task Json_KeepsAProcessWithoutAHookWithoutOne()
    {
        var settings = new AppSettings { Profiles = [new ProcessProfileSettings { ProcessName = "notepad" }] };

        var restored = AppSettingsJson.Deserialize(AppSettingsJson.Serialize(settings));

        await Assert.That(restored.Hooks).IsEmpty();
        await Assert.That(restored.FindHook("notepad")).IsNull();
    }

    // Пустой набор On законен и от отсутствия хука отличается: хук есть, числа сохранены, не
    // срабатывает нигде. Потеряй круг по JSON эту разницу — «выключил на время» превратилось бы в
    // «стёр».
    [Test]
    public async Task Json_KeepsAnEmptyOnSetDistinctFromAMissingHook()
    {
        var settings = new AppSettings
        {
            Hooks = new Dictionary<string, ProcessHookSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["elementclient_64"] = new() { ActivationLParam = 37336, SettleMs = 200, On = [] },
            },
        };

        var restored = AppSettingsJson.Deserialize(AppSettingsJson.Serialize(settings));

        var hook = restored.FindHook("elementclient_64")!;
        await Assert.That(hook.On).IsEmpty();
        await Assert.That(hook.Fires(HookOn.Input)).IsFalse();
        await Assert.That(hook.Fires(HookOn.Capture)).IsFalse();
        await Assert.That(hook.ActivationLParam).IsEqualTo(37336u);
    }

    [Test]
    public async Task FindHook_IgnoresCase()
    {
        var settings = new AppSettings
        {
            // Порядковый компаратор — так словарь и приезжает из JSON; поиск обязан быть
            // регистронезависимым независимо от того, с каким компаратором его собрали.
            Hooks = new Dictionary<string, ProcessHookSettings>(StringComparer.Ordinal)
            {
                ["ElementClient_64"] = new() { ActivationLParam = 37336 },
            },
        };

        await Assert.That(settings.FindHook("elementclient_64")?.ActivationLParam).IsEqualTo(37336u);
        await Assert.That(settings.FindHook("notepad")).IsNull();
        await Assert.That(settings.FindHook(null)).IsNull();
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

        // Скобка пробуждения — те же три значения, что лежали полями профиля до этой волны, плюс
        // явно названные умолчания: срабатывает и на вводе, и на захвате, живёт одно действие.
        var hook = settings.FindHook("elementclient_64")!;
        await Assert.That(hook.ActivationLParam).IsEqualTo(37336u);
        await Assert.That(hook.SettleMs).IsEqualTo(200);
        await Assert.That(hook.DeactivateMs).IsEqualTo(100);
        await Assert.That(hook.On).IsEquivalentTo(new[] { HookOn.Input, HookOn.Capture });
        await Assert.That(hook.Scope).IsEqualTo(HookLifetime.Action);
    }

    // Таблица известных сигналов применяется ПРИ СОЗДАНИИ и только к узнанному имени. Это и есть
    // то, что позволило убрать скобку из интерфейса, не потеряв смысл её отсутствия.
    [Test]
    public async Task NewHook_IsCreatedOnlyForKnownNames()
    {
        var game = KnownActivationSignals.NewHook("elementclient_64")!;

        await Assert.That(game.ActivationLParam).IsEqualTo(37336u);
        await Assert.That(game.SettleMs).IsEqualTo(200);
        await Assert.That(game.DeactivateMs).IsEqualTo(100);

        // Незнакомому имени — не хук с нулями, а отсутствие записи: только оно означает «обычный
        // процесс, пробуждение пропускается».
        await Assert.That(KnownActivationSignals.NewHook("notepad")).IsNull();
        await Assert.That(KnownActivationSignals.NewHook(null)).IsNull();
    }

    // ---- старая форма файла ------------------------------------------------------------------
    //
    // До появления блока Hooks сигнал побудки и обе паузы лежали ПОЛЯМИ ПРОФИЛЯ. После смены схемы
    // они стали бы неизвестными членами, а неизвестные члены System.Text.Json молча пропускает —
    // то есть у всех, кто уже пользуется программой, скобка исчезла бы при первом же сохранении из
    // панели. Симптом «макросы перестали работать», причина невидима, вернуть число неоткуда.

    private const string LegacySettings = """
        {
          "Watch": { "ProcessPollIntervalSeconds": 1, "WindowPollIntervalSeconds": 2 },
          "Profiles": [
            {
              "ProcessName": "elementclient_64",
              "ActivationLParam": 37336,
              "SettleDelayMs": 250,
              "DeactivationDelayMs": 90,
              "InputMethod": "PostMessage"
            },
            { "ProcessName": "notepad", "ActivationLParam": null, "SettleDelayMs": 0, "DeactivationDelayMs": 0 }
          ]
        }
        """;

    [Test]
    public async Task Json_AdoptsTheWakeBracketFromTheOldProfileFields()
    {
        var restored = AppSettingsJson.Deserialize(LegacySettings, out var adopted);

        await Assert.That(adopted).IsEqualTo(1);

        var hook = restored.FindHook("elementclient_64")!;
        await Assert.That(hook.ActivationLParam).IsEqualTo(37336u);
        await Assert.That(hook.SettleMs).IsEqualTo(250);
        await Assert.That(hook.DeactivateMs).IsEqualTo(90);
        // Старая скобка срабатывала и на вводе, и на захвате, и жила одно действие: миграция не
        // имеет права поменять поведение заодно.
        await Assert.That(hook.On).IsEquivalentTo(new[] { HookOn.Input, HookOn.Capture });
        await Assert.That(hook.Scope).IsEqualTo(HookLifetime.Action);

        // Профиль без сигнала и был «обычным процессом» — хука ему не полагается.
        await Assert.That(restored.FindHook("notepad")).IsNull();
        // Остальное профиль донёс как обычно.
        await Assert.That(restored.Profiles[0].InputMethod).IsEqualTo(InputMethod.PostMessage);
    }

    // ⚠️ «Ключа нет» и «ключ пуст» — разные вещи, и здесь это несущее различие: пустой "Hooks": {}
    // означает осознанное «скобок нет», и перебить его старыми полями значило бы отменить решение
    // пользователя.
    [Test]
    public async Task Json_DoesNotAdoptLegacyFieldsWhenHooksAreDeclaredEmpty()
    {
        var json = """
            {
              "Hooks": {},
              "Profiles": [
                { "ProcessName": "elementclient_64", "ActivationLParam": 37336, "SettleDelayMs": 250 }
              ]
            }
            """;

        var restored = AppSettingsJson.Deserialize(json, out var adopted);

        await Assert.That(adopted).IsEqualTo(0);
        await Assert.That(restored.Hooks).IsEmpty();
    }

    // Миграция читается, но НЕ переписывается: записанный обратно файл уже в новой форме, и старых
    // полей в нём нет ни одного — иначе следующее чтение имело бы два источника правды.
    [Test]
    public async Task Json_WritesTheAdoptedBracketInTheNewShapeOnly()
    {
        var migrated = AppSettingsJson.Deserialize(LegacySettings);

        var written = AppSettingsJson.Serialize(migrated);

        await Assert.That(written).Contains("\"Hooks\"");
        await Assert.That(written).Contains("\"SettleMs\": 250");
        await Assert.That(written).DoesNotContain("SettleDelayMs");
        await Assert.That(written).DoesNotContain("DeactivationDelayMs");
        await Assert.That(AppSettingsJson.Deserialize(written).FindHook("elementclient_64")!.SettleMs)
            .IsEqualTo(250);
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

    // Границы пауз переехали вместе с самими паузами. Экран их не показывает, так что приехать
    // такое может только из файла, правленного руками, — и пропустить в движок паузу устаканивания
    // в десять минут хуже, чем сказать про поле, которого нет на экране.
    [Test]
    public async Task Validator_RejectsOutOfRangeHookDelays()
    {
        var settings = AppSettings.Default with
        {
            Hooks = new Dictionary<string, ProcessHookSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["elementclient_64"] = new() { ActivationLParam = 37336, SettleMs = -1, DeactivateMs = 999_999 },
            },
        };

        var issues = AppSettingsValidator.Validate(settings);

        await Assert.That(issues.Select(i => i.Field)).Contains("Hooks[elementclient_64].SettleMs");
        await Assert.That(issues.Select(i => i.Field)).Contains("Hooks[elementclient_64].DeactivateMs");
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
