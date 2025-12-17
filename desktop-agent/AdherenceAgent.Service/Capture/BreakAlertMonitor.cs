using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Monitors break schedules and creates alert events when break windows start/end.
/// This is separate from break detection - alerts notify users about scheduled breaks,
/// while BreakDetector creates BREAK_START/BREAK_END events based on idle detection.
/// </summary>
public class BreakAlertMonitor
{
    private readonly ILogger<BreakAlertMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly BreakScheduleCache _breakScheduleCache;
    private Timer? _timer;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(1); // Check every minute
    private HashSet<string> _notifiedBreakIds = new(); // Track which break windows we've already notified

    public BreakAlertMonitor(
        ILogger<BreakAlertMonitor> logger,
        IEventBuffer buffer,
        BreakScheduleCache breakScheduleCache)
    {
        _logger = logger;
        _buffer = buffer;
        _breakScheduleCache = breakScheduleCache;
    }

    public void Start(CancellationToken token)
    {
        _timer = new Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        _logger.LogInformation("Break alert monitoring started (interval {Interval}s)", _pollInterval.TotalSeconds);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _logger.LogInformation("Break alert monitoring stopped");
    }

    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            var schedules = _breakScheduleCache.GetSchedules();
            if (schedules.Count == 0)
            {
                return;
            }

            var currentTime = DateTime.Now;
            var currentTimeOfDay = currentTime.TimeOfDay;

            // Check each schedule to see if we've entered a break window
            foreach (var schedule in schedules)
            {
                if (TryParseTime(schedule.StartTime, out var startTime) &&
                    TryParseTime(schedule.EndTime, out var endTime))
                {
                    // Check if we're currently in this break window
                    if (currentTimeOfDay >= startTime && currentTimeOfDay <= endTime)
                    {
                        // Check if we've already notified for this break window today
                        var breakKey = $"{schedule.Id}_{currentTime.Date:yyyy-MM-dd}";
                        if (!_notifiedBreakIds.Contains(breakKey))
                        {
                            // We've just entered this break window - send alert
                            await SendBreakWindowAlertAsync(schedule, true);
                            _notifiedBreakIds.Add(breakKey);
                            _logger.LogInformation(
                                "Break window alert sent: {ScheduleId} ({StartTime}-{EndTime}, {Duration}min)",
                                schedule.Id, schedule.StartTime, schedule.EndTime, schedule.BreakDurationMinutes);
                        }
                    }
                    else if (currentTimeOfDay > endTime)
                    {
                        // We've passed the end time - remove from notified set (for next day)
                        var breakKey = $"{schedule.Id}_{currentTime.Date:yyyy-MM-dd}";
                        if (_notifiedBreakIds.Contains(breakKey))
                        {
                            // Break window ended - send end alert
                            await SendBreakWindowAlertAsync(schedule, false);
                            _notifiedBreakIds.Remove(breakKey);
                            _logger.LogDebug("Break window ended: {ScheduleId}", schedule.Id);
                        }
                    }
                }
            }

            // Clean up old entries (from previous days)
            var today = currentTime.Date;
            _notifiedBreakIds.RemoveWhere(key => !key.EndsWith(today.ToString("yyyy-MM-dd")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Break alert monitor tick failed");
        }
    }

    /// <summary>
    /// Send a break window alert (start or end).
    /// This creates an event that the tray app can use to show notifications.
    /// </summary>
    private async Task SendBreakWindowAlertAsync(BreakSchedule schedule, bool isStart)
    {
        var ntAccount = AdherenceAgent.Shared.Helpers.WindowsIdentityHelper.GetCurrentNtAccount();
        
        // Create a special event type for break window alerts (not BREAK_START/BREAK_END)
        // We'll use BREAK_START/BREAK_END but with a flag indicating it's just an alert
        // Actually, let's create a separate event or use metadata to distinguish
        
        // For now, we'll create a BREAK_START event with metadata indicating it's an alert
        // The tray app will show notifications for these events
        var eventType = isStart ? EventTypes.BreakStart : EventTypes.BreakEnd;
        
        await _buffer.AddAsync(new AdherenceEvent
        {
            EventType = eventType,
            EventTimestampUtc = DateTime.UtcNow,
            NtAccount = ntAccount,
            Metadata = new Dictionary<string, object>
            {
                { "scheduled_break_id", schedule.Id },
                { "scheduled_start_time", schedule.StartTime },
                { "scheduled_end_time", schedule.EndTime },
                { "scheduled_duration_minutes", schedule.BreakDurationMinutes },
                { "is_alert", true }, // Flag to indicate this is an alert, not actual break detection
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Parse time string (HH:mm:ss or HH:mm) to TimeSpan.
    /// </summary>
    private bool TryParseTime(string timeStr, out TimeSpan timeSpan)
    {
        timeSpan = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(timeStr))
        {
            return false;
        }

        var parts = timeStr.Split(':');
        if (parts.Length < 2)
        {
            return false;
        }

        if (int.TryParse(parts[0], out var hours) &&
            int.TryParse(parts[1], out var minutes))
        {
            var seconds = parts.Length > 2 && int.TryParse(parts[2], out var s) ? s : 0;
            timeSpan = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        return false;
    }
}
