using Avalonia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartMacro.Agents;
using SmartMacro.Config;
using SmartMacro.GameWindows;
using SmartMacro.Hotkeys;
using SmartMacro.Input;
using SmartMacro.Native.Hotkey;
using SmartMacro.Orchestration;
using SmartMacro.Presentation;
using SmartMacro.ProcessMonitoring;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Storage;
using SmartMacro.Vision;
using SmartMacro.Windows;
using Serilog;

namespace SmartMacro.App;

// ⚠ STAGE 2A TRANSITIONAL HAZARD — DO NOT RUN THIS AND SmartMacro.Daemon AT THE SAME TIME.
//
// Stage 2A gave the engine a second, headless home (SmartMacro.Daemon) but deliberately left
// this composition root intact, because the view-models still call Core services
// (WindowRegistry, MacroGraphStore, Orchestrator, HotkeyListener) directly and would break
// the moment it went away. The result is two executables that can each host a full engine:
//
//   * RegisterHotKey is process-global — whichever starts second silently fails to bind and
//     its macros never fire, while the first keeps working. Confusing to debug.
//   * WH_MOUSE_LL hooks stack: a mouse chord fires the macro in BOTH processes.
//   * Two ProcessMonitors spawn two agents per game window, two boot macros race on the same
//     client, and input gets dispatched twice.
//
// Stage 3 deletes everything below and replaces it with an IPC client. Until then: run the
// daemon OR the app, never both.
internal static class Program
{
    public static IServiceProvider? Services { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var bootstrapConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "SMARTMACRO_")
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(bootstrapConfiguration)
            .CreateBootstrapLogger();

        try
        {
            Log.Information("SmartMacro starting");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddConfiguration(bootstrapConfiguration);

            builder.Services.AddSerilog((sp, lc) => lc
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(sp)
                .Enrich.FromLogContext());

            ConfigureServices(builder.Services, builder.Configuration);

            using var host = builder.Build();
            Services = host.Services;

            Log.Information("Composition root built, starting hosted services");
            host.StartAsync().GetAwaiter().GetResult();
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                Log.Information("Avalonia exited, stopping hosted services");
                host.StopAsync().GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "SmartMacro terminated unexpectedly");
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
        // wake-up) so neither DI nor the orchestrator has to know about Native types. Swap
        // the factory implementation once we know what the live client likes.
        // Win32NativeWindowSystem is a static class — no DI registration needed.
        services.AddSingleton<IGameWindowFactory, GameWindowFactory>();

        // Sole owner of window tags AND the hwnd → IGameWindow lookup — everything
        // (agents, macro primitives, UI) reads window state from here.
        services.AddSingleton<WindowRegistry>();

        services.AddSingleton<IClassMatcher, ClassMatcher>();
        // Stage 2B moved the "Dump captures" sweep out of MainWindow into Core; the button
        // resolves it from here. After stage 3 only the daemon registers it.
        services.AddSingleton<CaptureDumpService>();
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

        // UI seams. The view-models take these as interfaces so they can be exercised
        // headlessly (and so stage 3 can swap in IPC-backed implementations without
        // touching a single VM): thread marshalling, "start this macro", and the hotkey
        // suspend/resume the chord picker depends on.
        services.AddSingleton<IUiDispatcher>(AvaloniaUiDispatcher.Instance);
        services.AddSingleton<IMacroLauncher, OrchestratorMacroLauncher>();
        services.AddSingleton<IHotkeySuspension, HotkeyListenerSuspension>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }

    // Used by the Avalonia previewer/designer; must be parameterless and named exactly this way.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
