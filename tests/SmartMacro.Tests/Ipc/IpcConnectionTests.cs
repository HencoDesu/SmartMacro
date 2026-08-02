using System.Text.Json;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Tests.Macros;

namespace SmartMacro.Tests.Ipc;

// Stage 2B: the framing layer on its own — one line per message, blank lines tolerated,
// a bad line recoverable, and concurrent writers serialised.
//
// The last one is the test that earns its keep. IpcConnection has two independent writers
// in production (the request/response loop and the event pump) and StreamWriter is not
// thread-safe; delete the SemaphoreSlim and this file goes red while everything else
// stays green.
public class IpcConnectionTests
{
    private static IpcConnection ServerSide(DuplexStreamPair pair) =>
        new(pair.ServerInput, pair.ServerOutput, leaveOpen: true);

    [Test]
    public async Task WriteAsync_EmitsExactlyOneLinePerMessage()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        await connection.WriteAsync(new IpcResponse(1, Ok: true));
        await connection.WriteAsync(new IpcEvent(IpcMessageTypes.MacrosChanged));

        var lines = await pair.ReadLinesAsync(2);
        await Assert.That(JsonSerializer.Deserialize<IpcResponse>(lines[0], IpcJson.Options)!.Id).IsEqualTo(1);
        await Assert.That(JsonSerializer.Deserialize<IpcEvent>(lines[1], IpcJson.Options)!.Type)
            .IsEqualTo(IpcMessageTypes.MacrosChanged);
    }

    [Test]
    public async Task WriteAsync_FatGraphPayload_StaysOnOneLine()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        await connection.WriteAsync(new IpcResponse(
            1,
            Ok: true,
            IpcJson.Write(new SaveMacroRequest(FullMacroGraphFixture.Build()))));

        var line = await pair.ReadLineAsync();
        // If the graph's indentation leaked through, the reader would have stopped at the
        // first inner newline and this deserialize would throw.
        var reloaded = JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)!;
        await Assert.That(IpcJson.Read<SaveMacroRequest>(reloaded.Payload)!.Macro.Nodes)
            .Count().IsEqualTo(FullMacroGraphFixture.NodeCount);
    }

    [Test]
    public async Task ReadAsync_ReturnsNullAtEndOfStream()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        await pair.SendAsync(new IpcRequest(1, IpcMessageTypes.GetWindows));
        pair.CloseClient();

        await Assert.That((await connection.ReadAsync<IpcRequest>())!.Id).IsEqualTo(1);
        await Assert.That(await connection.ReadAsync<IpcRequest>()).IsNull();
    }

    [Test]
    public async Task ReadAsync_SkipsBlankLines()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        await pair.SendLineAsync(string.Empty);
        await pair.SendLineAsync("   ");
        await pair.SendAsync(new IpcRequest(7, IpcMessageTypes.GetMacros));

        var request = await connection.ReadAsync<IpcRequest>();

        await Assert.That(request!.Id).IsEqualTo(7);
    }

    [Test]
    public async Task ReadAsync_MalformedLine_ThrowsButLeavesTheConnectionUsable()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        await pair.SendLineAsync("{это не json");
        await pair.SendAsync(new IpcRequest(2, IpcMessageTypes.GetWindows));

        await Assert.That(async () => await connection.ReadAsync<IpcRequest>()).Throws<JsonException>();

        // The whole point of making the failure recoverable: the reader is positioned at
        // the next line, so one bad message must not cost us the connection.
        var next = await connection.ReadAsync<IpcRequest>();
        await Assert.That(next!.Id).IsEqualTo(2);
    }

    [Test]
    public async Task WriteAsync_ConcurrentWriters_ProduceWellFormedNewlineDelimitedJson()
    {
        const int Responses = 40;
        const int Events = 40;

        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        // Two writers hammering one connection from many threads at once — production has
        // exactly this shape (response loop + event pump), just less densely.
        var writers = new List<Task>();
        for (var i = 0; i < Responses; i++)
        {
            var id = i;
            writers.Add(Task.Run(() => connection.WriteAsync(new IpcResponse(id, Ok: true))));
        }
        for (var i = 0; i < Events; i++)
        {
            writers.Add(Task.Run(() => connection.WriteAsync(
                new IpcEvent(IpcMessageTypes.WindowClosed, IpcJson.Write(new WindowClosedEvent(0xABC))))));
        }
        await Task.WhenAll(writers);

        var lines = await pair.ReadLinesAsync(Responses + Events);

        var seenIds = new HashSet<int>();
        var eventCount = 0;
        foreach (var line in lines)
        {
            // Interleaved writes show up here as a JsonException, not as a subtle diff.
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("Id", out var id))
            {
                seenIds.Add(id.GetInt32());
            }
            else
            {
                await Assert.That(document.RootElement.GetProperty("Type").GetString())
                    .IsEqualTo(IpcMessageTypes.WindowClosed);
                eventCount++;
            }
        }

        await Assert.That(seenIds).Count().IsEqualTo(Responses);
        await Assert.That(eventCount).IsEqualTo(Events);
    }
}
