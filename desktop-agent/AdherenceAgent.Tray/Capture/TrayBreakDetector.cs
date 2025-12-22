using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Tray.Capture;

/// <summary>
/// Detects break periods based on idle time and scheduled break windows.
/// Creates BREAK_START and BREAK_END events when breaks are detected.
/// Runs in tray (interactive session) because it depends on reliable idle detection.
/// </summary>
public class TrayBreakDetector
{
    private readonly ILogger<TrayBreakDetector> _logger;
    private readonly IEventBuffer _buffer;
    private readonly BreakScheduleCache _breakScheduleCache;
    private bool _isOnBreak;
    private DateTime? _breakStartUtc;
    private string? _currentBreakScheduleId;

    // Minimum idle duration (in minutes) before considering it a break start
    private const int MinIdleMinutesForBreak = 2;

    public TrayBreakDetector(
        ILogger<TrayBreakDetector> logger,
        IEventBuffer buffer,
        BreakScheduleCache breakScheduleCache)
    {
        _logger = logger;
        _buffer = buffer;
        _breakScheduleCache = breakScheduleCache;
        _isOnBreak = false;
    }

    /// <summary>
    /// Check break status based on idle time and scheduled break windows.
    /// Called when idle state changes and periodically while idle.
    /// </summary>
    public async Task CheckBreakStatusAsync(bool isIdle, DateTime? idleStartUtc, double idleDurationMinutes)
    {
        try
        {
            var schedules = _breakScheduleCache.GetSchedules();
            _logger.LogDebug(
                "Break detection check: isIdle={IsIdle}, idleDurationMinutes={IdleMinutes}, schedulesCount={ScheduleCount}",
                isIdle, idleDurationMinutes, schedules.Count);

            if (schedules.Count == 0)
            {
                return;
            }

            var currentTime = DateTime.Now;
            var currentTimeOfDay = currentTime.TimeOfDay;

            // Check if we're currently in a scheduled break window
            var activeSchedule = FindActiveBreakSchedule(schedules, currentTimeOfDay);

            if (activeSchedule != null)
            {
                // We're in a scheduled break window
                if (isIdle && idleDurationMinutes >= MinIdleMinutesForBreak && !_isOnBreak)
                {
                    await StartBreakAsync(activeSchedule, idleStartUtc ?? DateTime.UtcNow);
                }
                else if (isIdle && idleDurationMinutes >= 0.5 && !_isOnBreak)
                {
                    // lenient: allow quick detection when break window starts
                    await StartBreakAsync(activeSchedule, idleStartUtc ?? DateTime.UtcNow);
                }
                else if (!isIdle && _isOnBreak)
                {
                    await EndBreakAsync(activeSchedule);
                }
            }
            else
            {
                // Not in a scheduled break window
                if (_isOnBreak)
                {
                    await EndBreakAsync(null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Break detection check failed");
        }
    }

    private BreakSchedule? FindActiveBreakSchedule(List<BreakSchedule> schedules, TimeSpan currentTime)
    {
        foreach (var schedule in schedules)
        {
            if (TryParseTime(schedule.StartTime, out var startTime) &&
                TryParseTime(schedule.EndTime, out var endTime))
            {
                if (currentTime >= startTime && currentTime <= endTime)
                {
                    return schedule;
                }
            }
        }

        return null;
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

    private async Task StartBreakAsync(BreakSchedule schedule, DateTime breakStartUtc)
    {
        _isOnBreak = true;
        _breakStartUtc = breakStartUtc;
        _currentBreakScheduleId = schedule.Id;

        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        await _buffer.AddAsync(new AdherenceEvent
        {
            EventType = EventTypes.BreakStart,
            EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
            NtAccount = ntAccount,
            Metadata = new Dictionary<string, object>
            {
                { "scheduled_break_id", schedule.Id },
                { "scheduled_start_time", schedule.StartTime },
                { "scheduled_end_time", schedule.EndTime },
                { "scheduled_duration_minutes", schedule.BreakDurationMinutes },
                { "is_alert", false },
            }
        }, CancellationToken.None);
    }

    private async Task EndBreakAsync(BreakSchedule? schedule)
    {
        if (!_isOnBreak)
        {
            return;
        }

        var breakEndUtc = DateTime.UtcNow;
        var breakDurationMinutes = _breakStartUtc.HasValue
            ? (int)(breakEndUtc - _breakStartUtc.Value).TotalMinutes
            : 0;

        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        var metadata = new Dictionary<string, object>
        {
            { "break_duration_minutes", breakDurationMinutes },
        };

        if (schedule != null)
        {
            metadata["scheduled_break_id"] = schedule.Id;
            metadata["scheduled_duration_minutes"] = schedule.BreakDurationMinutes;

            if (breakDurationMinutes > schedule.BreakDurationMinutes)
            {
                metadata["exceeded_minutes"] = breakDurationMinutes - schedule.BreakDurationMinutes;
            }
        }
        else if (_currentBreakScheduleId != null)
        {
            metadata["scheduled_break_id"] = _currentBreakScheduleId;
        }

        await _buffer.AddAsync(new AdherenceEvent
        {
            EventType = EventTypes.BreakEnd,
            EventTimestampUtc = breakEndUtc,
            NtAccount = ntAccount,
            Metadata = metadata
        }, CancellationToken.None);

        _isOnBreak = false;
        _breakStartUtc = null;
        _currentBreakScheduleId = null;
    }
}


