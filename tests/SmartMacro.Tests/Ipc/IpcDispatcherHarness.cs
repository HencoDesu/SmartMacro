using FakeItEasy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Hotkeys;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Storage;
using SmartMacro.Orchestration;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Ipc;

/// <summary>
/// Диспетчер, собранный с настоящим движком настолько, насколько его дёшево построить.
///
/// Настоящие: <see cref="WindowRegistry"/>, <see cref="MacroRunRegistry"/>,
/// <see cref="MacroGraphStore"/> над временной папкой, <see cref="CaptureDumpService"/> над
/// временной папкой. Это те части, ПОВЕДЕНИЕ которых обработчики и обязаны наружу выставлять,
/// так что, подделав их, мы проверяли бы разве что тот факт, что диспетчер зовёт методы, которые
/// мы ему и написали звать.
///
/// Подделаны: <see cref="IMacroRunner"/> и <see cref="IHotkeyRegistration"/> (настоящий
/// <see cref="Orchestrator"/> или <see cref="HotkeyListener"/> тянет за собой монитор процессов,
/// фабрику агентов и два монитора Win32), <see cref="IClassMatcher"/> и
/// <see cref="IHostApplicationLifetime"/> — модульный тест, который взаправду останавливает
/// хост, не нужен никому.
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
        Matcher = A.Fake<IClassMatcher>();
        Lifetime = A.Fake<IHostApplicationLifetime>();

        Captures = new CaptureDumpService(_baseDirectory, Windows, Matcher, NullLogger<CaptureDumpService>.Instance);
        Templates = new TemplateSetProvider(
            Path.Combine(_baseDirectory, "Assets"),
            NullLogger<TemplateSetProvider>.Instance);
        RunEvents = new RunEventPublisher(NullLogger<RunEventPublisher>.Instance);
        Log = new LogEventPublisher();
        Debug = new MacroDebugSession(NullLogger<MacroDebugSession>.Instance);

        Dispatcher = new IpcRequestDispatcher(
            Windows,
            Macros,
            Runs,
            Runner,
            Hotkeys,
            Captures,
            Templates,
            Lifetime,
            RunEvents,
            Log,
            Debug,
            NullLogger<IpcRequestDispatcher>.Instance);
    }

    public WindowRegistry Windows { get; }

    public MacroRunRegistry Runs { get; }

    public MacroGraphStore Macros { get; }

    public IMacroRunner Runner { get; }

    public IHotkeyRegistration Hotkeys { get; }

    public IClassMatcher Matcher { get; }

    public IHostApplicationLifetime Lifetime { get; }

    public CaptureDumpService Captures { get; }

    /// <summary>
    /// Настоящий, над пустой временной папкой <c>Assets/templates</c>: обработчики шаблонов —
    /// это тонкий слой над ним, и подделав его, мы проверяли бы только собственный маппер.
    /// </summary>
    public TemplateSetProvider Templates { get; }

    /// <summary>Кладёт файл в дерево шаблонов. <paramref name="set"/> = <c>null</c> — корень.</summary>
    public void WriteTemplate(string? set, string name, byte[] bytes)
    {
        var directory = set is null
            ? Templates.TemplatesRoot
            : Path.Combine(Templates.TemplatesRoot, set);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), bytes);
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

    /// <summary>
    /// Папка, внутри которой хранилище пишет <c>macros/</c>, а служба дампов — <c>debug/</c>.
    /// </summary>
    public string BaseDirectory => _baseDirectory;

    /// <summary>Путь к файлу, который занял бы макрос с таким именем.</summary>
    public string MacroFile(string name) => Path.Combine(_baseDirectory, MacroGraphStore.FolderName, $"{name}.json");

    public Task<IpcResponse> DispatchAsync(string type, object? payload = null, int id = 1,
        IIpcSession? session = null) =>
        Dispatcher.DispatchAsync(new IpcRequest(id, type, payload is null ? null : IpcJson.Write(payload)), session);

    public void Dispose()
    {
        RunEvents.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
