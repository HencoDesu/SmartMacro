using System.Text.Json;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Tests.Macros;

namespace SmartMacro.Tests.Ipc;

// Стадия 2B: слой нарезки на сообщения сам по себе — одна строка на сообщение, пустые строки
// терпятся, кривая строка не смертельна, одновременные писатели выстраиваются в очередь.
//
// Последний из них — тот тест, который отрабатывает свой хлеб. В бою у IpcConnection два
// независимых писателя (цикл «запрос-ответ» и насос событий), а StreamWriter не потокобезопасен;
// уберите SemaphoreSlim — и этот файл покраснеет, пока всё остальное останется зелёным.
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
    public async Task WriteAsync_FatPayload_StaysOnOneLine()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        // Самой жирной нагрузкой был граф макроса; с волны F3 он по трубе не ходит вовсе, и
        // тяжеловесом остался снимок настроек — он же единственная нагрузка, которую IpcJson
        // печатает настройками, унаследованными от файлового диалекта с его отступами.
        await connection.WriteAsync(new IpcResponse(
            1,
            Ok: true,
            IpcJson.Write(new SaveSettingsRequest(new SmartMacro.Contracts.Settings.AppSettings()))));

        var line = await pair.ReadLineAsync();
        // Просочись отступы наружу — читатель остановился бы на первом же внутреннем переводе
        // строки, и эта десериализация бросила бы исключение.
        var reloaded = JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)!;
        await Assert.That(IpcJson.Read<SaveSettingsRequest>(reloaded.Payload)!.Settings).IsNotNull();
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
        await pair.SendAsync(new IpcRequest(7, IpcMessageTypes.GetRunningMacros));

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

        // Ради этого отказ и сделан не смертельным: читатель стоит на следующей строке, так что
        // одно испорченное сообщение не имеет права стоить нам соединения.
        var next = await connection.ReadAsync<IpcRequest>();
        await Assert.That(next!.Id).IsEqualTo(2);
    }

    [Test]
    public async Task WriteAsync_ConcurrentWriters_ProduceWellFormedNewlineDelimitedJson()
    {
        const int responses = 40;
        const int events = 40;

        using var pair = new DuplexStreamPair();
        await using var connection = ServerSide(pair);

        // Два писателя молотят по одному соединению со множества потоков разом — в бою форма
        // ровно такая же (цикл ответов плюс насос событий), просто не такая плотная.
        var writers = new List<Task>();
        for (var i = 0; i < responses; i++)
        {
            var id = i;
            writers.Add(Task.Run(() => connection.WriteAsync(new IpcResponse(id, Ok: true))));
        }

        for (var i = 0; i < events; i++)
        {
            writers.Add(Task.Run(() => connection.WriteAsync(
                new IpcEvent(IpcMessageTypes.WindowClosed, IpcJson.Write(new WindowClosedEvent(0xABC))))));
        }

        await Task.WhenAll(writers);

        var lines = await pair.ReadLinesAsync(responses + events);

        var seenIds = new HashSet<int>();
        var eventCount = 0;
        foreach (var line in lines)
        {
            // Перемешавшиеся записи всплывут здесь как JsonException, а не как еле заметное
            // расхождение.
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

        await Assert.That(seenIds).Count().IsEqualTo(responses);
        await Assert.That(eventCount).IsEqualTo(events);
    }
}
