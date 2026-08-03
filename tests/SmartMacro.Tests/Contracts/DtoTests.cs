using System.Text.Json;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Validation;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Contracts;

// Стадия 1: после разделения DTO — единственная форма, которую видит интерфейс, поэтому важны
// обе половины: round trip через JSON (точность передачи по проводу) и мапперы из живых типов
// Core (а вот там-то ширина IntPtr или DateTimeKind и портятся втихую).
public class DtoTests
{
    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, IpcJson.Options), IpcJson.Options)!;

    [Test]
    public async Task WindowDto_RoundTrips()
    {
        var reloaded = RoundTrip(new WindowDto(0x7FFF_FFFF_FFFF, "elementclient", ["перс", "МАСТЕР"]));

        await Assert.That(reloaded.Hwnd).IsEqualTo(0x7FFF_FFFF_FFFFL);
        await Assert.That(reloaded.ProcessName).IsEqualTo("elementclient");
        await Assert.That(reloaded.Tags).Count().IsEqualTo(2);
        await Assert.That(reloaded.Tags[1]).IsEqualTo("МАСТЕР");
    }

    [Test]
    public async Task RunningMacroDto_RoundTrips_IncludingNullCurrentNode()
    {
        var runId = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 8, 2, 12, 34, 56, TimeSpan.Zero);

        var withNode = RoundTrip(new RunningMacroDto(runId, "pw-boot", started, "wait"));
        await Assert.That(withNode.RunId).IsEqualTo(runId);
        await Assert.That(withNode.MacroName).IsEqualTo("pw-boot");
        await Assert.That(withNode.StartedUtc).IsEqualTo(started);
        await Assert.That(withNode.CurrentNodeId).IsEqualTo("wait");

        var beforeFirstNode = RoundTrip(new RunningMacroDto(runId, "pw-boot", started, null));
        await Assert.That(beforeFirstNode.CurrentNodeId).IsNull();
    }

    [Test]
    public async Task ValidationIssueDto_RoundTrips()
    {
        var reloaded = RoundTrip(new ValidationIssueDto("Error", "click1",
            "У ClickNode должно быть задано ровно одно из Point / PointVar."));

        await Assert.That(reloaded.Severity).IsEqualTo("Error");
        await Assert.That(reloaded.NodeId).IsEqualTo("click1");

        var graphLevel = RoundTrip(new ValidationIssueDto("Warning", null, "Нода недостижима из StartNodeId."));
        await Assert.That(graphLevel.NodeId).IsNull();
    }

    [Test]
    public async Task ValidationIssue_MapsBothWays()
    {
        var issue = new ValidationIssue(ValidationSeverity.Warning, "n1", "хот-луп");

        var dto = issue.ToDto();
        await Assert.That(dto.Severity).IsEqualTo("Warning");

        var back = dto.ToIssue();
        await Assert.That(back).IsEqualTo(issue);
    }

    [Test]
    public async Task ValidationIssue_UnknownSeverity_ReadsAsError()
    {
        // То, что не поддаётся классификации, никогда не понижаем в важности молча.
        var parsed = new ValidationIssueDto("Catastrophe", null, "?").ToIssue();

        await Assert.That(parsed.Severity).IsEqualTo(ValidationSeverity.Error);
    }

    [Test]
    public async Task ValidationIssueList_MapsAndSurvivesAResponsePayload()
    {
        // SaveMacro отвечает массивом замечаний; пустой означает «сохранено».
        IReadOnlyList<ValidationIssue> issues =
        [
            new(ValidationSeverity.Error, "a", "битая ссылка"),
            new(ValidationSeverity.Warning, null, "недостижимая нода"),
        ];

        var response = new IpcResponse(1, Ok: true, IpcJson.Write(issues.ToDto()));
        var reloaded = IpcJson.Read<ValidationIssueDto[]>(RoundTrip(response).Payload)!;

        await Assert.That(reloaded).Count().IsEqualTo(2);
        await Assert.That(reloaded[0].Severity).IsEqualTo("Error");
        await Assert.That(reloaded[1].NodeId).IsNull();

        var saved = new IpcResponse(2, Ok: true, IpcJson.Write(Array.Empty<ValidationIssueDto>()));
        await Assert.That(IpcJson.Read<ValidationIssueDto[]>(RoundTrip(saved).Payload)!).IsEmpty();
    }

    [Test]
    public async Task ManagedWindowInfo_MapsToWindowDto()
    {
        var window = new ManagedWindowInfo(new IntPtr(0x1A2B3C), "elementclient", new HashSet<string> { "перс" });

        var dto = window.ToDto();

        await Assert.That(dto.Hwnd).IsEqualTo(0x1A2B3CL);
        await Assert.That(dto.ProcessName).IsEqualTo("elementclient");
        await Assert.That(dto.Tags).Count().IsEqualTo(1);

        // Проекция списка — это копия: последующие снимки реестра не имеют права её менять.
        var many = new[] { window, new ManagedWindowInfo(new IntPtr(2), "notepad", new HashSet<string>()) }.ToDto();
        await Assert.That(many).Count().IsEqualTo(2);
        await Assert.That(many[1].Tags).IsEmpty();
    }

    [Test]
    public async Task MacroRunSnapshot_MapsToRunningMacroDto_WithAZeroOffset()
    {
        var runId = Guid.NewGuid();
        var started = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

        var dto = new MacroRunSnapshot(runId, "pw-immunity", started, "delay1").ToDto();

        await Assert.That(dto.RunId).IsEqualTo(runId);
        await Assert.That(dto.StartedUtc.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(dto.StartedUtc.UtcDateTime).IsEqualTo(started);
        await Assert.That(dto.CurrentNodeId).IsEqualTo("delay1");
    }

    [Test]
    public async Task MacroRunSnapshot_WithUnspecifiedKind_IsNotShiftedByTheLocalOffset()
    {
        // DateTimeOffset(DateTime) применяет к значению с Unspecified МЕСТНОЕ смещение; маппер
        // сперва прибивает kind, поэтому в провод уезжает ровно та отметка времени, которую
        // мы и поставили.
        var unspecified = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Unspecified);

        var dto = new MacroRunSnapshot(Guid.NewGuid(), "m", unspecified, null).ToDto();

        await Assert.That(dto.StartedUtc.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(dto.StartedUtc.UtcDateTime.Hour).IsEqualTo(12);
    }
}
