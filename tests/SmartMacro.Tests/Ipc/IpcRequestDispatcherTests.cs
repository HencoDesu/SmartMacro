using FakeItEasy;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Macros;

namespace SmartMacro.Tests.Ipc;

// Stage 2B: the whole IpcMessageTypes catalogue, one handler at a time — the happy path
// plus the failure that actually matters for each. The two worth calling out:
//
//   * SaveMacro with a validation ERROR must leave the disk untouched. Asserted against a
//     real temp-folder store, because "did not write" is not something a fake can prove.
//   * A handler must never throw across the wire: an unknown type and an exploding
//     dependency both have to come back as Ok=false with a message.
public class IpcRequestDispatcherTests
{
    private static MacroGraph SimpleMacro(string name, VirtualKey key = VirtualKey.F1) => new()
    {
        Name = name,
        StartNodeId = "n0",
        Nodes = [new KeyPressNode { Id = "n0", Key = key, Target = new TargetSelector() }],
    };

    // ------------------------------------------------------------------------ windows

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

        // The UI always tags from a snapshot that may be a moment stale; a client is not
        // wrong just because the game client closed in the meantime.
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

    // ------------------------------------------------------------------------- macros

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

        // Stand in for Orchestrator.RunAsync: run until cancelled, then Complete() in the
        // finally. Without an acknowledgement StopAsync never returns, which is precisely
        // the contract the handler leans on.
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

        // Documented as success: a run that finished on its own between the UI's snapshot
        // and the click has already done what Stop was asking for.
        var response = await harness.DispatchAsync(IpcMessageTypes.StopMacro, new StopMacroRequest(Guid.NewGuid()));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(response.Error).IsNull();
    }

    [Test]
    public async Task GetRunningMacros_ReturnsTheRunRegistrySnapshot()
    {
        using var harness = new IpcDispatcherHarness();
        var handle = harness.Runs.TryBegin("бут")!;
        handle.CurrentNodeId = "wait-in-world";

        var response = await harness.DispatchAsync(IpcMessageTypes.GetRunningMacros);

        var runs = IpcJson.Read<RunningMacroDto[]>(response.Payload)!;
        await Assert.That(runs).Count().IsEqualTo(1);
        await Assert.That(runs[0].RunId).IsEqualTo(handle.RunId);
        await Assert.That(runs[0].MacroName).IsEqualTo("бут");
        await Assert.That(runs[0].CurrentNodeId).IsEqualTo("wait-in-world");
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
        // Same witness IpcGraphPayloadTests uses, but end to end: store → handler → payload.
        // A dropped $type discriminator anywhere on that path shows up as a diff here.
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
            // Edge into a node that isn't in the graph — a hard error, not a warning.
            StartNodeId = "n0",
            Nodes = [new KeyPressNode { Id = "n0", Key = VirtualKey.F1, Target = new TargetSelector(), Next = "нетуноды" }],
        };

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveMacro, new SaveMacroRequest(broken));

        await Assert.That(response.Ok).IsTrue();
        var issues = IpcJson.Read<ValidationIssueDto[]>(response.Payload)!;
        await Assert.That(issues).IsNotEmpty();
        await Assert.That(issues.Any(i => i.Severity == nameof(SmartMacro.Macros.Validation.ValidationSeverity.Error))).IsTrue();

        // The assertion the whole handler exists for.
        await Assert.That(File.Exists(harness.MacroFile("битый"))).IsFalse();
        await Assert.That(harness.Macros.TryGet("битый")).IsNull();
    }

    [Test]
    public async Task SaveMacro_WithAnIllegalFileName_IsRejectedAsAValidationIssue()
    {
        using var harness = new IpcDispatcherHarness();
        var graph = SimpleMacro("плохое/имя");

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveMacro, new SaveMacroRequest(graph));

        // The store would throw ArgumentException here; surfacing it as an issue instead is
        // what lets the editor show it next to the structural errors.
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
            StartNodeId = "n0",
            Nodes =
            [
                new KeyPressNode { Id = "n0", Key = VirtualKey.F1, Target = new TargetSelector() },
                // Nothing points here → unreachable → warning, not an error.
                new DelayNode { Id = "orphan", Ms = 100 },
            ],
        };

        var response = await harness.DispatchAsync(IpcMessageTypes.SaveMacro, new SaveMacroRequest(withUnreachableNode));

        // Empty list means "written" by contract — warnings do not block a save and are
        // deliberately not reported here, because a non-empty list means rejection.
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

    // ------------------------------------------------------------------------ hotkeys

    [Test]
    public async Task SuspendHotkeys_And_ResumeHotkeys_ReachTheListener()
    {
        using var harness = new IpcDispatcherHarness();

        await Assert.That((await harness.DispatchAsync(IpcMessageTypes.SuspendHotkeys)).Ok).IsTrue();
        await Assert.That((await harness.DispatchAsync(IpcMessageTypes.ResumeHotkeys)).Ok).IsTrue();

        A.CallTo(() => harness.Hotkeys.SuspendAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Hotkeys.ResumeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // D4: chords the daemon has bound but Windows would not give it. Without this the
    // failure is a line in a log file the pipe never carries, and the user sees a hotkey
    // that looks bound and does nothing.
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
        await Assert.That(failures[0].MacroName).IsEqualTo("баг-госта");
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

    // -------------------------------------------------------------------- diagnostics

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

        // Broadcast, not a reply payload: the asker is a second UI launch about to exit and
        // the panel that must come forward is a different connection entirely.
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

        // Ordering is the contract: the reply has to be produced before anything starts
        // tearing the host (and therefore this connection) down.
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

    // -------------------------------------------------------------------- error paths

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
