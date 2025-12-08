using Microsoft.Extensions.Configuration;

namespace AdherenceAgent.Shared.Configuration;

/// <summary>
/// Loads agent configuration from appsettings + ProgramData config.json with env overrides.
/// </summary>
public class ConfigLoader
{
    private readonly IConfiguration _configuration;

    public ConfigLoader(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public AgentConfig Load()
    {
        var config = new AgentConfig();
        _configuration.Bind("Agent", config);
        return config;
    }
}

