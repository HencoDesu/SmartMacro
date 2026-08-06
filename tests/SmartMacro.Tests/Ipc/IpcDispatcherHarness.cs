using FakeItEasy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Hotkeys;
using SmartMacro.Settings;
using SmartMacro.Ipc;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Orchestration;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Ipc;

/// <summary>
/// Диспетчер, собранный с настоящим движком настолько, насколько его дёшево построить.
///
/// Настоящие: <see cref="WindowRegistry"/>, <see cref="MacroRunRegistry"/>,
/// <see cref="MacroGraphStore"/> над временной папкой. Это те части, ПОВЕДЕНИЕ которых
/// обработчики и обязаны наружу выставлять, так что, подделав их, мы проверяли бы разве что тот
/// факт, что диспетчер зовёт методы, которые мы ему и написали звать.
///
/// Подделаны: <see cref="IMacroRunner"/> и <see cref="IHotkeyRegistration"/> (настоящий
/// <see cref="Orchestrator"/> или <see cref="HotkeyListener"/> тянет за собой монитор процессов,
/// фабрику агентов и два монитора Win32) и <see cref="IHostApplicationLifetime"/> — модульный
/// тест, который взаправду останавливает хост, не нужен никому.
/// </summary>
internal sealed class IpcDispatcherHarness : IDisposable
{
    private readonly string _baseDirectory;

    public IpcDispatcherHarness()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), $"smartmacro-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_baseDirectory);

        Windows = new WindowRegistry(NullLogger<WindowRegistry>.Instance);
        Runs = new MacroRunRegistry(NullLogger<MacroRunRegistry>.Instance);
        Macros = new MacroGraphStore(_baseDirectory, NullLogger<MacroGraphStore>.Instance);

        Runner = A.Fake<IMacroRunner>();
        Hotkeys = A.Fake<IHotkeyRegistration>();
        Lifetime = A.Fake<IHostApplicationLifetime>();

        RunEvents = new RunEventPublisher(NullLogger<RunEventPublisher>.Instance);
        Log = new LogEventPublisher();
        Debug = new MacroDebugSession(NullLogger<MacroDebugSession>.Instance);

        // Настоящее хранилище над той же временной папкой: обработчики настроек — тонкий слой
        // над ним, и подделав его, мы проверяли бы только собственный маппер. Конструктор при
        // этом сам создаст settings.json с умолчаниями — ровно то, что делает демон при первом
        // запуске, и заодно бесплатная проверка, что это не падает.
        SettingsFile = new SettingsStore(_baseDirectory, NullLogger<SettingsStore>.Instance);
        LogLevel = new FakeLogLevelSwitch();
        SettingsSnapshots = new SettingsSnapshotProvider(SettingsFile, LogLevel);
        Session = new FakeSession(Hotkeys);

        Dispatcher = new IpcRequestDispatcher(
            Windows,
            Macros,
            Runs,
            Runner,
            Hotkeys,
            Lifetime,
            RunEvents,
            Log,
            Debug,
            SettingsFile,
            SettingsSnapshots,
            NullLogger<IpcRequestDispatcher>.Instance);
    }

    /// <summary>Уровень журнала без Serilog: у Core его нет, а провайдеру снимка нужен только шов.</summary>
    internal sealed class FakeLogLevelSwitch : ILogLevelSwitch
    {
        public LogLevelDto Current { get; set; } = LogLevelDto.Information;
    }

    /// <summary>
    /// Соединение, которого нет. Обработчикам, чьё состояние принадлежит клиенту, а не движку
    /// (подписки и приостановка хоткеев), сессия нужна обязательно — а поднимать ради них
    /// <see cref="IpcServer"/> с парой потоков в памяти стоит дороже, чем проверяется.
    ///
    /// Форвардит НАПРЯМУЮ, без учёта фронта: за фронт («одна аренда на соединение, снимается на
    /// разрыве») отвечает <c>IpcServer.ClientConnection</c>, и проверяется он там же — в
    /// <c>IpcServerTests</c>. Повторив здесь его логику, мы бы проверяли копию.
    /// </summary>
    internal sealed class FakeSession : IIpcSession
    {
        private readonly IHotkeyRegistration _hotkeys;

        public FakeSession(IHotkeyRegistration hotkeys) => _hotkeys = hotkeys;

        public bool WantsRunEvents { get; private set; }

        public void SetRunEventSubscription(bool enabled) => WantsRunEvents = enabled;

        public bool WantsLog { get; private set; }

        public void SetLogSubscription(bool enabled) => WantsLog = enabled;

        public Task SetHotkeySuspensionAsync(bool enabled, CancellationToken cancellationToken = default) =>
            enabled ? _hotkeys.SuspendAsync(cancellationToken) : _hotkeys.ResumeAsync(cancellationToken);
    }

    /// <summary>
    /// Настоящее хранилище настроек над временной папкой. Названо НЕ <c>Settings</c> намеренно:
    /// это имя в области видимости разрешается в пространство имён <c>SmartMacro.Settings</c>.
    /// </summary>
    public SettingsStore SettingsFile { get; }

    /// <summary>Рубильник уровня журнала — тот, что двигает <c>SetLogLevel</c>.</summary>
    public FakeLogLevelSwitch LogLevel { get; }

    /// <summary>Сборщик снимка: он же стоит за <c>GetSettings</c> и за пушем <c>SettingsChanged</c>.</summary>
    public SettingsSnapshotProvider SettingsSnapshots { get; }

    public WindowRegistry Windows { get; }

    public MacroRunRegistry Runs { get; }

    public MacroGraphStore Macros { get; }

    public IMacroRunner Runner { get; }

    public IHotkeyRegistration Hotkeys { get; }

    /// <summary>Сессия по умолчанию для обработчиков, которым соединение обязательно.</summary>
    public FakeSession Session { get; }

    public IHostApplicationLifetime Lifetime { get; }

    /// <summary>
    /// Кладёт макрос в папку — тем же путём, которым это делает панель, то есть файлом мимо
    /// демона. С волны F3 других путей нет: хранилище демона только читает.
    /// </summary>
    public void WriteMacro(MacroGraph graph, params (string Path, byte[] Bytes)[] templates)
    {
        var folder = MacroBundleFolder.In(_baseDirectory);
        Directory.CreateDirectory(folder);
        MacroBundleWriter.Write(MacroBundleFolder.PathFor(folder, graph.Name), new MacroBundleContent
        {
            Metadata = MacroBundleMetadata.CreateNew(graph.Name),
            Graph = graph,
            Templates = [.. templates.Select(t => new MacroBundleFile(t.Path, t.Bytes))],
        });
        Macros.Refresh();
    }

    /// <summary>Настоящий: именно из насоса событий прогона и отвечает <c>SubscribeRunEvents</c>.</summary>
    public RunEventPublisher RunEvents { get; }

    /// <summary>
    /// Настоящий: кольцо предыстории и подписка на ленту — это он, а обработчик
    /// <c>SubscribeLog</c> над ним совсем тонкий. Стока Serilog здесь нет и не нужно —
    /// тест кладёт записи прямо через <c>Append</c>, ровно как это делает сток в демоне.
    /// </summary>
    public LogEventPublisher Log { get; }

    /// <summary>
    /// Настоящая: точки останова и состояние пауз — обработчики отладчика над ней совсем тонкие.
    /// </summary>
    public MacroDebugSession Debug { get; }

    public IpcRequestDispatcher Dispatcher { get; }

    /// <summary>Папка, внутри которой хранилище держит <c>macros/</c> и <c>settings.json</c>.</summary>
    public string BaseDirectory => _baseDirectory;

    /// <summary>Путь к файлу, который занял бы макрос с таким именем.</summary>
    public string MacroFile(string name) =>
        MacroBundleFolder.PathFor(MacroBundleFolder.In(_baseDirectory), name);

    public Task<IpcResponse> DispatchAsync(string type, object? payload = null, int id = 1,
        IIpcSession? session = null) =>
        Dispatcher.DispatchAsync(new IpcRequest(id, type, payload is null ? null : IpcJson.Write(payload)), session);

    public void Dispose()
    {
        RunEvents.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SettingsSnapshots.Detach();
        SettingsFile.Dispose();
        Macros.Dispose();
        Runs.Dispose();
        try
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Наблюдатель хранилища может ещё мгновение держать папку; уборка временных файлов —
            // не то, что проверяет хоть один из этих тестов.
        }
    }
}
