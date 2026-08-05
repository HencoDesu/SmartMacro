using FakeItEasy;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Macros;

namespace SmartMacro.Tests.Ipc;

// Стадия 2B: весь каталог IpcMessageTypes, по одному обработчику за раз — счастливый путь плюс
// тот отказ, который для каждого из них по-настоящему важен. Два стоит назвать отдельно:
//
//   * Обработчик не имеет права бросить исключение через провод: и неизвестный тип, и
//     взорвавшаяся зависимость обязаны вернуться как Ok=false с сообщением.
//
// Волна F3 вычеркнула отсюда семь обработчиков: GetMacros, SaveMacro, DeleteMacro и всю четвёрку
// шаблонов. Макрос по трубе не ходит вовсе — библиотекой владеет панель и правит её файлами, —
// поэтому проверять здесь стало нечего, а то, что раньше проверяли эти тесты (запись, перенос
// шаблонов, отказ по ошибке валидации), проверяется в MacroBundleFolderTests и
// MacroEditorViewModelTests.
public class IpcRequestDispatcherTests
{
    private static MacroGraph SimpleMacro(string name, VirtualKey key = VirtualKey.F1) => new()
    {
        Name = name,
        StartNodeId = Ids.Of("n0"),
        Nodes = [new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = key, Target = new TargetSelector() }],
    };

    // -------------------------------------------------------------------------- окна

    [Test]
    public async Task GetWindows_ReturnsTheRegistrySnapshotAsDtos()
    {
        using var harness = new IpcDispatcherHarness();
        harness.Windows.Register(0x10, "elementclient");
        harness.Windows.AddTag(0x10, "Лучник");
        harness.Windows.Register(0x20, "notepad");

        var response = await harness.DispatchAsync(IpcMessageTypes.GetWindows);

        await Assert.That(response.Ok).IsTrue();
        var windows = IpcJson.Read<WindowDto[]>(response.Payload)!;
        await Assert.That(windows).Count().IsEqualTo(2);
        var archer = windows.Single(w => w.Hwnd == 0x10);
        await Assert.That(archer.ProcessName).IsEqualTo("elementclient");
        await Assert.That(archer.Tags).Contains("Лучник");
        await Assert.That(windows.Single(w => w.Hwnd == 0x20).Tags).IsEmpty();
    }

    [Test]
    public async Task AddTag_And_RemoveTag_MutateTheRegistry()
    {
        using var harness = new IpcDispatcherHarness();
        harness.Windows.Register(0x30, "elementclient");

        var added = await harness.DispatchAsync(IpcMessageTypes.AddTag, new AddTagRequest(0x30, "МАСТЕР"));
        await Assert.That(added.Ok).IsTrue();
        await Assert.That(harness.Windows.HasTag(0x30, "МАСТЕР")).IsTrue();

        var removed = await harness.DispatchAsync(IpcMessageTypes.RemoveTag, new RemoveTagRequest(0x30, "МАСТЕР"));
        await Assert.That(removed.Ok).IsTrue();
        await Assert.That(harness.Windows.HasTag(0x30, "МАСТЕР")).IsFalse();
    }

    [Test]
    public async Task AddTag_ForAWindowThatIsGone_IsANoOpNotAnError()
    {
        using var harness = new IpcDispatcherHarness();

        // Интерфейс всегда навешивает теги по снимку, который может на мгновение отстать; клиент
        // не виноват в том, что клиент игры за это время успел закрыться.
        var response = await harness.DispatchAsync(IpcMessageTypes.AddTag, new AddTagRequest(0xDEAD, "Жрец"));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(harness.Windows.Snapshot()).IsEmpty();
    }

    [Test]
    public async Task AddTag_WithoutAPayload_Fails()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.AddTag);

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).Contains("AddTag");
    }

    // ---------------------------------------------------------------------- макросы

    [Test]
    public async Task RunMacro_KnownName_StartsItAndAnswersImmediately()
    {
        using var harness = new IpcDispatcherHarness();
        harness.WriteMacro(SimpleMacro("иммунка"));

        var response = await harness.DispatchAsync(IpcMessageTypes.RunMacro, new RunMacroRequest("иммунка"));

        await Assert.That(response.Ok).IsTrue();
        A.CallTo(() => harness.Runner.RunMacro("иммунка")).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task RunMacro_UnknownName_FailsAndStartsNothing()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.RunMacro, new RunMacroRequest("нетутакого"));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).Contains("нетутакого");
        A.CallTo(() => harness.Runner.RunMacro(A<string>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task StopMacro_CancelsTheRunAndWaitsForTheRunnerToAcknowledge()
    {
        using var harness = new IpcDispatcherHarness();
        var handle = harness.Runs.TryBegin("бесконечный")!;

        // Заместитель Orchestrator.RunAsync: крутиться до отмены, потом Complete() в finally.
        // Без подтверждения StopAsync не вернётся никогда, а обработчик опирается ровно на эту
        // договорённость.
        var runner = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, handle.Token);
            }
            catch (OperationCanceledException)
            {
            }

            harness.Runs.Complete(handle.RunId);
        });

        var response = await harness.DispatchAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(handle.RunId));
        await runner;

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(harness.Runs.Snapshot()).IsEmpty();
    }

    [Test]
    public async Task StopMacro_UnknownRunId_Succeeds()
    {
        using var harness = new IpcDispatcherHarness();

        // Задокументировано как успех: прогон, сам завершившийся между снимком интерфейса и
        // щелчком, уже сделал то, о чём просил «Стоп».
        var response = await harness.DispatchAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(Guid.NewGuid()));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(response.Error).IsNull();
    }

    [Test]
    public async Task GetRunningMacros_ReturnsTheRunRegistrySnapshot()
    {
        using var harness = new IpcDispatcherHarness();
        var handle = harness.Runs.TryBegin("бут")!;
        handle.CurrentNodeName = "wait-in-world";

        var response = await harness.DispatchAsync(IpcMessageTypes.GetRunningMacros);

        var runs = IpcJson.Read<RunningMacroDto[]>(response.Payload)!;
        await Assert.That(runs).Count().IsEqualTo(1);
        await Assert.That(runs[0].RunId).IsEqualTo(handle.RunId);
        await Assert.That(runs[0].MacroName).IsEqualTo("бут");
        await Assert.That(runs[0].CurrentNodeName).IsEqualTo("wait-in-world");
    }

    // F3 завела гонку: пишет панель, а демон узнаёт о файле наблюдателем с гашением
    // дребезга в 300 мс. «Сохранить», а сразу следом «Запустить» попадают в промежуток, где макроса
    // в снимке ещё нет, — и кнопка отвечала бы «макрос не найден» про файл, который только
    // что записали. На промахе диспетчер заглядывает на диск ещё раз.
    [Test]
    public async Task RunMacro_AMacroWrittenAMomentAgo_IsFoundByRereadingTheFolder()
    {
        using var harness = new IpcDispatcherHarness();

        // Пишем файл МИМО хранилища и НЕ зовём Refresh — так выглядит запись панели
        // в первые мгновения после «Сохранить».
        var folder = MacroBundleFolder.In(harness.BaseDirectory);
        Directory.CreateDirectory(folder);
        MacroBundleWriter.Write(MacroBundleFolder.PathFor(folder, "только-что"), new MacroBundleContent
        {
            Metadata = MacroBundleMetadata.CreateNew("только-что"),
            Graph = SimpleMacro("только-что"),
        });
        await Assert.That(harness.Macros.TryGet("только-что")).IsNull();

        var response = await harness.DispatchAsync(IpcMessageTypes.RunMacro, new RunMacroRequest("только-что"));

        await Assert.That(response.Ok).IsTrue();
        A.CallTo(() => harness.Runner.RunMacro("только-что")).MustHaveHappenedOnceExactly();
    }

    // Вторая сторона той же гонки, и она хуже первой. «Макроса ещё нет» отвечает ошибкой; «макрос
    // есть, но прошлой версии» не отвечает ничем — демон молча гоняет по живым клиентам
    // предыдущую правку вместе со старыми шаблонами, и симптом у этого один: «моя правка не
    // работает». Наблюдатель хранилища узнаёт о записи через 300 мс, поэтому смотрим сюда в
    // момент, когда бегун РАЗРЕШАЕТ имя, — именно этот снимок он и получит.
    [Test]
    public async Task RunMacro_AMacroEditedAMomentAgo_IsRefreshedBeforeTheRunnerResolvesIt()
    {
        using var harness = new IpcDispatcherHarness();
        harness.WriteMacro(SimpleMacro("правленый", VirtualKey.F1));

        // Панель переписала бандл; демону об этом никто не сказал.
        Overwrite(harness, SimpleMacro("правленый", VirtualKey.F9));

        // Снимок держит прежнюю версию — вот она, гонка. (300 мс гашения дребезга против двух
        // соседних строк: наблюдатель успеть не может.)
        await Assert.That(KeyOf(harness.Macros.TryGet("правленый")!)).IsEqualTo(VirtualKey.F1);

        MacroGraph? resolved = null;
        A.CallTo(() => harness.Runner.RunMacro("правленый"))
            .Invokes(() => resolved = harness.Macros.TryGet("правленый"));

        var response = await harness.DispatchAsync(IpcMessageTypes.RunMacro, new RunMacroRequest("правленый"));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(KeyOf(resolved!)).IsEqualTo(VirtualKey.F9);
    }

    // Обратная сторона: обычный путь в файловую систему НЕ ходит. Проверка стоит одной отметки
    // времени именно затем, чтобы не платить разбором каждого бандла в macros/ за каждое нажатие
    // ▸; выродись она в безусловный Refresh — этот тест краснеет. Файлу здесь искусственно
    // состарена дата записи: так выглядит любой макрос, которого сегодня не касались.
    [Test]
    public async Task RunMacro_AMacroUntouchedForAges_DoesNotRereadTheFolder()
    {
        using var harness = new IpcDispatcherHarness();
        harness.WriteMacro(SimpleMacro("давнишний", VirtualKey.F1));

        // На диске лежит другая версия, но дата записи старая — значит, наблюдатель о ней давно
        // рассказал бы, и заглядывать на диск не за чем.
        Overwrite(harness, SimpleMacro("давнишний", VirtualKey.F9));
        File.SetLastWriteTimeUtc(harness.MacroFile("давнишний"), DateTime.UtcNow.AddHours(-1));

        MacroGraph? resolved = null;
        A.CallTo(() => harness.Runner.RunMacro("давнишний"))
            .Invokes(() => resolved = harness.Macros.TryGet("давнишний"));

        var response = await harness.DispatchAsync(IpcMessageTypes.RunMacro, new RunMacroRequest("давнишний"));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(KeyOf(resolved!)).IsEqualTo(VirtualKey.F1);
    }

    /// <summary>Кладёт бандл поверх существующего мимо хранилища — так пишет панель.</summary>
    private static void Overwrite(IpcDispatcherHarness harness, MacroGraph graph) =>
        MacroBundleWriter.Write(harness.MacroFile(graph.Name), new MacroBundleContent
        {
            Metadata = MacroBundleMetadata.CreateNew(graph.Name),
            Graph = graph,
        });

    private static VirtualKey KeyOf(MacroGraph graph) => ((KeyPressNode)graph.Nodes[0]).Key;

    // ------------------------------------------------------------------------ хоткеи

    [Test]
    public async Task SuspendHotkeys_And_ResumeHotkeys_ReachTheListener()
    {
        using var harness = new IpcDispatcherHarness();

        await Assert.That((await harness.DispatchAsync(
            IpcMessageTypes.SuspendHotkeys, session: harness.Session)).Ok).IsTrue();
        await Assert.That((await harness.DispatchAsync(
            IpcMessageTypes.ResumeHotkeys, session: harness.Session)).Ok).IsTrue();

        A.CallTo(() => harness.Hotkeys.SuspendAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Hotkeys.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // Приостановка — состояние КЛИЕНТА, а не движка, и обработчик обязан это знать: снять её
    // способна только панель, а уходит панель не всегда через ResumeHotkeys. Без соединения за
    // спиной аренду некому было бы вернуть, поэтому запрос отклоняется — ровно как
    // SubscribeRunEvents и SubscribeLog.
    [Test]
    public async Task SuspendHotkeys_WithoutAConnection_IsRefused()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.SuspendHotkeys);

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).Contains("соединению");
        A.CallTo(() => harness.Hotkeys.SuspendAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    // D4: аккорды, которые демон привязал, а Windows ему не отдала. Без этого отказ остаётся
    // строчкой в файле лога, который труба не переносит, — а пользователь видит хоткей, который
    // выглядит привязанным и не делает ничего.
    [Test]
    public async Task GetHotkeyFailures_ReportsWhatRegisterHotKeyRefused()
    {
        using var harness = new IpcDispatcherHarness();
        A.CallTo(() => harness.Hotkeys.Failures).Returns(
            [new HotkeyFailureDto("баг-госта", HotkeyModifiers.Win, VirtualKey.L)]);

        var response = await harness.DispatchAsync(IpcMessageTypes.GetHotkeyFailures);
        var failures = IpcJson.Read<HotkeyFailureDto[]>(response.Payload);

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(failures).IsNotNull();
        await Assert.That(failures!).Count().IsEqualTo(1);
        await Assert.That(failures![0].MacroName).IsEqualTo("баг-госта");
        await Assert.That(failures[0].Modifiers).IsEqualTo(HotkeyModifiers.Win);
        await Assert.That(failures[0].Key).IsEqualTo(VirtualKey.L);
    }

    [Test]
    public async Task GetHotkeyFailures_WithNothingWrong_IsAnEmptyArray()
    {
        using var harness = new IpcDispatcherHarness();
        A.CallTo(() => harness.Hotkeys.Failures).Returns([]);

        var response = await harness.DispatchAsync(IpcMessageTypes.GetHotkeyFailures);

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(IpcJson.Read<HotkeyFailureDto[]>(response.Payload)).IsNotNull();
        await Assert.That(IpcJson.Read<HotkeyFailureDto[]>(response.Payload)!).IsEmpty();
    }

    // ---------------------------------------------------------------------- диагностика

    [Test]
    public async Task DumpCaptures_CreatesTheFolderAndAnswersWithItsPath()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.DumpCaptures);

        await Assert.That(response.Ok).IsTrue();
        var folder = IpcJson.Read<string>(response.Payload)!;
        await Assert.That(folder).IsEqualTo(harness.Captures.FolderPath);
        await Assert.That(Directory.Exists(folder)).IsTrue();
    }

    [Test]
    public async Task RequestActivate_BroadcastsActivateWindow_ToEveryClient()
    {
        using var harness = new IpcDispatcherHarness();
        var sent = new List<IpcEvent>();
        var broadcaster = A.Fake<IIpcBroadcaster>();
        A.CallTo(() => broadcaster.Broadcast(A<IpcEvent>._))
            .Invokes((IpcEvent evt) => sent.Add(evt));
        harness.Dispatcher.AttachBroadcaster(broadcaster);

        var response = await harness.DispatchAsync(IpcMessageTypes.RequestActivate);

        // Рассылка, а не нагрузка ответа: спрашивающий — это второй запуск интерфейса, который
        // сейчас завершится, а та панель, которая обязана выйти вперёд, — вообще другое
        // соединение.
        await Assert.That(response.Ok).IsTrue();
        await Assert.That(sent.Select(evt => evt.Type)).IsEquivalentTo(new[] { IpcMessageTypes.ActivateWindow });
        await Assert.That(sent[0].Payload).IsNull();
    }

    [Test]
    public async Task RequestActivate_WithNoServerAttached_IsRejectedRatherThanThrowing()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.RequestActivate);

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsNotNull();
    }

    [Test]
    public async Task Shutdown_AnswersOk_ThenStopsTheHost()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.Shutdown);

        // Порядок здесь и есть договорённость: ответ обязан быть выдан прежде, чем что-либо
        // начнёт разбирать хост (а с ним и это соединение).
        await Assert.That(response.Ok).IsTrue();
        A.CallTo(() => harness.Lifetime.StopApplication()).MustNotHaveHappened();

        var stopped = await WaitUntilAsync(() =>
        {
            try
            {
                A.CallTo(() => harness.Lifetime.StopApplication()).MustHaveHappened();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
        await Assert.That(stopped).IsTrue();
    }

    // -------------------------------------------------------------------- пути отказа

    [Test]
    public async Task UnknownRequestType_IsRejectedWithAReadableError()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync("TeleportPlayer");

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsEqualTo("unknown request type: TeleportPlayer");
        await Assert.That(response.Id).IsEqualTo(1);
    }

    [Test]
    public async Task HandlerException_BecomesAFailedResponse_NotAThrow()
    {
        using var harness = new IpcDispatcherHarness();
        A.CallTo(() => harness.Hotkeys.SuspendAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("RegisterHotKey сломался"));

        var response = await harness.DispatchAsync(
            IpcMessageTypes.SuspendHotkeys, id: 99, session: harness.Session);

        await Assert.That(response.Id).IsEqualTo(99);
        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsEqualTo("RegisterHotKey сломался");
    }

    [Test]
    public async Task ResponseIdAlwaysEchoesTheRequestId()
    {
        using var harness = new IpcDispatcherHarness();

        foreach (var id in new[] { 0, 1, int.MaxValue })
        {
            var ok = await harness.DispatchAsync(IpcMessageTypes.GetWindows, id: id);
            var bad = await harness.DispatchAsync("НеизвестныйТип", id: id);
            await Assert.That(ok.Id).IsEqualTo(id);
            await Assert.That(bad.Id).IsEqualTo(id);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }
}
