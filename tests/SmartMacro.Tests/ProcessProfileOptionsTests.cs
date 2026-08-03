using Microsoft.Extensions.Configuration;
using SmartMacro.Config;

namespace SmartMacro.Tests;

// W0.1: ProcessProfiles — привязка из конфигурации (секция "ProcessProfiles" представляет собой
// голый массив JSON и привязывается к ProcessProfileOptions.Profiles тем же способом, каким это
// делает Program.cs) плюс помощники поиска профиля и объединения отслеживаемых имён, на которые
// опираются ProcessMonitor и GameWindowFactory.
public class ProcessProfileOptionsTests
{
    private static ProcessProfileOptions BindFromMemory(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        // Повторяет привязку в корне композиции из Program.cs.
        var options = new ProcessProfileOptions();
        configuration.GetSection(ProcessProfileOptions.SectionName).Bind(options.Profiles);
        return options;
    }

    [Test]
    public async Task Bind_FromInMemoryConfiguration_BindsAllFields()
    {
        var options = BindFromMemory(new Dictionary<string, string?>
        {
            ["ProcessProfiles:0:ProcessName"] = "elementclient_64",
            ["ProcessProfiles:0:ActivationLParam"] = "37336",
            ["ProcessProfiles:0:SettleDelayMs"] = "200",
            ["ProcessProfiles:0:DeactivationDelayMs"] = "100",
            ["ProcessProfiles:1:ProcessName"] = "notepad",
        });

        await Assert.That(options.Profiles).Count().IsEqualTo(2);

        var pw = options.Profiles[0];
        await Assert.That(pw.ProcessName).IsEqualTo("elementclient_64");
        await Assert.That(pw.ActivationLParam).IsEqualTo(37336u);
        await Assert.That(pw.SettleDelayMs).IsEqualTo(200);
        await Assert.That(pw.DeactivationDelayMs).IsEqualTo(100);

        // ActivationLParam не задан — значит, ввод обычный, без плясок с пробуждением.
        var plain = options.Profiles[1];
        await Assert.That(plain.ProcessName).IsEqualTo("notepad");
        await Assert.That(plain.ActivationLParam).IsNull();
        await Assert.That(plain.SettleDelayMs).IsEqualTo(0);
        await Assert.That(plain.DeactivationDelayMs).IsEqualTo(0);
    }

    [Test]
    public async Task Bind_MissingSection_YieldsEmptyProfiles()
    {
        var options = BindFromMemory([]);

        await Assert.That(options.Profiles).IsEmpty();
        await Assert.That(options.GetWatchedProcessNames()).IsEmpty();
    }

    [Test]
    public async Task FindByProcessName_MatchesCaseInsensitively()
    {
        var options = new ProcessProfileOptions
        {
            Profiles =
            {
                new ProcessProfile { ProcessName = "ElementClient_64", ActivationLParam = 1 },
            },
        };

        var found = options.FindByProcessName("elementclient_64");

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.ActivationLParam).IsEqualTo(1u);
    }

    [Test]
    public async Task FindByProcessName_Unknown_ReturnsNull()
    {
        var options = new ProcessProfileOptions
        {
            Profiles = { new ProcessProfile { ProcessName = "elementclient_64" } },
        };

        await Assert.That(options.FindByProcessName("explorer")).IsNull();
        await Assert.That(options.FindByProcessName("")).IsNull();
    }

    [Test]
    public async Task FindByProcessName_DuplicateNames_FirstWins()
    {
        var options = new ProcessProfileOptions
        {
            Profiles =
            {
                new ProcessProfile { ProcessName = "game", SettleDelayMs = 10 },
                new ProcessProfile { ProcessName = "GAME", SettleDelayMs = 99 },
            },
        };

        var found = options.FindByProcessName("game");

        await Assert.That(found!.SettleDelayMs).IsEqualTo(10);
    }

    [Test]
    public async Task GetWatchedProcessNames_ReturnsDistinctUnion_SkippingBlanks()
    {
        var options = new ProcessProfileOptions
        {
            Profiles =
            {
                new ProcessProfile { ProcessName = "elementclient_64" },
                new ProcessProfile { ProcessName = "ELEMENTCLIENT_64" }, // дубль, другой регистр
                new ProcessProfile { ProcessName = "" }, // пустое — пропускается
                new ProcessProfile { ProcessName = "notepad" },
            },
        };

        var names = options.GetWatchedProcessNames();

        await Assert.That(names).Count().IsEqualTo(2);
        await Assert.That(names[0]).IsEqualTo("elementclient_64");
        await Assert.That(names[1]).IsEqualTo("notepad");
    }

    [Test]
    public async Task InertProfile_HasNoActivationDance()
    {
        var inert = ProcessProfile.Inert;

        await Assert.That(inert.ActivationLParam).IsNull();
        await Assert.That(inert.SettleDelayMs).IsEqualTo(0);
        await Assert.That(inert.DeactivationDelayMs).IsEqualTo(0);
    }
}
