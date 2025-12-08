namespace AdherenceAgent.Shared.Configuration;

/// <summary>
/// Agent runtime configuration loaded from appsettings/config.json + environment overrides.
/// </summary>
public class AgentConfig
{
    public string ApiEndpoint { get; set; } = "https://adherence-api.local/api";
    public int SyncIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
    public int IdleThresholdMinutes { get; set; } = 5;
    public int IdleThresholdSeconds { get; set; } = 0; // optional override for testing
    public int WindowCheckIntervalSeconds { get; set; } = 5;
    public int IdleCheckIntervalSeconds { get; set; } = 30;
    public int MaxRetryAttempts { get; set; } = 3;
    public int MaxBufferSize { get; set; } = 10_000;
    public string WorkstationName { get; set; } = Environment.MachineName;
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string AgentVersion { get; set; } = "0.1.0";
    public int CleanupSentDays { get; set; } = 7;
}

