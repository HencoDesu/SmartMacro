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
/// A dispatcher wired to as much of the real engine as is cheap to build.
///
/// Real: <see cref="WindowRegistry"/>, <see cref="MacroRunRegistry"/>,
/// <see cref="MacroGraphStore"/> over a temp folder, <see cref="CaptureDumpService"/> over
/// a temp folder. These are the components whose BEHAVIOUR the handlers are supposed to
/// expose, so faking them would only test that the dispatcher calls the methods we wrote
/// it to call.
///
/// Faked: <see cref="IMacroRunner"/> and <see cref="IHotkeyRegistration"/> (a real
/// <see cref="Orchestrator"/> or <see cref="HotkeyListener"/> drags in the process
/// monitor, the agent factory and two Win32 monitors), <see cref="IClassMatcher"/>, and
/// <see cref="IHostApplicationLifetime"/> — nobody wants a unit test that actually stops
/// a host.
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
        Macros = new MacroGraphStore(_baseDirectory, NullLogger<MacroGraphStore>.Instance, seedDefaults: false);

        Runner = A.Fake<IMacroRunner>();
        Hotkeys = A.Fake<IHotkeyRegistration>();
        Matcher = A.Fake<IClassMatcher>();
        Lifetime = A.Fake<IHostApplicationLifetime>();

        Captures = new CaptureDumpService(_baseDirectory, Windows, Matcher, NullLogger<CaptureDumpService>.Instance);
        RunEvents = new RunEventPublisher(NullLogger<RunEventPublisher>.Instance);

        Dispatcher = new IpcRequestDispatcher(
            Windows,
            Macros,
            Runs,
            Runner,
            Hotkeys,
            Captures,
            Lifetime,
            RunEvents,
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

    /// <summary>Real: the run-event pump is what <c>SubscribeRunEvents</c> answers from.</summary>
    public RunEventPublisher RunEvents { get; }

    public IpcRequestDispatcher Dispatcher { get; }

    /// <summary>Folder the store writes <c>macros/</c> and the dump service writes <c>debug/</c> under.</summary>
    public string BaseDirectory => _baseDirectory;

    /// <summary>Path of the file a macro of this name would occupy.</summary>
    public string MacroFile(string name) => Path.Combine(_baseDirectory, MacroGraphStore.FolderName, $"{name}.json");

    public Task<IpcResponse> DispatchAsync(string type, object? payload = null, int id = 1, IIpcSession? session = null) =>
        Dispatcher.DispatchAsync(new IpcRequest(id, type, payload is null ? null : IpcJson.Write(payload)), session);

    public void Dispose()
    {
        RunEvents.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Macros.Dispose();
        Runs.Dispose();
        try
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // The store's watcher may still hold the folder for a moment; temp cleanup is
            // not what any of these tests are asserting.
        }
    }
}
