using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;

namespace AdherenceAgent.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IEventBuffer _buffer;
    private readonly AgentConfig _config;

    public Worker(ILogger<Worker> logger, IEventBuffer buffer, AgentConfig config)
    {
        _logger = logger;
        _buffer = buffer;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _buffer.InitializeAsync(stoppingToken);
        _logger.LogInformation("Worker initialized; capture and buffer are active.");
        // Periodic cleanup for sent events
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _buffer.CleanupAsync(_config.CleanupSentDays, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup task failed");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
