using AdherenceAgent.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace AdherenceAgent.Shared.Logging;

public static class LoggerBuilder
{
    public static ILogger CreateLogger(IConfiguration configuration, AgentConfig agentConfig)
    {
        PathProvider.EnsureDirectories();

        var logPath = Path.Combine(PathProvider.LogsDirectory, "agent.log");

        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration, new Serilog.Settings.Configuration.ConfigurationReaderOptions
            {
                SectionName = "Serilog"
            })
            .MinimumLevel.Information()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .WriteTo.EventLog(
                "AdherenceAgent",
                manageEventSource: true,
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning)
            .Enrich.WithProperty("AgentVersion", agentConfig.AgentVersion)
            .Enrich.WithProperty("Workstation", agentConfig.WorkstationName)
            .CreateLogger();
    }
}

