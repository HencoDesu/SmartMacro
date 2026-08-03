using FakeItEasy;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Macros;

namespace SmartMacro.Tests.Ipc;

// Стадия 2B: весь каталог IpcMessageTypes, по одному обработчику за раз — счастливый путь плюс
// тот отказ, который для каждого из них по-настоящему важен. Два стоит назвать отдельно:
//
//   * SaveMacro с ОШИБКОЙ проверки обязан оставить диск нетронутым. Проверяется на настоящем
//     хранилище во временной папке, потому что «не записал» — это не то, что способна доказать
//     подделка.
//   * Обработчик не имеет права бросить исключение через провод: и неизвестный тип, и
//     взорвавшаяся зависимость обязаны вернуться как Ok=false с сообщением.
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
        await harness.Macros.SaveAsync(SimpleMacro("иммунка"));

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

    [Test]
    public async Task GetMacros_RoundTripsEveryNodeAndTriggerTypeThroughTheWire()
    {
        using var harness = new IpcDispatcherHarness();
        var original = FullMacroGraphFixture.Build("полный");
        await harness.Macros.SaveAsync(original);

        var response = await harness.DispatchAsync(IpcMessageTypes.GetMacros);

        var macros = IpcJson.Read<MacroGraph[]>(response.Payload)!;
        await Assert.That(macros).Count().IsEqualTo(1);
        // Тот же свидетель, что и в IpcGraphPayloadTests, только из конца в конец:
        // хранилище → обработчик → нагрузка. Потерянный где угодно на этом пути дискриминатор
        // $type всплывёт здесь расхождением.
        await Assert.That(MacroGraphJson.Serialize(macros[0])).IsEqualTo(MacroGraphJson.Serialize(original));
    }

    [Test]
    public async Task SaveMacro_ValidGraph_WritesItAndAnswersWithAnEmptyIssueList()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(
            IpcMessageTypes.SaveMacro,
            new SaveMacroRequest(SimpleMacro("ассист", VirtualKey.F2)));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(IpcJson.Read<ValidationIssueDto[]>(response.Payload)!).IsEmpty();
        await Assert.That(File.Exists(harness.MacroFile("ассист"))).IsTrue();
        await Assert.That(harness.Macros.TryGet("ассист")).IsNotNull();
    }

    [Test]
    public async Task SaveMacro_WithAValidationError_WritesNothingAndReturnsTheIssues()
    {
        using var harness = new IpcDispatcherHarness();
        var broken = new MacroGraph
        {
            Name = "битый",
            // Ребро в ноду, которой в графе нет, — это жёсткая ошибка, а не предупреждение.
            StartNodeId = Ids.Of("n0"),
            Nodes =
            [
                new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = VirtualKey.F1, Target = new TargetSelector(), Next = Ids.Of("нетуноды") }
            ],
        };

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveMacro, new SaveMacroRequest(broken));

        await Assert.That(response.Ok).IsTrue();
        var issues = IpcJson.Read<ValidationIssueDto[]>(response.Payload)!;
        await Assert.That(issues).IsNotEmpty();
        await Assert.That(issues.Any(i => i.Severity == nameof(SmartMacro.Macros.Validation.ValidationSeverity.Error)))
            .IsTrue();

        // Та самая проверка, ради которой весь обработчик и существует.
        await Assert.That(File.Exists(harness.MacroFile("битый"))).IsFalse();
        await Assert.That(harness.Macros.TryGet("битый")).IsNull();
    }

    [Test]
    public async Task SaveMacro_WithAnIllegalFileName_IsRejectedAsAValidationIssue()
    {
        using var harness = new IpcDispatcherHarness();
        var graph = SimpleMacro("плохое/имя");

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveMacro, new SaveMacroRequest(graph));

        // Хранилище бросило бы здесь ArgumentException; именно то, что вместо этого ошибку
        // подают как замечание, и позволяет редактору показать её рядом со структурными.
        await Assert.That(response.Ok).IsTrue();
        var issues = IpcJson.Read<ValidationIssueDto[]>(response.Payload)!;
        await Assert.That(issues.Any(i => i.Message.Contains('/'))).IsTrue();
        await Assert.That(harness.Macros.All).IsEmpty();
    }

    [Test]
    public async Task SaveMacro_WithWarningsOnly_StillSaves()
    {
        using var harness = new IpcDispatcherHarness();
        var withUnreachableNode = new MacroGraph
        {
            Name = "спредупреждением",
            StartNodeId = Ids.Of("n0"),
            Nodes =
            [
                new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = VirtualKey.F1, Target = new TargetSelector() },
                // Сюда ничто не ведёт → недостижима → предупреждение, а не ошибка.
                new DelayNode { Id = Ids.Of("orphan"), DisplayName = "orphan", Ms = 100 },
            ],
        };

        var response =
            await harness.DispatchAsync(IpcMessageTypes.SaveMacro, new SaveMacroRequest(withUnreachableNode));

        // Пустой список по договорённости означает «записано»: предупреждения сохранению не
        // мешают и здесь намеренно не сообщаются, потому что непустой список означает отказ.
        await Assert.That(IpcJson.Read<ValidationIssueDto[]>(response.Payload)!).IsEmpty();
        await Assert.That(File.Exists(harness.MacroFile("спредупреждением"))).IsTrue();
    }

    [Test]
    public async Task SaveMacro_WithoutAPayload_Fails()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveMacro);

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).Contains("SaveMacro");
    }

    [Test]
    public async Task DeleteMacro_RemovesTheFile_AndIsANoOpForAnUnknownName()
    {
        using var harness = new IpcDispatcherHarness();
        await harness.Macros.SaveAsync(SimpleMacro("временный"));

        var deleted = await harness.DispatchAsync(IpcMessageTypes.DeleteMacro, new DeleteMacroRequest("временный"));
        await Assert.That(deleted.Ok).IsTrue();
        await Assert.That(File.Exists(harness.MacroFile("временный"))).IsFalse();

        var again = await harness.DispatchAsync(IpcMessageTypes.DeleteMacro, new DeleteMacroRequest("временный"));
        await Assert.That(again.Ok).IsTrue();
    }

    // ------------------------------------------------------------------------ хоткеи

    [Test]
    public async Task SuspendHotkeys_And_ResumeHotkeys_ReachTheListener()
    {
        using var harness = new IpcDispatcherHarness();

        await Assert.That((await harness.DispatchAsync(IpcMessageTypes.SuspendHotkeys)).Ok).IsTrue();
        await Assert.That((await harness.DispatchAsync(IpcMessageTypes.ResumeHotkeys)).Ok).IsTrue();

        A.CallTo(() => harness.Hotkeys.SuspendAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Hotkeys.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
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

    // ------------------------------------------------------------------------- шаблоны

    // 1×1 PNG: заголовок настоящий, потому что именно его читает Catalog().
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Test]
    public async Task GetTemplates_ListsTheWholeTree_WithoutASinglePixel()
    {
        using var harness = new IpcDispatcherHarness();
        harness.WriteTemplate(null, "ServerSelectButton", OnePixelPng);
        harness.WriteTemplate("classes", "Лучник", OnePixelPng);

        var response = await harness.DispatchAsync(IpcMessageTypes.GetTemplates);
        var templates = IpcJson.Read<TemplateDto[]>(response.Payload);

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(templates).IsNotNull();
        await Assert.That(templates!.Select(t => $"{t.Set}/{t.Name}"))
            .IsEquivalentTo(new[] { "/ServerSelectButton", "classes/Лучник" });
        // Размеры — да, байты — нет: за байтами ходят по одному файлу, и вся суть разделения в
        // том, чтобы список можно было тянуть целиком, не завалив трубу картинками.
        await Assert.That(templates![0].Width).IsEqualTo(1);
        await Assert.That(templates[0].Bytes).IsEqualTo(OnePixelPng.Length);
    }

    [Test]
    public async Task GetTemplates_WithNoTreeAtAll_IsAnEmptyArray()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(IpcMessageTypes.GetTemplates);

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(IpcJson.Read<TemplateDto[]>(response.Payload)!).IsEmpty();
    }

    [Test]
    public async Task GetTemplateImage_AnswersWithTheBytes_AndEchoesWhichTemplate()
    {
        using var harness = new IpcDispatcherHarness();
        harness.WriteTemplate("classes", "Жрец", OnePixelPng);

        var response = await harness.DispatchAsync(
            IpcMessageTypes.GetTemplateImage,
            new GetTemplateImageRequest("classes", "Жрец"));
        var image = IpcJson.Read<TemplateImageDto>(response.Payload);

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(image).IsNotNull();
        await Assert.That(image!.Png).IsEquivalentTo(OnePixelPng);
        // Эхо нужно панели против гонки выделения: щёлкнув по списку быстрее, чем отвечает
        // демон, она обязана уметь отличить ответ на предпоследний выбор от ответа на последний.
        await Assert.That(image.Describes("classes", "Жрец")).IsTrue();
        await Assert.That(image.Describes(null, "Жрец")).IsFalse();
    }

    [Test]
    public async Task GetTemplateImage_ForSomethingThatIsNotThere_Fails()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(
            IpcMessageTypes.GetTemplateImage,
            new GetTemplateImageRequest(null, "НетТакого"));

        // Отказ, а не пустая картинка: панель просит по строке из собственного списка, так что
        // промах означает, что список устарел, и молчание спрятало бы ровно это.
        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).Contains("НетТакого");
    }

    [Test]
    public async Task GetTemplateImage_RefusesAPathDressedUpAsAName()
    {
        using var harness = new IpcDispatcherHarness();

        var response = await harness.DispatchAsync(
            IpcMessageTypes.GetTemplateImage,
            new GetTemplateImageRequest(null, @"..\..\appsettings"));

        await Assert.That(response.Ok).IsFalse();
    }

    [Test]
    public async Task GetTemplateImage_RefusesAnythingOverThePreviewCeiling()
    {
        using var harness = new IpcDispatcherHarness();
        harness.WriteTemplate(null, "Огромный", new byte[TemplateLimits.MaxImageBytes + 1]);

        var response = await harness.DispatchAsync(
            IpcMessageTypes.GetTemplateImage,
            new GetTemplateImageRequest(null, "Огромный"));

        // Труба у превью общая с потоком событий прогона, и запись в неё сериализована: строка
        // base64 на много мегабайт встала бы перед пачкой событий живого макроса.
        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).Contains("потолк");
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

        var response = await harness.DispatchAsync(IpcMessageTypes.SuspendHotkeys, id: 99);

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
