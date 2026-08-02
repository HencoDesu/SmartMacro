using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SmartMacro.Agents;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.Hotkeys;
using SmartMacro.Input;
using SmartMacro.Ipc;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Storage;
using SmartMacro.Native.Hotkey;
using SmartMacro.Native.Tray;
using SmartMacro.Orchestration;
using SmartMacro.Presentation;
using SmartMacro.ProcessMonitoring;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Daemon;

// The resident half of SmartMacro: a windowless WinExe that hosts the whole engine —
// process monitoring, the macro library, hotkey registration, vision, input dispatch — plus
// a Win32 tray icon. No Avalonia, no XAML, no render loop; the UI is a separate process the
// tray launches on demand and the user can close again without stopping automation.
//
// This is the ONLY process that hosts an engine. Stage 3 stripped SmartMacro.App down to an
// IPC client with no SmartMacro.Core reference at all, which retired the stage-2A hazard of
// two composition roots fighting over RegisterHotKey, the mouse hook and the game windows.
//
// The control endpoint is the whole interface: IpcServer listens on the named pipe
// "smartmacro-control" (JSON Lines, multi-client) and exposes the engine to the panel —
// window/tag snapshots and pushes, macro CRUD and runs, hotkey suspend/resume, capture
// dumps, activate-the-panel, shutdown. Anything the UI needs is a message type here, not a
// second copy of the engine there.
internal static class Program
{
    public static int Main(string[] args)
    {
        // Claim single-instance BEFORE anything expensive: a second daemon would double every
        // global side effect this process has (hotkeys, hooks, input).
        using var instance = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.DaemonMutexName);

        var bootstrapConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "SMARTMACRO_")
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(bootstrapConfiguration)
            .CreateBootstrapLogger();

        if (instance is null)
        {
            Log.Information(
                "SmartMacro daemon already running (mutex {Mutex} held) — exiting",
                SingleInstanceGuard.DaemonMutexName);
            Log.CloseAndFlush();
            return 0;
        }

        try
        {
            Log.Information("SmartMacro daemon starting");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddConfiguration(bootstrapConfiguration);

            builder.Services.AddSerilog((sp, lc) => lc
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(sp)
                .Enrich.FromLogContext());

            ConfigureServices(builder.Services, builder.Configuration);

            using var host = builder.Build();

            Log.Information("Composition root built, running host");
            host.RunAsync().GetAwaiter().GetResult();
            Log.Information("SmartMacro daemon stopped");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "SmartMacro daemon terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection("Agent"));
        // "ProcessProfiles" is a raw JSON array, so bind it into the wrapper's list.
        services.AddOptions<ProcessProfileOptions>()
            .Configure(options => configuration.GetSection(ProcessProfileOptions.SectionName).Bind(options.Profiles));
        services.Configure<CoordinateReaderOptions>(configuration.GetSection("Vision:CoordinateReader"));
        services.Configure<ClassMatcherOptions>(configuration.GetSection("Vision:ClassMatcher"));
        services.Configure<WindowVisionOptions>(configuration.GetSection("Vision:Window"));

        // GameWindowFactory bakes in the input-strategy choice (PostMessage + WM_ACTIVATEAPP
        // wake-up) so neither DI nor the orchestrator has to know about Native types.
        // Win32NativeWindowSystem is a static class — no DI registration needed.
        services.AddSingleton<IGameWindowFactory, GameWindowFactory>();

        // Sole owner of window tags AND the hwnd → IGameWindow lookup — everything
        // (agents, macro primitives, and from stage 2B the IPC layer) reads window state
        // from here.
        services.AddSingleton<WindowRegistry>();

        services.AddSingleton<IClassMatcher, ClassMatcher>();
        services.AddSingleton<TemplateSetProvider>();
        services.AddSingleton<ICoordinateReader, TesseractCoordinateReader>();
        services.AddSingleton<WindowIconService>();
        services.AddSingleton<AgentInputDispatcher>();
        services.AddSingleton<CursorPositionProvider>();
        services.AddSingleton<ICharacterAgentFactory, CharacterAgentFactory>();
        services.AddSingleton<Win32HotkeyMonitor>();
        services.AddSingleton<Win32MouseHookMonitor>();

        // Macro engine. The store is the library of record: it resolves sub-macros for
        // RunMacroNode, supplies HotkeyListener's bindings, and tells the orchestrator
        // which graphs a new process should boot. On first run it migrates a legacy
        // macros.json and/or seeds the PW example set.
        services.AddSingleton<MacroGraphStore>();
        services.AddSingleton<IMacroGraphResolver>(sp => sp.GetRequiredService<MacroGraphStore>());
        services.AddSingleton<IMacroPrimitives, MacroPrimitives>();
        services.AddSingleton<MacroExecutor>();
        services.AddSingleton<MacroRunRegistry>();

        // ProcessMonitor and HotkeyListener are registered first because Orchestrator
        // subscribes to their events during construction. DI resolves them before
        // Orchestrator regardless of registration order, but listing them first reads
        // naturally.
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());

        // Hotkey bindings come from the macro library itself (each graph's HotkeyTriggers),
        // so the listener re-registers whenever the library changes. No hotkeys.json.
        services.AddSingleton<HotkeyListener>();
        services.AddHostedService(sp => sp.GetRequiredService<HotkeyListener>());

        // Single Orchestrator instance, also drives the dispatch-loop lifecycle via IHostedService.
        services.AddSingleton<Orchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<Orchestrator>());

        // --- IPC (stage 2B) ----------------------------------------------------------
        //
        // The two narrow seams the dispatcher needs. Both resolve to the singletons above:
        // the interfaces exist so the request handlers can be unit-tested without a live
        // engine, not because there is a second implementation.
        services.AddSingleton<IMacroRunner>(sp => sp.GetRequiredService<Orchestrator>());
        services.AddSingleton<IHotkeyRegistration>(sp => sp.GetRequiredService<HotkeyListener>());
        services.AddSingleton<CaptureDumpService>();
        services.AddSingleton<IpcRequestDispatcher>();
        services.AddSingleton<IpcServer>();
        // Event fan-out as a capability, for the tray's "the panel is already up — bring it
        // forward" path. (The dispatcher gets the same object handed to it by the server's
        // constructor instead, because server → dispatcher → server would be a DI cycle.)
        services.AddSingleton<IIpcBroadcaster>(sp => sp.GetRequiredService<IpcServer>());

        // Registered AFTER the engine and BEFORE the tray, which pins both ends of its
        // lifetime: hosted services start in registration order, so the pipe only appears
        // once ProcessMonitor/HotkeyListener/Orchestrator are up and a client connecting the
        // instant it sees the pipe gets a live registry; they stop in REVERSE order, so the
        // pipe is torn down early — right after the tray icon, before agents and hotkeys
        // unwind — and the panel learns the daemon is going away instead of hanging on a
        // half-dead engine.
        services.AddHostedService(sp => sp.GetRequiredService<IpcServer>());

        // Tray last: hosted services stop in reverse registration order, so the icon is the
        // first thing to disappear when the user picks "Выход" — no stale icon hanging around
        // while agents and hotkeys drain.
        services.AddSingleton<Win32TrayIcon>();
        services.AddHostedService<TrayController>();
    }
}
