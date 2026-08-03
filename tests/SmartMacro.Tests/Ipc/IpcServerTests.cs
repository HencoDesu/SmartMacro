using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Ipc;

// Стадия 2B: собственные обязанности сервера — сопоставление ответов запросам, разделение
// «ответ или событие», рассылка нескольким клиентам и выживание при клиенте, умершем посреди
// рассылки.
//
// Каждое соединение здесь — пара потоков в памяти (см. InMemoryDuplex.cs). Ни одного
// NamedPipeServerStream во всём наборе не создаётся: ему потребовалось бы глобальное имя (а это
// конец параллельным тестам) и сеанс рабочего стола, да и разыграть на нём «собеседника больше
// нет» тяжело. Возможным это делает шов ServeConnectionAsync — ниже него боевой путь и тестовый
// суть один и тот же код.
public class IpcServerTests
{
    private sealed class ServerFixture : IAsyncDisposable
    {
        public ServerFixture()
        {
            Engine = new IpcDispatcherHarness();
            Server = new IpcServer(
                Engine.Dispatcher,
                Engine.Windows,
                Engine.Macros,
                Engine.Runs,
                Engine.RunEvents,
                Engine.Debug,
                NullLogger<IpcServer>.Instance);
            Server.SubscribeToEngine();
        }

        public IpcDispatcherHarness Engine { get; }

        public IpcServer Server { get; }

        /// <summary>Открывает клиента, дожидается, пока сервер его зарегистрирует, и отдаёт обе половины.</summary>
        public async Task<(DuplexStreamPair Client, Task Serve)> ConnectAsync()
        {
            var expected = Server.ConnectionCount + 1;
            var pair = new DuplexStreamPair();
            var serve = Server.ServeConnectionAsync(pair.ServerInput, pair.ServerOutput);
            await WaitUntilAsync(() => Server.ConnectionCount >= expected);
            return (pair, serve);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Engine.Dispose();
        }
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    private static string TypeOf(JsonElement element) => element.GetProperty("Type").GetString()!;

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    // ----------------------------------------------- сопоставление ответов запросам

    [Test]
    public async Task PipelinedRequests_GetRepliesCarryingTheirOwnIds()
    {
        await using var fixture = new ServerFixture();
        fixture.Engine.Windows.Register(0x10, "elementclient");
        var (client, serve) = await fixture.ConnectAsync();

        // Оба уходят раньше, чем вернётся хоть один ответ: цикл обработчиков не ждёт завершения
        // одного запроса, прежде чем прочитать следующий, — поэтому ответы могут прийти в любом
        // порядке, и единственное, что привязывает их к вызывающему, — это Id.
        await client.SendAsync(new IpcRequest(11, IpcMessageTypes.GetWindows));
        await client.SendAsync(new IpcRequest(22, IpcMessageTypes.GetRunningMacros));

        var replies = (await client.ReadLinesAsync(2)).Select(Parse).ToList();

        await Assert.That(replies.Select(r => r.GetProperty("Id").GetInt32()).OrderBy(id => id))
            .IsEquivalentTo(new[] { 11, 22 });
        await Assert.That(replies.All(r => r.GetProperty("Ok").GetBoolean())).IsTrue();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task Events_CarryNoId_SoTheyCanNeverBeMistakenForAReply()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        await client.SendAsync(new IpcRequest(5, IpcMessageTypes.GetWindows));
        await Assert.That(Parse(await client.ReadLineAsync()).GetProperty("Id").GetInt32()).IsEqualTo(5);

        fixture.Engine.Windows.Register(0x77, "elementclient");

        var evt = Parse(await client.ReadLineAsync());
        await Assert.That(evt.TryGetProperty("Id", out _)).IsFalse();
        await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.WindowAppeared);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task RequestActivate_ReachesEveryClient_ThroughTheServerTheDispatcherWasHandedAtConstruction()
    {
        await using var fixture = new ServerFixture();
        var (asker, askerServe) = await fixture.ConnectAsync();
        var (panel, panelServe) = await fixture.ConnectAsync();

        // Отправитель — это второй запуск интерфейса; получатель, который здесь важен, — ДРУГОЕ
        // соединение. Заодно покрыта и сама обвязка: IpcServer передаёт себя диспетчеру в
        // собственном конструкторе, потому что DI этот цикл разрешить не может.
        await asker.SendAsync(new IpcRequest(9, IpcMessageTypes.RequestActivate));

        // Спросивший получает И ответ, И свою копию рассылки, а путь ответа и насос событий —
        // независимые писатели, так что порядок не гарантирован.
        var askerLines = (await asker.ReadLinesAsync(2)).Select(Parse).ToList();
        var reply = askerLines.Single(line => line.TryGetProperty("Id", out _));
        await Assert.That(reply.GetProperty("Ok").GetBoolean()).IsTrue();
        await Assert.That(askerLines.Where(line => !line.TryGetProperty("Id", out _)).Select(TypeOf))
            .IsEquivalentTo(new[] { IpcMessageTypes.ActivateWindow });

        var pushed = Parse(await panel.ReadLineAsync());
        await Assert.That(pushed.TryGetProperty("Id", out _)).IsFalse();
        await Assert.That(TypeOf(pushed)).IsEqualTo(IpcMessageTypes.ActivateWindow);

        asker.CloseClient();
        panel.CloseClient();
        await Task.WhenAll(askerServe, panelServe);
    }

    // -------------------------------------------------------------- события движка

    [Test]
    public async Task WindowLifecycle_IsPushedAsAppeared_TagsChanged_Closed()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        fixture.Engine.Windows.Register(0x123, "elementclient");
        fixture.Engine.Windows.AddTag(0x123, "Лучник");
        fixture.Engine.Windows.Unregister(0x123);

        var events = (await client.ReadLinesAsync(3)).Select(Parse).ToList();

        await Assert.That(events.Select(TypeOf)).IsEquivalentTo(new[]
        {
            IpcMessageTypes.WindowAppeared,
            IpcMessageTypes.WindowTagsChanged,
            IpcMessageTypes.WindowClosed,
        });

        var appeared = IpcJson.Read<WindowDto>(events[0].GetProperty("Payload"))!;
        await Assert.That(appeared.Hwnd).IsEqualTo(0x123L);
        await Assert.That(appeared.Tags).IsEmpty();

        // События по тегам несут полное новое состояние, а не разницу.
        var tagged = IpcJson.Read<WindowDto>(events[1].GetProperty("Payload"))!;
        await Assert.That(tagged.Tags).IsEquivalentTo(new[] { "Лучник" });

        // От окна не остаётся ничего, кроме его дескриптора.
        await Assert.That(IpcJson.Read<WindowClosedEvent>(events[2].GetProperty("Payload")))
            .IsEqualTo(new WindowClosedEvent(0x123));

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task LibraryChange_IsPushedAsPayloadlessMacrosChanged()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        await fixture.Engine.Macros.SaveAsync(new MacroGraph
        {
            Name = "новый",
            StartNodeId = "n0",
            Nodes = [new KeyPressNode { Id = "n0", Key = VirtualKey.F3, Target = new TargetSelector() }],
        });

        var evt = Parse(await client.ReadLineAsync());

        await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.MacrosChanged);
        // Нагрузки нет по протоколу: библиотека бывает большой, клиент перезапрашивает сам.
        await Assert.That(evt.TryGetProperty("Payload", out _)).IsFalse();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task RunRegistryChange_IsPushedWithTheFreshSnapshot()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        var handle = fixture.Engine.Runs.TryBegin("бут")!;
        var started = Parse(await client.ReadLineAsync());
        var runs = IpcJson.Read<RunningMacroDto[]>(started.GetProperty("Payload"))!;
        await Assert.That(TypeOf(started)).IsEqualTo(IpcMessageTypes.RunningMacrosChanged);
        await Assert.That(runs).Count().IsEqualTo(1);
        await Assert.That(runs[0].RunId).IsEqualTo(handle.RunId);

        fixture.Engine.Runs.Complete(handle.RunId);
        var finished = Parse(await client.ReadLineAsync());
        await Assert.That(IpcJson.Read<RunningMacroDto[]>(finished.GetProperty("Payload"))!).IsEmpty();

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task UnsubscribeFromEngine_SilencesPushes()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        fixture.Server.UnsubscribeFromEngine();
        fixture.Engine.Windows.Register(0x1, "elementclient");

        // В очереди не должно было оказаться ничего, так что единственная строка, которую мы
        // можем получить, — ответ на посланный следом запрос: проскочи туда событие, оно шло бы
        // первым.
        await client.SendAsync(new IpcRequest(3, IpcMessageTypes.GetWindows));
        var line = Parse(await client.ReadLineAsync());
        await Assert.That(line.GetProperty("Id").GetInt32()).IsEqualTo(3);

        client.CloseClient();
        await serve;
    }

    // ------------------------------------------------------- несколько клиентов

    [Test]
    public async Task OneEvent_ReachesEveryConnectedClient()
    {
        await using var fixture = new ServerFixture();
        var (first, serveFirst) = await fixture.ConnectAsync();
        var (second, serveSecond) = await fixture.ConnectAsync();
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(2);

        fixture.Engine.Windows.Register(0x42, "elementclient");

        foreach (var client in new[] { first, second })
        {
            var evt = Parse(await client.ReadLineAsync());
            await Assert.That(TypeOf(evt)).IsEqualTo(IpcMessageTypes.WindowAppeared);
            await Assert.That(IpcJson.Read<WindowDto>(evt.GetProperty("Payload"))!.Hwnd).IsEqualTo(0x42L);
        }

        first.CloseClient();
        second.CloseClient();
        await Task.WhenAll(serveFirst, serveSecond);
    }

    [Test]
    public async Task ClientThatDiesMidBroadcast_IsDropped_AndTheRestKeepReceiving()
    {
        await using var fixture = new ServerFixture();
        var (dying, serveDying) = await fixture.ConnectAsync();
        var (survivor, serveSurvivor) = await fixture.ConnectAsync();

        // Ломается только направление демон→клиент: читатель сервера по-прежнему стоит на месте,
        // так что о смерти он узнаёт из неудавшегося ПУША, — и это тот самый случай, который не
        // имеет права утащить за собой сервер (или второго клиента).
        dying.BreakServerWrites();

        fixture.Engine.Windows.Register(0x50, "elementclient");

        await Assert.That(TypeOf(Parse(await survivor.ReadLineAsync()))).IsEqualTo(IpcMessageTypes.WindowAppeared);
        await Assert.That(await WaitUntilAsync(() => fixture.Server.ConnectionCount == 1)).IsTrue();

        // После этого сервер полностью работоспособен: и события идут, и запросы тоже.
        fixture.Engine.Windows.AddTag(0x50, "Жрец");
        await Assert.That(TypeOf(Parse(await survivor.ReadLineAsync()))).IsEqualTo(IpcMessageTypes.WindowTagsChanged);

        await survivor.SendAsync(new IpcRequest(9, IpcMessageTypes.GetWindows));
        var reply = Parse(await survivor.ReadLineAsync());
        await Assert.That(reply.GetProperty("Id").GetInt32()).IsEqualTo(9);
        await Assert.That(reply.GetProperty("Ok").GetBoolean()).IsTrue();

        await serveDying;
        survivor.CloseClient();
        await serveSurvivor;
    }

    [Test]
    public async Task ClientDisconnect_RemovesItFromTheConnectionSet()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();
        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(1);

        client.CloseClient();
        await serve;

        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(0);
    }

    // ------------------------------------------------------------------ живучесть

    [Test]
    public async Task MalformedLine_IsSkipped_AndTheConnectionKeepsServing()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        await client.SendLineAsync("{\"Id\": не число}");
        await client.SendAsync(new IpcRequest(4, IpcMessageTypes.GetWindows));

        var reply = Parse(await client.ReadLineAsync());
        await Assert.That(reply.GetProperty("Id").GetInt32()).IsEqualTo(4);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task ConcurrentEventBurstAndReplies_ProduceWellFormedNewlineDelimitedJson()
    {
        const int requests = 25;
        const int events = 25;

        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        // Два производителя в гонке на ОДНОМ соединении: насос событий и обработчики запросов.
        // Без блокировки записи в IpcConnection именно здесь вывод превращается в склеенные
        // полусообщения (или в исключение «поток занят»), и каждый Parse ниже падает.
        var pushes = Task.Run(() =>
        {
            for (var i = 0; i < events; i++)
            {
                fixture.Server.Broadcast(new IpcEvent(
                    IpcMessageTypes.WindowClosed,
                    IpcJson.Write(new WindowClosedEvent(i))));
            }
        });
        var sends = Task.Run(async () =>
        {
            for (var i = 0; i < requests; i++)
            {
                await client.SendAsync(new IpcRequest(i, IpcMessageTypes.GetWindows));
            }
        });
        await Task.WhenAll(pushes, sends);

        var lines = await client.ReadLinesAsync(requests + events, timeoutMs: 15000);

        var ids = new HashSet<int>();
        var hwnds = new HashSet<long>();
        foreach (var element in lines.Select(Parse))
        {
            if (element.TryGetProperty("Id", out var id))
            {
                await Assert.That(element.GetProperty("Ok").GetBoolean()).IsTrue();
                ids.Add(id.GetInt32());
            }
            else
            {
                hwnds.Add(IpcJson.Read<WindowClosedEvent>(element.GetProperty("Payload"))!.Hwnd);
            }
        }

        await Assert.That(ids).Count().IsEqualTo(requests);
        await Assert.That(hwnds).Count().IsEqualTo(events);

        client.CloseClient();
        await serve;
    }

    [Test]
    public async Task Broadcast_WithNoClients_IsHarmless()
    {
        await using var fixture = new ServerFixture();

        // Движок поднимает события целыми днями независимо от того, подключена панель или нет;
        // рассылка обязана в такие моменты быть пустой операцией, а не разыменованием null.
        fixture.Engine.Windows.Register(0x1, "elementclient");
        fixture.Server.Broadcast(new IpcEvent(IpcMessageTypes.ActivateWindow));

        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(0);
    }

    [Test]
    public async Task StopAsync_ClosesLiveConnections()
    {
        await using var fixture = new ServerFixture();
        var (client, serve) = await fixture.ConnectAsync();

        // StartAsync в этом тесте нет — он открыл бы настоящий named pipe. Вторая работа
        // StopAsync, свернуть всё подключённое, гоняется на соединении в памяти.
        await fixture.Server.StopAsync(CancellationToken.None);
        await serve;

        await Assert.That(fixture.Server.ConnectionCount).IsEqualTo(0);
        client.Dispose();
    }
}
