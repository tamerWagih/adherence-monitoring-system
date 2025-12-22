using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Tray.Capture;

/// <summary>
/// Tray-owned idle detection (runs inside the interactive user session).
/// Uses GetLastInputInfo, which is reliable in the tray but not from a Windows Service (Session 0).
/// </summary>
public sealed class TrayIdleMonitor
{
    private readonly ILogger<TrayIdleMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly TrayBreakDetector? _breakDetector;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _idleThreshold;

    private System.Threading.Timer? _timer;
    private bool _isIdle;
    private DateTime? _idleStartUtc;
    private int _idleCandidateHits;

    public TrayIdleMonitor(
        ILogger<TrayIdleMonitor> logger,
        IEventBuffer buffer,
        AgentConfig config,
        TrayBreakDetector? breakDetector = null)
    {
        _logger = logger;
        _buffer = buffer;
        _breakDetector = breakDetector;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(5, config.IdleCheckIntervalSeconds));
        _idleThreshold = config.IdleThresholdSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(1, config.IdleThresholdSeconds))
            : TimeSpan.FromMinutes(Math.Max(1, config.IdleThresholdMinutes));
    }

    public void Start(CancellationToken token)
    {
        _timer = new System.Threading.Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        _logger.LogInformation("Tray idle monitor started (poll {PollSeconds}s, threshold {ThresholdSeconds}s)",
            _pollInterval.TotalSeconds, _idleThreshold.TotalSeconds);
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
            var idleTime = GetIdleTime();

            if (!_isIdle && idleTime >= _idleThreshold)
            {
                // Safety: require two consecutive samples over threshold.
                // This adds at most one poll interval delay and avoids one-off bogus readings.
                _idleCandidateHits = Math.Min(_idleCandidateHits + 1, 10);
                if (_idleCandidateHits < 2)
                {
                    return;
                }

                _isIdle = true;
                _idleStartUtc = DateTime.UtcNow;

                var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                var maxReportedSeconds = 3600; // cap 1h (same behavior as service)
                var reportedIdleSeconds = Math.Min((int)idleTime.TotalSeconds, maxReportedSeconds);

                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.IdleStart,
                    EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                    NtAccount = ntAccount,
                    Metadata = new Dictionary<string, object>
                    {
                        { "idle_seconds", reportedIdleSeconds }
                    }
                }, token);

                _logger.LogInformation("Tray idle start detected after {Seconds}s (threshold: {Threshold}s)",
                    idleTime.TotalSeconds, _idleThreshold.TotalSeconds);

                if (_breakDetector != null)
                {
                    await _breakDetector.CheckBreakStatusAsync(true, _idleStartUtc, idleTime.TotalMinutes);
                }
            }
            else if (_isIdle && idleTime < _idleThreshold)
            {
                _isIdle = false;
                _idleCandidateHits = 0;

                if (_idleStartUtc.HasValue)
                {
                    var totalIdle = DateTime.UtcNow - _idleStartUtc.Value;
                    var idleSeconds = Math.Max(0, Math.Min((int)totalIdle.TotalSeconds, 86400));

                    var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.IdleEnd,
                        EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                        NtAccount = ntAccount,
                        Metadata = new Dictionary<string, object>
                        {
                            { "idle_seconds", idleSeconds },
                            { "idle_session_seconds", idleSeconds }
                        }
                    }, token);

                    _logger.LogInformation("Tray idle end detected after {Seconds}s", idleSeconds);
                }

                _idleStartUtc = null;

                if (_breakDetector != null)
                {
                    await _breakDetector.CheckBreakStatusAsync(false, null, 0);
                }
            }
            else
            {
                // reset candidate count when we are clearly active
                if (!_isIdle && idleTime < _idleThreshold)
                {
                    _idleCandidateHits = 0;
                }

                // Keep break detection in sync while idle, in case a break window opens during a long idle.
                if (_breakDetector != null && _isIdle)
                {
                    var totalIdle = _idleStartUtc.HasValue ? (DateTime.UtcNow - _idleStartUtc.Value) : idleTime;
                    await _breakDetector.CheckBreakStatusAsync(true, _idleStartUtc, totalIdle.TotalMinutes);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tray idle monitor tick failed");
        }
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (GetLastInputInfo(ref info))
        {
            uint idleTicks = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(idleTicks);
        }

        return TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}


