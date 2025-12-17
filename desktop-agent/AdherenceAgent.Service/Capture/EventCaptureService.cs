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
    private readonly IEventBuffer _buffer;
    private readonly LoginLogoffMonitor _loginMonitor;
    private readonly IdleMonitor _idleMonitor;
    private readonly SessionSwitchMonitor _sessionSwitchMonitor;
    private readonly ProcessMonitor _processMonitor;
    private readonly BreakAlertMonitor _breakAlertMonitor;

    public EventCaptureService(
        ILogger<EventCaptureService> logger,
        IEventBuffer buffer,
        LoginLogoffMonitor loginMonitor,
        IdleMonitor idleMonitor,
        SessionSwitchMonitor sessionSwitchMonitor,
        ProcessMonitor processMonitor,
        BreakAlertMonitor breakAlertMonitor)
    {
        _logger = logger;
        _buffer = buffer;
        _loginMonitor = loginMonitor;
        _idleMonitor = idleMonitor;
        _sessionSwitchMonitor = sessionSwitchMonitor;
        _processMonitor = processMonitor;
        _breakAlertMonitor = breakAlertMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for database initialization before starting monitors
        _logger.LogInformation("Waiting for database initialization before starting event capture...");
        try
        {
            await _buffer.InitializeAsync(stoppingToken);
            _logger.LogInformation("Database initialized, starting event capture service.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database, event capture will not start.");
            throw;
        }

        _loginMonitor.Start(stoppingToken);
        _idleMonitor.Start(stoppingToken);
        _sessionSwitchMonitor.Start(stoppingToken);
        _processMonitor.Start(stoppingToken);
        _breakAlertMonitor.Start(stoppingToken);
        
        _logger.LogInformation("Service monitors started: LoginLogoff, Idle, SessionSwitch, ProcessMonitor, BreakAlertMonitor. Interactive capture is handled by the tray app.");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping event capture service.");
        _loginMonitor.Stop();
        _idleMonitor.Stop();
        _sessionSwitchMonitor.Stop();
        _processMonitor.Stop();
        _breakAlertMonitor.Stop();
        
        return base.StopAsync(cancellationToken);
    }
}

