using AdherenceAgent.Service;
using AdherenceAgent.Service.Capture;
using AdherenceAgent.Service.Sync;
using AdherenceAgent.Service.Tray;
using AdherenceAgent.Service.Upload;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Security;
using AdherenceAgent.Shared.Storage;
using Serilog;
using System.Security.Principal;

// One-time credential seeding helper: dotnet run --project ... -- --set-creds <workstationId> <apiKey>
if (args.Length >= 3 && args[0] == "--set-creds")
{
    var workstationId = args[1];
    var apiKey = args[2];
    try
    {
        var store = new CredentialStore();
        store.Save(workstationId, apiKey);
        Console.WriteLine("Saved workstation credentials to %ProgramData%\\AdherenceAgent\\creds.bin");
        return;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to save credentials: {ex.Message}");
        Environment.Exit(1);
    }
}

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseSerilog((context, services, loggerConfiguration) =>
    {
        var agentConfig = context.Configuration.GetSection("Agent").Get<AgentConfig>() ?? new AgentConfig();
        var enableEventLog = context.Configuration.GetValue<bool?>("Agent:EnableEventLog") ?? false;
        var isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        PathProvider.EnsureDirectories();

        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(PathProvider.LogsDirectory, "agent.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .Enrich.WithProperty("AgentVersion", agentConfig.AgentVersion)
            .Enrich.WithProperty("Workstation", agentConfig.WorkstationName);

        if (enableEventLog && isElevated)
        {
            loggerConfiguration.WriteTo.EventLog(
                "AdherenceAgent",
                manageEventSource: true,
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning);
        }
    })
    .ConfigureServices((context, services) =>
    {
        var agentConfig = context.Configuration.GetSection("Agent").Get<AgentConfig>() ?? new AgentConfig();
        services.AddSingleton(agentConfig);
        services.AddSingleton<IEventBuffer, SQLiteEventBuffer>();
        services.AddSingleton<ClassificationCache>(provider =>
            new ClassificationCache(provider.GetService<ILogger<ClassificationCache>>()));
        services.AddSingleton<BreakScheduleCache>(provider =>
            new BreakScheduleCache(provider.GetService<ILogger<BreakScheduleCache>>()));
        services.AddSingleton<BreakDetector>();
        services.AddSingleton<BreakAlertMonitor>(provider => new BreakAlertMonitor(
            provider.GetRequiredService<ILogger<BreakAlertMonitor>>(),
            provider.GetRequiredService<IEventBuffer>(),
            provider.GetRequiredService<BreakScheduleCache>()));
        services.AddSingleton<LoginLogoffMonitor>();
        services.AddSingleton(provider => new IdleMonitor(
            provider.GetRequiredService<ILogger<IdleMonitor>>(),
            provider.GetRequiredService<IEventBuffer>(),
            agentConfig.IdleCheckIntervalSeconds,
            agentConfig.IdleThresholdMinutes,
            agentConfig.IdleThresholdSeconds,
            provider.GetRequiredService<BreakDetector>()));
        services.AddSingleton<SessionSwitchMonitor>();
        services.AddSingleton<ProcessMonitor>(provider => new ProcessMonitor(
            provider.GetRequiredService<ILogger<ProcessMonitor>>(),
            provider.GetRequiredService<IEventBuffer>()));
        services.AddSingleton<CredentialStore>();
        services.AddHttpClient("adherence");
        services.AddHostedService<Worker>(); // Start Worker first to initialize database
        services.AddHostedService<EventCaptureService>();
        services.AddHostedService<UploadService>();
        services.AddHostedService<ConfigSyncService>();
        services.AddHostedService<BreakTimerService>();
        // Keep tray alive in the interactive session so we don't lose interactive capture if user kills it.
        services.AddHostedService<TrayWatchdogService>();
    })
    .Build();

await host.RunAsync();
