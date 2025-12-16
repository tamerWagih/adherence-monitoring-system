using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Monitors break duration and alerts when breaks exceed scheduled limits.
/// Can be extended with UI notifications (toast, tray icon) in the future.
/// </summary>
public class BreakTimerService : BackgroundService
{
    private readonly ILogger<BreakTimerService> _logger;
    private readonly BreakDetector _breakDetector;
    private readonly TimeSpan _checkInterval;

    public BreakTimerService(
        ILogger<BreakTimerService> logger,
        BreakDetector breakDetector)
    {
        _logger = logger;
        _breakDetector = breakDetector;
        _checkInterval = TimeSpan.FromSeconds(30); // Check every 30 seconds
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var breakStatus = _breakDetector.GetCurrentBreakStatus();
                if (breakStatus.IsOnBreak)
                {
                    // Log break duration periodically
                    _logger.LogInformation(
                        "Break in progress: {DurationMinutes} minutes (Started: {StartTime})",
                        breakStatus.BreakDurationMinutes,
                        breakStatus.BreakStartUtc.ToLocalTime().ToString("HH:mm:ss"));

                    // TODO: In future, can add:
                    // - Windows toast notifications
                    // - Tray icon updates
                    // - Sound alerts when break exceeds limit
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Break timer service error");
                await Task.Delay(_checkInterval, stoppingToken);
            }
        }
    }
}
