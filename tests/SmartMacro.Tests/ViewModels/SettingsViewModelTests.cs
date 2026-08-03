using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Contracts.Settings;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D6: экран настроек. Проверяется механика, а не список полей, — потому что механика здесь и есть
// содержание волны:
//
//   * правки КОПЯТСЯ и уходят по «Применить» (поле, применяющееся на каждое нажатие клавиши,
//     означает, что «12» по дороге побудет «1», и демон честно поработает с интервалом в секунду);
//   * уровень журнала — единственное исключение, он уходит сразу и правкой НЕ считается, потому
//     что никуда не сохраняется;
//   * пуш SettingsChanged (в том числе от правки файла блокнотом) не затирает недописанную форму;
//   * то, чего экран не показывает, обязано пережить сохранение — иначе панель молча стирала бы
//     то, что правили в файле;
//   * сигнал побудки проносится нетронутым и проставляется по таблице ТОЛЬКО при создании профиля.
public class SettingsViewModelTests
{
    private static SettingsSnapshotDto Snapshot(AppSettings? settings = null,
        LogLevelDto level = LogLevelDto.Information) =>
        new(settings ?? AppSettings.Default, level, @"C:\smartmacro\settings.json", @"C:\smartmacro");

    private static SettingsViewModel Create(FakeIpcClient client, SettingsSnapshotDto? snapshot = null)
    {
        client.Respond(IpcMessageTypes.GetSettings, snapshot ?? Snapshot());
        client.Respond(IpcMessageTypes.SaveSettings, Array.Empty<SettingsIssue>());
        var vm = new SettingsViewModel(client, ImmediateUiDispatcher.Instance);
        // Конструктор ничего не запрашивает: снимок приезжает по Connected, ровно как у остальных
        // view-model. FakeIpcClient завершает всё синхронно, так что к возврату уже загружено.
        client.RaiseConnected();
        return vm;
    }

    // ---- загрузка ----------------------------------------------------------------------------

    [Test]
    public async Task Connected_LoadsTheSnapshotIntoFields()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client, Snapshot(AppSettings.Default with
        {
            Watch = new WatchSettings { ProcessPollIntervalSeconds = 4, WindowPollIntervalSeconds = 6 },
            Input = new InputSettings { DefaultMethod = InputMethod.PostMessage },
            Vision = new VisionSettings { MatchThreshold = 0.55 },
            Startup = new StartupSettings { RunAtLogon = true, RunElevated = false },
        }, LogLevelDto.Debug));

        await Assert.That(vm.ProcessPollSeconds).IsEqualTo("4");
        await Assert.That(vm.WindowPollSeconds).IsEqualTo("6");
        await Assert.That(vm.MatchThreshold).IsEqualTo(0.55);
        await Assert.That(vm.MatchThresholdText).IsEqualTo("0.55");
        await Assert.That(vm.DefaultInput.Method).IsEqualTo(InputMethod.PostMessage);
        await Assert.That(vm.RunAtLogon).IsTrue();
        await Assert.That(vm.RunElevated).IsFalse();
        await Assert.That(vm.LogLevel.Level).IsEqualTo(LogLevelDto.Debug);
        await Assert.That(vm.FolderPath).IsEqualTo(@"C:\smartmacro");
        await Assert.That(vm.IsDirty).IsFalse();
    }

    // Экран не предлагает SendInput: проверить его без живой игры нельзя, а непроверенный способ
    // в списке хуже отсутствующего.
    [Test]
    public async Task InputChoices_DoNotOfferTheUnimplementedMethod()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);

        await Assert.That(vm.InputChoices.Select(c => c.Method))
            .IsEquivalentTo(new InputMethod?[] { InputMethod.SendMessage, InputMethod.PostMessage });
        // У профиля тот же список плюс «По умолчанию».
        await Assert.That(vm.ProfileInputChoices[0].Method).IsNull();
        await Assert.That(vm.ProfileInputChoices).Count().IsEqualTo(3);
    }

    // ---- накопление правок --------------------------------------------------------------------

    [Test]
    public async Task EditingFields_CountsChangesWithoutSending()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        var before = client.CountOf(IpcMessageTypes.SaveSettings);

        vm.ProcessPollSeconds = "5";
        vm.RunAtLogon = true;

        await Assert.That(vm.ChangeCount).IsEqualTo(2);
        await Assert.That(vm.IsDirty).IsTrue();
        await Assert.That(vm.ChangeText).IsEqualTo("2 изменения не применены");
        await Assert.That(client.CountOf(IpcMessageTypes.SaveSettings)).IsEqualTo(before);
    }

    [Test]
    public async Task Revert_DropsEditsAndTheCounter()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        vm.ProcessPollSeconds = "5";

        vm.Revert();

        await Assert.That(vm.ProcessPollSeconds).IsEqualTo("1");
        await Assert.That(vm.IsDirty).IsFalse();
    }

    [Test]
    public async Task Apply_SendsTheEditedSettings()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        vm.ProcessPollSeconds = "5";
        vm.MatchThreshold = 0.42;
        vm.DefaultInput = vm.InputChoices.Single(c => c.Method == InputMethod.PostMessage);

        await vm.ApplyAsync();

        var sent = client.PayloadsOf<SaveSettingsRequest>(IpcMessageTypes.SaveSettings).Single().Settings;
        await Assert.That(sent!.Watch.ProcessPollIntervalSeconds).IsEqualTo(5);
        await Assert.That(sent.Vision.MatchThreshold).IsEqualTo(0.42);
        await Assert.That(sent.Input.DefaultMethod).IsEqualTo(InputMethod.PostMessage);
    }

    // Экран показывает из Vision только порог по умолчанию. Всё остальное обязано доехать до файла
    // нетронутым: сохранение из панели не имеет права стирать то, что правили руками.
    [Test]
    public async Task Apply_PreservesTheVisionFieldsTheScreenDoesNotShow()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client, Snapshot(AppSettings.Default with
        {
            Vision = new VisionSettings
            {
                MatchThreshold = 0.7,
                PollIntervalMs = 321,
                ClassMatcher = new ClassMatcherSettings { LuminanceThreshold = 177, MatchThreshold = 0.33 },
            },
        }));

        vm.MatchThreshold = 0.5;
        await vm.ApplyAsync();

        var sent = client.PayloadsOf<SaveSettingsRequest>(IpcMessageTypes.SaveSettings).Single().Settings!;
        await Assert.That(sent.Vision.MatchThreshold).IsEqualTo(0.5);
        await Assert.That(sent.Vision.PollIntervalMs).IsEqualTo(321);
        await Assert.That(sent.Vision.ClassMatcher.LuminanceThreshold).IsEqualTo(177);
        await Assert.That(sent.Vision.ClassMatcher.MatchThreshold).IsEqualTo(0.33);
    }

    // Недопечатанное число — забота того, кто видит курсор, а не демона. Запрос не уходит вовсе.
    [Test]
    public async Task Apply_RejectsUnparseableNumbersWithoutSending()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        vm.ProcessPollSeconds = "быстро";

        await vm.ApplyAsync();

        await Assert.That(client.CountOf(IpcMessageTypes.SaveSettings)).IsEqualTo(0);
        await Assert.That(vm.HasIssues).IsTrue();
        await Assert.That(vm.Issues.Single()).Contains("быстро");
    }

    // Те же правила, что у демона, — из общего валидатора. Прогоняются здесь ради мгновенного
    // ответа, а не вместо демонских.
    [Test]
    public async Task Apply_RunsTheSharedValidatorBeforeSending()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        vm.ProcessPollSeconds = "0";

        await vm.ApplyAsync();

        await Assert.That(client.CountOf(IpcMessageTypes.SaveSettings)).IsEqualTo(0);
        await Assert.That(vm.HasIssues).IsTrue();
    }

    [Test]
    public async Task Apply_ShowsTheDaemonsRefusal()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        client.Respond(IpcMessageTypes.SaveSettings,
            new[] { new SettingsIssue("Watch.ProcessPollIntervalSeconds", "демону не понравилось") });
        vm.ProcessPollSeconds = "5";

        await vm.ApplyAsync();

        await Assert.That(vm.HasIssues).IsTrue();
        await Assert.That(vm.Issues.Single()).IsEqualTo("демону не понравилось");
    }

    // ---- пуш --------------------------------------------------------------------------------

    [Test]
    public async Task SettingsChangedPush_ReloadsWhenNothingIsBeingEdited()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);

        client.RaiseEvent(IpcMessageTypes.SettingsChanged, Snapshot(AppSettings.Default with
        {
            Watch = new WatchSettings { ProcessPollIntervalSeconds = 9, WindowPollIntervalSeconds = 2 },
        }));

        await Assert.That(vm.ProcessPollSeconds).IsEqualTo("9");
        await Assert.That(vm.IsDirty).IsFalse();
    }

    // Пуш приходит и от правки файла блокнотом. Стереть на нём наполовину заполненную форму
    // означало бы наказать пользователя за то, что он открыл файл в соседнем окне.
    [Test]
    public async Task SettingsChangedPush_KeepsUnsavedEdits()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        vm.ProcessPollSeconds = "5";

        client.RaiseEvent(IpcMessageTypes.SettingsChanged, Snapshot(AppSettings.Default with
        {
            Watch = new WatchSettings { ProcessPollIntervalSeconds = 9, WindowPollIntervalSeconds = 2 },
        }));

        await Assert.That(vm.ProcessPollSeconds).IsEqualTo("5");
        await Assert.That(vm.IsDirty).IsTrue();
        // «Отменить» после этого покажет уже НОВОЕ состояние файла, а не то, что было при загрузке.
        vm.Revert();
        await Assert.That(vm.ProcessPollSeconds).IsEqualTo("9");
    }

    // ---- уровень журнала ---------------------------------------------------------------------

    // Единственное поле экрана, которое уходит немедленно: оно не часть файла, копить его в
    // «Применить» значило бы приписать ему чужие свойства.
    [Test]
    public async Task LogLevel_IsSentImmediatelyAndIsNotACountedChange()
    {
        var client = new FakeIpcClient();
        client.Respond(IpcMessageTypes.SetLogLevel, Snapshot(level: LogLevelDto.Debug));
        using var vm = Create(client);

        vm.LogLevel = SettingsViewModel.LogLevels.Single(c => c.Level == LogLevelDto.Debug);

        await Assert.That(client.PayloadsOf<SetLogLevelRequest>(IpcMessageTypes.SetLogLevel).Single().Level)
            .IsEqualTo(LogLevelDto.Debug);
        await Assert.That(vm.IsDirty).IsFalse();
    }

    // ---- профили -----------------------------------------------------------------------------

    // Таблица известных сигналов применяется ПРИ СОЗДАНИИ и только к узнанному имени — именно это
    // и позволило убрать число из интерфейса, не потеряв смысл его отсутствия.
    [Test]
    public async Task AddProfile_FillsTheWakeSignalOnlyForKnownNames()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client, Snapshot(AppSettings.Default with { Profiles = [] }));

        vm.NewProfileName = "elementclient_64";
        vm.AddProfile();
        vm.NewProfileName = "notepad";
        vm.AddProfile();

        await Assert.That(vm.Profiles[0].WakesWindows).IsTrue();
        await Assert.That(vm.Profiles[0].WakeText).IsEqualTo("есть");
        await Assert.That(vm.Profiles[1].WakesWindows).IsFalse();
        await Assert.That(vm.Profiles[1].WakeText).IsEqualTo("не нужен");

        await vm.ApplyAsync();

        var sent = client.PayloadsOf<SaveSettingsRequest>(IpcMessageTypes.SaveSettings).Single().Settings!;
        await Assert.That(sent.Profiles[0].ActivationLParam).IsEqualTo(37336u);
        await Assert.That(sent.Profiles[1].ActivationLParam).IsNull();
    }

    // Экран не показывает сигнал побудки числом и потому не имеет права им распоряжаться: правка
    // любого другого поля строки обязана донести его нетронутым.
    [Test]
    public async Task EditingAProfile_CarriesTheWakeSignalThroughUntouched()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);

        vm.Profiles.Single().SettleDelayMs = "350";
        await vm.ApplyAsync();

        var sent = client.PayloadsOf<SaveSettingsRequest>(IpcMessageTypes.SaveSettings).Single().Settings!;
        await Assert.That(sent.Profiles[0].ActivationLParam).IsEqualTo(37336u);
        await Assert.That(sent.Profiles[0].SettleDelayMs).IsEqualTo(350);
    }

    [Test]
    public async Task RemoveProfile_CountsAsAChangeAndDropsTheRow()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);

        vm.RemoveProfile(vm.Profiles.Single());

        await Assert.That(vm.Profiles).IsEmpty();
        await Assert.That(vm.ChangeCount).IsEqualTo(1);
    }

    // ---- диагностика --------------------------------------------------------------------------

    // Пять проверок делает демон, шестую — время ответа канала — панель: демон не может честно
    // измерить время ответа самому себе.
    [Test]
    public async Task RunDiagnostics_AddsThePanelsOwnChannelCheck()
    {
        var client = new FakeIpcClient();
        client.Respond(IpcMessageTypes.RunDiagnostics, new[]
        {
            new DiagnosticDto(DiagnosticIds.Elevation, DiagnosticStatus.Ok, "Права", "ок"),
            new DiagnosticDto(DiagnosticIds.Templates, DiagnosticStatus.Failed, "Нет шаблона", "подробности"),
        });
        using var vm = Create(client);

        await vm.RunDiagnosticsAsync();

        await Assert.That(vm.Diagnostics.Select(d => d.Id))
            .IsEquivalentTo(new[] { DiagnosticIds.Channel, DiagnosticIds.Elevation, DiagnosticIds.Templates });
        await Assert.That(vm.ProblemCount).IsEqualTo(1);
        await Assert.That(vm.DiagnosticsSummary).Contains("2 из 3");
    }

    // Демон не ответил — это САМ ПО СЕБЕ вердикт, и самый важный: движка нет, макросы не идут.
    [Test]
    public async Task RunDiagnostics_ReportsASilentDaemonAsAFailedChannel()
    {
        var client = new FakeIpcClient();
        client.Fail(IpcMessageTypes.RunDiagnostics, "труба закрыта");
        using var vm = Create(client);

        await vm.RunDiagnosticsAsync();

        var row = vm.Diagnostics.Single();
        await Assert.That(row.Id).IsEqualTo(DiagnosticIds.Channel);
        await Assert.That(row.IsFailed).IsTrue();
        await Assert.That(vm.ProblemCount).IsEqualTo(1);
    }

    // ---- сброс ------------------------------------------------------------------------------

    [Test]
    public async Task Reset_TakesTheStateTheDaemonAnswersWith()
    {
        var client = new FakeIpcClient();
        using var vm = Create(client);
        client.Respond(IpcMessageTypes.ResetSettings, Snapshot(AppSettings.Default with
        {
            Watch = new WatchSettings { ProcessPollIntervalSeconds = 1, WindowPollIntervalSeconds = 2 },
        }));
        vm.ProcessPollSeconds = "42";

        await vm.ResetAsync();

        await Assert.That(vm.ProcessPollSeconds).IsEqualTo("1");
        await Assert.That(vm.IsDirty).IsFalse();
    }
}
