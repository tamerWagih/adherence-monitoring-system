using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Tray.Capture;

/// <summary>
/// Tray-owned break window alerts (scheduled reminders).
/// Emits BREAK_START/BREAK_END with metadata is_alert=true; tray uses those to show notifications.
/// </summary>
public sealed class TrayBreakAlertMonitor
{
    private readonly ILogger<TrayBreakAlertMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly BreakScheduleCache _breakScheduleCache;
    private System.Threading.Timer? _timer;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(1);
    private HashSet<string> _notifiedBreakIds = new();

    public TrayBreakAlertMonitor(
        ILogger<TrayBreakAlertMonitor> logger,
        IEventBuffer buffer,
        BreakScheduleCache breakScheduleCache)
    {
        _logger = logger;
        _buffer = buffer;
        _breakScheduleCache = breakScheduleCache;
    }

    public void Start(CancellationToken token)
    {
        _timer = new System.Threading.Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        _logger.LogInformation("Tray break alert monitoring started (interval {Interval}s)", _pollInterval.TotalSeconds);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
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

            foreach (var schedule in schedules)
            {
                if (!TryParseTime(schedule.StartTime, out var startTime) ||
                    !TryParseTime(schedule.EndTime, out var endTime))
                {
                    continue;
                }

                if (currentTimeOfDay >= startTime && currentTimeOfDay <= endTime)
                {
                    var breakKey = $"{schedule.Id}_{currentTime.Date:yyyy-MM-dd}";
                    if (!_notifiedBreakIds.Contains(breakKey))
                    {
                        await SendBreakWindowAlertAsync(schedule, isStart: true);
                        _notifiedBreakIds.Add(breakKey);
                    }
                }
                else if (currentTimeOfDay > endTime)
                {
                    var breakKey = $"{schedule.Id}_{currentTime.Date:yyyy-MM-dd}";
                    if (_notifiedBreakIds.Contains(breakKey))
                    {
                        await SendBreakWindowAlertAsync(schedule, isStart: false);
                        _notifiedBreakIds.Remove(breakKey);
                    }
                }
            }

            // Clean up old entries
            var today = currentTime.Date;
            _notifiedBreakIds.RemoveWhere(key => !key.EndsWith(today.ToString("yyyy-MM-dd")));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tray break alert monitor tick failed");
        }
    }

    private async Task SendBreakWindowAlertAsync(BreakSchedule schedule, bool isStart)
    {
        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        var eventType = isStart ? EventTypes.BreakStart : EventTypes.BreakEnd;

        await _buffer.AddAsync(new AdherenceEvent
        {
            EventType = eventType,
            EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
            NtAccount = ntAccount,
            Metadata = new Dictionary<string, object>
            {
                { "scheduled_break_id", schedule.Id },
                { "scheduled_start_time", schedule.StartTime },
                { "scheduled_end_time", schedule.EndTime },
                { "scheduled_duration_minutes", schedule.BreakDurationMinutes },
                { "is_alert", true },
            }
        }, CancellationToken.None);
    }

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


