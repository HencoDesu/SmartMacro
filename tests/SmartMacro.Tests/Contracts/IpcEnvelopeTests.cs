using System.Text.Json;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Contracts;

// Стадия 1: конверт — та единственная часть протокола, которую оба процесса разбирают ещё до
// того, как узнают, что за сообщение пришло, а значит, он обязан пережить любую форму: нагрузка
// есть, нагрузки нет, ответ с ошибкой и типы сообщений, о которых эта сборка слыхом не слыхивала.
public class IpcEnvelopeTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, IpcJson.Options);
        return JsonSerializer.Deserialize<T>(json, IpcJson.Options)!;
    }

    [Test]
    public async Task Request_WithPayload_RoundTripsIdTypeAndPayload()
    {
        var request = new IpcRequest(42, IpcMessageTypes.AddTag, IpcJson.Write(new AddTagRequest(0x1234, "МАСТЕР")));

        var reloaded = RoundTrip(request);

        await Assert.That(reloaded.Id).IsEqualTo(42);
        await Assert.That(reloaded.Type).IsEqualTo("AddTag");
        var payload = IpcJson.Read<AddTagRequest>(reloaded.Payload);
        await Assert.That(payload).IsEqualTo(new AddTagRequest(0x1234, "МАСТЕР"));
    }

    [Test]
    public async Task Request_WithoutPayload_RoundTripsAsNull()
    {
        var reloaded = RoundTrip(new IpcRequest(1, IpcMessageTypes.GetWindows));

        await Assert.That(reloaded.Id).IsEqualTo(1);
        await Assert.That(reloaded.Payload).IsNull();
        // Попытка вычитать типизированную нагрузку из сообщения без аргументов ничего не делает,
        // а не бросает исключение.
        await Assert.That(IpcJson.Read<AddTagRequest>(reloaded.Payload)).IsNull();
    }

    [Test]
    public async Task Response_Ok_WithArrayPayload_RoundTrips()
    {
        var windows = new[]
        {
            new WindowDto(0x10, "elementclient", ["перс", "МАСТЕР"]),
            new WindowDto(0x20, "notepad", []),
        };
        var response = new IpcResponse(7, Ok: true, IpcJson.Write(windows));

        var reloaded = RoundTrip(response);

        await Assert.That(reloaded.Id).IsEqualTo(7);
        await Assert.That(reloaded.Ok).IsTrue();
        await Assert.That(reloaded.Error).IsNull();
        var payload = IpcJson.Read<WindowDto[]>(reloaded.Payload)!;
        await Assert.That(payload).Count().IsEqualTo(2);
        await Assert.That(payload[0].Hwnd).IsEqualTo(0x10L);
        await Assert.That(payload[0].Tags).Count().IsEqualTo(2);
        await Assert.That(payload[1].Tags).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Response_Failure_CarriesErrorAndNoPayload()
    {
        var reloaded = RoundTrip(new IpcResponse(9, Ok: false, Payload: null, Error: "Макрос не найден"));

        await Assert.That(reloaded.Ok).IsFalse();
        await Assert.That(reloaded.Error).IsEqualTo("Макрос не найден");
        await Assert.That(reloaded.Payload).IsNull();
    }

    [Test]
    public async Task Response_Ok_WithScalarPayload_RoundTrips()
    {
        // DumpCaptures отвечает голой строкой JSON, а не объектом.
        var reloaded = RoundTrip(new IpcResponse(3, Ok: true, IpcJson.Write(@"C:\captures\2026-08-02")));

        await Assert.That(IpcJson.Read<string>(reloaded.Payload)).IsEqualTo(@"C:\captures\2026-08-02");
    }

    [Test]
    public async Task Event_WithAndWithoutPayload_RoundTrips()
    {
        var closed = RoundTrip(new IpcEvent(IpcMessageTypes.WindowClosed, IpcJson.Write(new WindowClosedEvent(0x99))));
        await Assert.That(closed.Type).IsEqualTo("WindowClosed");
        await Assert.That(IpcJson.Read<WindowClosedEvent>(closed.Payload)).IsEqualTo(new WindowClosedEvent(0x99));

        var changed = RoundTrip(new IpcEvent(IpcMessageTypes.MacrosChanged));
        await Assert.That(changed.Type).IsEqualTo("MacrosChanged");
        await Assert.That(changed.Payload).IsNull();
    }

    [Test]
    public async Task UnknownType_DeserializesWithoutThrowing()
    {
        // Демон посвежее, разговаривающий с клиентом постарее (или наоборот), не имеет права
        // взорваться в разборщике — что делать с необслуживаемым типом, решает диспетчер.
        const string requestJson = """{"Id":5,"Type":"TeleportPlayer","Payload":{"Whatever":true}}""";
        const string eventJson = """{"Type":"MoonPhaseChanged","Payload":[1,2,3]}""";

        var request = JsonSerializer.Deserialize<IpcRequest>(requestJson, IpcJson.Options)!;
        var evt = JsonSerializer.Deserialize<IpcEvent>(eventJson, IpcJson.Options)!;

        await Assert.That(request.Type).IsEqualTo("TeleportPlayer");
        await Assert.That(request.Payload!.Value.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(evt.Type).IsEqualTo("MoonPhaseChanged");
        await Assert.That(evt.Payload!.Value.ValueKind).IsEqualTo(JsonValueKind.Array);
    }

    [Test]
    public async Task ExplicitJsonNullPayload_ReadsAsNull()
    {
        const string json = """{"Id":1,"Type":"GetWindows","Payload":null}""";

        var request = JsonSerializer.Deserialize<IpcRequest>(json, IpcJson.Options)!;

        await Assert.That(request.Payload).IsNull();
        await Assert.That(IpcJson.Read<WindowDto[]>(request.Payload)).IsNull();
    }

    [Test]
    public async Task SerializedEnvelope_IsASingleLine()
    {
        // Транспорт — это JSON Lines: перевод строки внутри сообщения разрезал бы его надвое.
        // MacroGraphJson.Options делает отступы (файлы правят руками), так что это ровно та
        // настройка, которую IpcJson обязан перебить, — а нагрузка с графом здесь самый жирный
        // случай.
        var request = new IpcRequest(
            1,
            IpcMessageTypes.SaveMacro,
            IpcJson.Write(new SaveMacroRequest(Macros.FullMacroGraphFixture.Build())));

        var json = JsonSerializer.Serialize(request, IpcJson.Options);

        await Assert.That(json).DoesNotContain("\n");
        await Assert.That(json).DoesNotContain("\r");
        await Assert.That(IpcJson.Options.WriteIndented).IsFalse();
    }
}
