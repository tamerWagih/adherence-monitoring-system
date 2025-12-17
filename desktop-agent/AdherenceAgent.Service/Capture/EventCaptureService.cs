using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Coordinates all event capture monitors and writes events to the buffer.
/// </summary>
public class EventCaptureService : BackgroundService
{
    private readonly ILogger<EventCaptureService> _logger;
    private readonly LoginLogoffMonitor _loginMonitor;
    private readonly IdleMonitor _idleMonitor;
    private readonly SessionSwitchMonitor _sessionSwitchMonitor;
    private readonly ActiveWindowMonitor _windowMonitor;
    private readonly TeamsMonitor? _teamsMonitor;
    private readonly BrowserTabMonitor? _browserTabMonitor;
    private readonly ProcessMonitor? _processMonitor;
    private readonly BreakAlertMonitor? _breakAlertMonitor;

    public EventCaptureService(
        ILogger<EventCaptureService> logger,
        LoginLogoffMonitor loginMonitor,
        IdleMonitor idleMonitor,
        SessionSwitchMonitor sessionSwitchMonitor,
        ActiveWindowMonitor windowMonitor,
        TeamsMonitor? teamsMonitor = null,
        BrowserTabMonitor? browserTabMonitor = null,
        ProcessMonitor? processMonitor = null,
        BreakAlertMonitor? breakAlertMonitor = null)
    {
        _logger = logger;
        _loginMonitor = loginMonitor;
        _idleMonitor = idleMonitor;
        _sessionSwitchMonitor = sessionSwitchMonitor;
        _windowMonitor = windowMonitor;
        _teamsMonitor = teamsMonitor;
        _browserTabMonitor = browserTabMonitor;
        _processMonitor = processMonitor;
        _breakAlertMonitor = breakAlertMonitor;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting event capture service.");
        _loginMonitor.Start(stoppingToken);
        _idleMonitor.Start(stoppingToken);
        _sessionSwitchMonitor.Start(stoppingToken);
        _windowMonitor.Start(stoppingToken);
        
        // Start Day 8 monitors if available
        _teamsMonitor?.Start(stoppingToken);
        _browserTabMonitor?.Start(stoppingToken);
        _processMonitor?.Start(stoppingToken);
        
        // Start break alert monitor (for scheduled break notifications)
        _breakAlertMonitor?.Start(stoppingToken);
        
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping event capture service.");
        _loginMonitor.Stop();
        _idleMonitor.Stop();
        _sessionSwitchMonitor.Stop();
        _windowMonitor.Stop();
        
        // Stop Day 8 monitors if available
        _teamsMonitor?.Stop();
        _browserTabMonitor?.Stop();
        _processMonitor?.Stop();
        
        // Stop break alert monitor
        _breakAlertMonitor?.Stop();
        
        return base.StopAsync(cancellationToken);
    }
}

