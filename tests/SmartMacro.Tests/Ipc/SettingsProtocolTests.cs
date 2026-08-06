using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Contracts.Settings;

namespace SmartMacro.Tests.Ipc;

// Настроечная часть протокола на настоящем хранилище: GetSettings / SaveSettings / ResetSettings /
// SetLogLevel.
//
// Диспетчер здесь — тонкий слой, и проверяется именно то, что делает его тонким: договорённость
// «пустой массив = записано», отказ, который НЕ пишет, и разделение времени жизни (файл переживает
// перезапуск, уровень журнала — нет).
public class SettingsProtocolTests
{
    [Test]
    public async Task GetSettings_ReturnsTheFileAndTheLiveLogLevel()
    {
        using var harness = new IpcDispatcherHarness();
        harness.LogLevel.Current = LogLevelDto.Debug;

        var response = await harness.DispatchAsync(IpcMessageTypes.GetSettings);
        var snapshot = IpcJson.Read<SettingsSnapshotDto>(response.Payload)!;

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(snapshot.Settings.Profiles[0].ProcessName).IsEqualTo("elementclient_64");
        await Assert.That(snapshot.LogLevel).IsEqualTo(LogLevelDto.Debug);
        await Assert.That(snapshot.SettingsFilePath).IsEqualTo(harness.SettingsFile.FilePath);
        await Assert.That(snapshot.FolderPath).IsEqualTo(harness.BaseDirectory);
    }

    [Test]
    public async Task SaveSettings_EmptyIssuesMeansWritten()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveSettings,
            new SaveSettingsRequest(AppSettings.Default with
            {
                Watch = new WatchSettings { ProcessPollIntervalSeconds = 8, WindowPollIntervalSeconds = 9 },
            }));

        await Assert.That(IpcJson.Read<SettingsIssue[]>(response.Payload)).IsEmpty();
        await Assert.That(harness.SettingsFile.Current.Watch.ProcessPollIntervalSeconds).IsEqualTo(8);
        // И на диске: «приняли, но не записали» — самый неприятный вид полуработы.
        var onDisk = AppSettingsJson.Deserialize(File.ReadAllText(harness.SettingsFile.FilePath));
        await Assert.That(onDisk.Watch.WindowPollIntervalSeconds).IsEqualTo(9);
    }

    [Test]
    public async Task SaveSettings_RejectedSettingsAreNotWritten()
    {
        using var harness = new IpcDispatcherHarness();
        var before = File.ReadAllText(harness.SettingsFile.FilePath);

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveSettings,
            new SaveSettingsRequest(AppSettings.Default with
            {
                Vision = new VisionSettings { MatchThreshold = 42 },
            }));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(IpcJson.Read<SettingsIssue[]>(response.Payload)).IsNotEmpty();
        await Assert.That(File.ReadAllText(harness.SettingsFile.FilePath)).IsEqualTo(before);
    }

    // Нагрузка приезжает из JSON, и клиент вправе поля не положить. Отказ, а не падение.
    [Test]
    public async Task SaveSettings_WithoutPayloadIsRejectedPolitely()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveSettings,
            new SaveSettingsRequest(null));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsNotNull();
    }

    [Test]
    public async Task ResetSettings_RestoresDefaultsAndAnswersWithTheResult()
    {
        using var harness = new IpcDispatcherHarness();
        await harness.DispatchAsync(IpcMessageTypes.SaveSettings,
            new SaveSettingsRequest(AppSettings.Default with { Profiles = [] }));

        var response = await harness.DispatchAsync(IpcMessageTypes.ResetSettings);
        var snapshot = IpcJson.Read<SettingsSnapshotDto>(response.Payload)!;

        await Assert.That(snapshot.Settings.Profiles).Count().IsEqualTo(1);
        await Assert.That(harness.SettingsFile.Current.Profiles[0].ProcessName).IsEqualTo("elementclient_64");
    }

    // Уровень журнала намеренно НЕ часть файла: он остаётся в appsettings.json, чтобы падение на
    // чтении настроек можно было расследовать уровнем, известным ДО. Здесь это закреплено с обеих
    // сторон — рубильник сдвинулся, файл не тронут.
    [Test]
    public async Task SetLogLevel_MovesTheSwitchAndWritesNothing()
    {
        using var harness = new IpcDispatcherHarness();
        var before = File.ReadAllText(harness.SettingsFile.FilePath);

        var response = await harness.DispatchAsync(IpcMessageTypes.SetLogLevel,
            new SetLogLevelRequest(LogLevelDto.Warning));
        var snapshot = IpcJson.Read<SettingsSnapshotDto>(response.Payload)!;

        await Assert.That(harness.LogLevel.Current).IsEqualTo(LogLevelDto.Warning);
        await Assert.That(snapshot.LogLevel).IsEqualTo(LogLevelDto.Warning);
        await Assert.That(File.ReadAllText(harness.SettingsFile.FilePath)).IsEqualTo(before);
    }

    // Провайдер снимка — единственное место, где снимок собирается: и ответ на GetSettings, и пуш
    // SettingsChanged идут через него, иначе они однажды разошлись бы.
    [Test]
    public async Task SnapshotProvider_RaisesChangedForBothTheFileAndTheLogLevel()
    {
        using var harness = new IpcDispatcherHarness();
        var seen = new List<SettingsSnapshotDto>();
        harness.SettingsSnapshots.Changed += seen.Add;

        await harness.DispatchAsync(IpcMessageTypes.SaveSettings,
            new SaveSettingsRequest(AppSettings.Default with { Profiles = [] }));
        await harness.DispatchAsync(IpcMessageTypes.SetLogLevel, new SetLogLevelRequest(LogLevelDto.Error));

        await Assert.That(seen).Count().IsEqualTo(2);
        await Assert.That(seen[0].Settings.Profiles).IsEmpty();
        await Assert.That(seen[1].LogLevel).IsEqualTo(LogLevelDto.Error);
    }
}
