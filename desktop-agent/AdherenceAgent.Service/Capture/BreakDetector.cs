using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Detects break periods based on idle time and scheduled break windows.
/// Creates BREAK_START and BREAK_END events when breaks are detected.
/// </summary>
public class BreakDetector
{
    private readonly ILogger<BreakDetector> _logger;
    private readonly IEventBuffer _buffer;
    private readonly BreakScheduleCache _breakScheduleCache;
    private bool _isOnBreak;
    private DateTime? _breakStartUtc;
    private string? _currentBreakScheduleId;

    // Minimum idle duration (in minutes) before considering it a break start
    private const int MinIdleMinutesForBreak = 2;

    public BreakDetector(
        ILogger<BreakDetector> logger,
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
    /// Called when idle state changes.
    /// </summary>
    /// <param name="isIdle">Whether the user is currently idle</param>
    /// <param name="idleStartUtc">When idle period started (UTC)</param>
    /// <param name="idleDurationMinutes">Duration of idle period in minutes</param>
    public async Task CheckBreakStatusAsync(bool isIdle, DateTime? idleStartUtc, double idleDurationMinutes)
    {
        try
        {
            var schedules = _breakScheduleCache.GetSchedules();
            if (schedules.Count == 0)
            {
                // No break schedules configured - nothing to detect
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
                    // Break started: user is idle during break window
                    await StartBreakAsync(activeSchedule, idleStartUtc ?? DateTime.UtcNow);
                }
                else if (!isIdle && _isOnBreak)
                {
                    // Break ended: user resumed activity during break window
                    await EndBreakAsync(activeSchedule);
                }
            }
            else
            {
                // Not in a scheduled break window
                if (_isOnBreak)
                {
                    // Break window ended - end the break
                    await EndBreakAsync(null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Break detection check failed");
        }
    }

    /// <summary>
    /// Find the active break schedule for the current time of day.
    /// </summary>
    private BreakSchedule? FindActiveBreakSchedule(List<BreakSchedule> schedules, TimeSpan currentTime)
    {
        foreach (var schedule in schedules)
        {
            if (TryParseTime(schedule.StartTime, out var startTime) &&
                TryParseTime(schedule.EndTime, out var endTime))
            {
                // Check if current time is within break window
                if (currentTime >= startTime && currentTime <= endTime)
                {
                    return schedule;
                }
            }
        }

        return null;
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

    /// <summary>
    /// Start a break period.
    /// </summary>
    private async Task StartBreakAsync(BreakSchedule schedule, DateTime breakStartUtc)
    {
        _isOnBreak = true;
        _breakStartUtc = breakStartUtc;
        _currentBreakScheduleId = schedule.Id;

        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        await _buffer.AddAsync(new AdherenceEvent
        {
            EventType = EventTypes.BreakStart,
            EventTimestampUtc = DateTime.UtcNow,
            NtAccount = ntAccount,
            Metadata = new Dictionary<string, object>
            {
                { "scheduled_break_id", schedule.Id },
                { "scheduled_start_time", schedule.StartTime },
                { "scheduled_end_time", schedule.EndTime },
                { "scheduled_duration_minutes", schedule.BreakDurationMinutes },
            }
        }, CancellationToken.None);

        _logger.LogInformation(
            "Break started: Schedule {ScheduleId} ({StartTime}-{EndTime}, {Duration} min)",
            schedule.Id, schedule.StartTime, schedule.EndTime, schedule.BreakDurationMinutes);
    }

    /// <summary>
    /// End a break period.
    /// </summary>
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

            // Check if break exceeded scheduled duration
            if (breakDurationMinutes > schedule.BreakDurationMinutes)
            {
                var exceededMinutes = breakDurationMinutes - schedule.BreakDurationMinutes;
                metadata["exceeded_minutes"] = exceededMinutes;
                _logger.LogWarning(
                    "Break exceeded scheduled duration by {ExceededMinutes} minutes (Schedule {ScheduleId})",
                    exceededMinutes, schedule.Id);
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

        _logger.LogInformation(
            "Break ended: Duration {DurationMinutes} minutes",
            breakDurationMinutes);

        _isOnBreak = false;
        _breakStartUtc = null;
        _currentBreakScheduleId = null;
    }

    /// <summary>
    /// Get current break status (for break timer display).
    /// </summary>
    public BreakStatus GetCurrentBreakStatus()
    {
        if (!_isOnBreak || !_breakStartUtc.HasValue)
        {
            return new BreakStatus { IsOnBreak = false };
        }

        var duration = DateTime.UtcNow - _breakStartUtc.Value;
        return new BreakStatus
        {
            IsOnBreak = true,
            BreakStartUtc = _breakStartUtc.Value,
            BreakDurationMinutes = (int)duration.TotalMinutes,
            ScheduledBreakId = _currentBreakScheduleId,
        };
    }

    /// <summary>
    /// Current break status for display purposes.
    /// </summary>
    public class BreakStatus
    {
        public bool IsOnBreak { get; set; }
        public DateTime BreakStartUtc { get; set; }
        public int BreakDurationMinutes { get; set; }
        public string? ScheduledBreakId { get; set; }
    }
}
