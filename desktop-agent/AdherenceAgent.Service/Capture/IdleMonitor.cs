using System.Runtime.InteropServices;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using AdherenceAgent.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Polls for idle/active transitions using GetLastInputInfo.
/// </summary>
public class IdleMonitor
{
    private readonly ILogger<IdleMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly BreakDetector? _breakDetector;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _idleThreshold;
    private Timer? _timer;
    private bool _isIdle;
    private DateTime? _idleStartUtc;

    public IdleMonitor(
        ILogger<IdleMonitor> logger,
        IEventBuffer buffer,
        int pollSeconds,
        int idleThresholdMinutes,
        int idleThresholdSecondsOverride = 0,
        BreakDetector? breakDetector = null)
    {
        _logger = logger;
        _buffer = buffer;
        _breakDetector = breakDetector;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(5, pollSeconds));
        _idleThreshold = idleThresholdSecondsOverride > 0
            ? TimeSpan.FromSeconds(Math.Max(1, idleThresholdSecondsOverride))
            : TimeSpan.FromMinutes(Math.Max(1, idleThresholdMinutes));
    }

    public void Start(CancellationToken token)
    {
        _timer = new Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
    }

    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            var idleTime = GetIdleTime();
            var idleDurationMinutes = idleTime.TotalMinutes;
            var wasIdle = _isIdle;

            if (!_isIdle && idleTime >= _idleThreshold)
            {
                _isIdle = true;
                _idleStartUtc = DateTime.UtcNow - idleTime;
                var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.IdleStart,
                    EventTimestampUtc = DateTime.UtcNow,
                    NtAccount = ntAccount,
                    Metadata = new Dictionary<string, object> { { "idle_seconds", (int)idleTime.TotalSeconds } }
                }, token);
                _logger.LogInformation("Idle start detected after {Seconds}s", idleTime.TotalSeconds);

                // Check for break detection
                if (_breakDetector != null)
                {
                    await _breakDetector.CheckBreakStatusAsync(true, _idleStartUtc, idleDurationMinutes);
                }
            }
            else if (_isIdle && idleTime < _idleThreshold)
            {
                _isIdle = false;
                var totalIdle = _idleStartUtc.HasValue ? (DateTime.UtcNow - _idleStartUtc.Value) : idleTime;
                var totalIdleMinutes = totalIdle.TotalMinutes;
                _idleStartUtc = null;
                var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.IdleEnd,
                    EventTimestampUtc = DateTime.UtcNow,
                    NtAccount = ntAccount,
                    Metadata = new Dictionary<string, object>
                    {
                        { "idle_seconds", (int)idleTime.TotalSeconds },
                        { "idle_session_seconds", (int)totalIdle.TotalSeconds }
                    }
                }, token);
                _logger.LogInformation("Idle end detected after {Seconds}s (session {SessionSeconds}s)", idleTime.TotalSeconds, totalIdle.TotalSeconds);

                // Check for break detection
                if (_breakDetector != null)
                {
                    await _breakDetector.CheckBreakStatusAsync(false, null, 0);
                }
            }
            else if (_isIdle && _breakDetector != null)
            {
                // Continue checking break status while idle (in case break window starts)
                var totalIdle = _idleStartUtc.HasValue ? (DateTime.UtcNow - _idleStartUtc.Value) : idleTime;
                await _breakDetector.CheckBreakStatusAsync(true, _idleStartUtc, totalIdle.TotalMinutes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idle monitor tick failed");
        }
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        uint idleTicks = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleTicks);
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

