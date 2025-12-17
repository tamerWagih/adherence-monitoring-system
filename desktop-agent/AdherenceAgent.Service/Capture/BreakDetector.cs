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
            _logger.LogInformation(
                "Break detection check: isIdle={IsIdle}, idleDurationMinutes={IdleMinutes}, schedulesCount={ScheduleCount}",
                isIdle, idleDurationMinutes, schedules.Count);

            if (schedules.Count == 0)
            {
                // No break schedules configured - nothing to detect
                _logger.LogWarning("No break schedules configured - skipping break detection. Check if break_schedules.json exists and contains schedules.");
                return;
            }

            // Log all schedules for debugging
            foreach (var s in schedules)
            {
                _logger.LogDebug("Cached schedule: {ScheduleId} ({StartTime}-{EndTime}, {Duration}min)", 
                    s.Id, s.StartTime, s.EndTime, s.BreakDurationMinutes);
            }

            var currentTime = DateTime.Now;
            var currentTimeOfDay = currentTime.TimeOfDay;

            _logger.LogDebug(
                "Current time: {CurrentTime}, TimeOfDay: {TimeOfDay}",
                currentTime.ToString("yyyy-MM-dd HH:mm:ss"), currentTimeOfDay);

            // Check if we're currently in a scheduled break window
            var activeSchedule = FindActiveBreakSchedule(schedules, currentTimeOfDay);

            if (activeSchedule != null)
            {
                _logger.LogInformation(
                    "Active break schedule found: {ScheduleId} ({StartTime}-{EndTime}), currentTime={CurrentTime}",
                    activeSchedule.Id, activeSchedule.StartTime, activeSchedule.EndTime, currentTimeOfDay);

                // We're in a scheduled break window
                // If user is idle (even briefly) during break window, detect break immediately
                // This allows break detection as soon as break window starts and user becomes idle
                if (isIdle && idleDurationMinutes >= MinIdleMinutesForBreak && !_isOnBreak)
                {
                    _logger.LogInformation(
                        "Break start conditions met: idle={IsIdle}, duration={Duration}min (min={Min}min), onBreak={OnBreak}",
                        isIdle, idleDurationMinutes, MinIdleMinutesForBreak, _isOnBreak);
                    // Break started: user is idle during break window
                    await StartBreakAsync(activeSchedule, idleStartUtc ?? DateTime.UtcNow);
                }
                else if (isIdle && idleDurationMinutes >= 0.5 && !_isOnBreak)
                {
                    // More lenient: if user is idle for at least 30 seconds during break window, detect break
                    // This allows immediate detection when break window starts
                    _logger.LogInformation(
                        "Break start detected (lenient): idle={IsIdle}, duration={Duration}min, onBreak={OnBreak}",
                        isIdle, idleDurationMinutes, _isOnBreak);
                    await StartBreakAsync(activeSchedule, idleStartUtc ?? DateTime.UtcNow);
                }
                else if (!isIdle && _isOnBreak)
                {
                    // Break ended: user resumed activity during break window
                    await EndBreakAsync(activeSchedule);
                }
                else
                {
                    _logger.LogInformation(
                        "Break window active but conditions not met: idle={IsIdle}, duration={Duration}min (need {Min}min), onBreak={OnBreak}",
                        isIdle, idleDurationMinutes, MinIdleMinutesForBreak, _isOnBreak);
                }
            }
            else
            {
                _logger.LogDebug("No active break schedule found for current time {TimeOfDay}", currentTimeOfDay);
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
        _logger.LogDebug("Checking {Count} schedules against current time {CurrentTime}", schedules.Count, currentTime);
        
        foreach (var schedule in schedules)
        {
            if (TryParseTime(schedule.StartTime, out var startTime) &&
                TryParseTime(schedule.EndTime, out var endTime))
            {
                _logger.LogDebug(
                    "Checking schedule {ScheduleId}: {StartTime}-{EndTime}, current={CurrentTime} (startOk={StartOk}, endOk={EndOk})",
                    schedule.Id, startTime, endTime, currentTime, 
                    currentTime >= startTime, currentTime <= endTime);
                
                // Check if current time is within break window
                if (currentTime >= startTime && currentTime <= endTime)
                {
                    _logger.LogInformation("Schedule {ScheduleId} matches current time {CurrentTime}", schedule.Id, currentTime);
                    return schedule;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Failed to parse schedule {ScheduleId} times: Start={StartTime}, End={EndTime}",
                    schedule.Id, schedule.StartTime, schedule.EndTime);
            }
        }

        _logger.LogDebug("No matching schedule found for current time {CurrentTime}", currentTime);
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
                { "is_alert", false }, // This is actual break detection based on idle, not just an alert
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
