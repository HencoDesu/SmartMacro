using Avalonia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfectWorldAgent.Agents;
using PerfectWorldAgent.Config;
using PerfectWorldAgent.Core;
using PerfectWorldAgent.Native;
using PerfectWorldAgent.Native.Hotkey;
using PerfectWorldAgent.Orchestration;
using PerfectWorldAgent.App.ViewModels;
using PerfectWorldAgent.Vision;
using Serilog;

namespace PerfectWorldAgent.App;

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
            .AddEnvironmentVariables(prefix: "PWAGENT_")
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(bootstrapConfiguration)
            .CreateBootstrapLogger();

        try
        {
            Log.Information("PerfectWorldAgent starting");

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
            Log.Fatal(ex, "PerfectWorldAgent terminated unexpectedly");
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
        services.Configure<HotkeyOptions>(configuration.GetSection("Hotkeys"));
        services.Configure<ActivatingInputOptions>(configuration.GetSection("Input:Activating"));
        services.Configure<SendInputOptions>(configuration.GetSection("Input:SendInput"));
        services.Configure<CoordinateReaderOptions>(configuration.GetSection("Vision:CoordinateReader"));

        // GameWindowFactory bakes in the input-strategy choice (PostMessage + WM_ACTIVATEAPP
        // wake-up) so neither DI nor the orchestrator has to know about Native types. Swap
        // the factory implementation once we know what the live client likes.
        // Win32NativeWindowSystem is a static class — no DI registration needed.
        services.AddSingleton<IGameWindowFactory, GameWindowFactory>();
        services.AddSingleton<ICharacterRoster, JsonCharacterRoster>();
        services.AddSingleton<INameMatcher, NameMatcher>();
        services.AddSingleton<ICoordinateReader, TesseractCoordinateReader>();
        services.AddSingleton<ICharacterProvider, CharacterProvider>();
        services.AddSingleton<ICharacterAgentFactory, CharacterAgentFactory>();
        services.AddSingleton<Win32HotkeyMonitor>();
        services.AddSingleton<Win32MouseHookMonitor>();

        // ProcessMonitor and HotkeyListener are registered first because Orchestrator
        // subscribes to their events during construction. DI resolves them before
        // Orchestrator regardless of registration order, but listing them first reads
        // naturally.
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());

        // HotkeyConfigStore is the runtime source of truth for hotkey bindings — seeded
        // from HotkeyOptions defaults on first run (no hotkeys.json), persists user edits
        // from SettingsDialog, raises BindingsChanged so HotkeyListener can re-register.
        services.AddSingleton<HotkeyConfigStore>();

        services.AddSingleton<HotkeyListener>();
        services.AddHostedService(sp => sp.GetRequiredService<HotkeyListener>());

        // Single Orchestrator instance, also drives the dispatch-loop lifecycle via IHostedService.
        services.AddSingleton<Orchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<Orchestrator>());

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }

    // Used by the Avalonia previewer/designer; must be parameterless and named exactly this way.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
