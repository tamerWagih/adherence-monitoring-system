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

        _processMonitor.Start(stoppingToken);

        // NOTE: Idle, lock/unlock, and break alerts are owned by the tray app (interactive session),
        // to avoid Session 0 / WTS inconsistencies and duplicated events.
        // NOTE: Login/Logoff is also owned by the tray app via session switch events to avoid duplicates.
        _logger.LogInformation("Service monitors started: ProcessMonitor. Idle/login-logoff/lock-unlock/break alerts are handled by the tray app.");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping event capture service.");
        _processMonitor.Stop();
        
        return base.StopAsync(cancellationToken);
    }
}

