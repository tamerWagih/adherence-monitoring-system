using System.Runtime.InteropServices;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using AdherenceAgent.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Polls for idle/active transitions using WTS APIs (works from Windows Service context).
/// GetLastInputInfo doesn't work from services running as LocalSystem, so we use WTS APIs instead.
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
    private DateTime? _lastKnownActiveTime;
    private TimeSpan? _lastIdleTimeSample;
    private int _idleCandidateHits;

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
        _lastKnownActiveTime = DateTime.UtcNow; // Initialize to now to avoid false idle at startup
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
            
            // If idleTime is suspiciously large (> 1 day), it's likely stale data from service context
            // Reset to zero to avoid false idle detection
            if (idleTime.TotalDays > 1)
            {
                _logger.LogDebug("Detected suspiciously large idle time ({Days} days), resetting to zero (likely stale data from service context)", idleTime.TotalDays);
                idleTime = TimeSpan.Zero;
            }
            
            var idleDurationMinutes = idleTime.TotalMinutes;
            var wasIdle = _isIdle;

            if (!_isIdle && idleTime >= _idleThreshold)
            {
                // Safety: Require two consecutive "idle >= threshold" samples before emitting IDLE_START.
                // This protects against transient/garbage WTS samples (especially right after service start / session changes).
                // It adds at most one poll interval of delay to idle detection.
                _idleCandidateHits = Math.Min(_idleCandidateHits + 1, 10);
                if (_idleCandidateHits < 2)
                {
                    _logger.LogDebug("Idle candidate detected (hit {Hit}/2): idle={IdleSeconds}s, threshold={ThresholdSeconds}s",
                        _idleCandidateHits, idleTime.TotalSeconds, _idleThreshold.TotalSeconds);
                    _lastIdleTimeSample = idleTime;
                    return;
                }

                _isIdle = true;
                // Set idle start to now (when threshold was crossed), not (now - idleTime)
                // idleTime can be cumulative from previous sessions if not properly reset,
                // so using it to calculate start time would be incorrect
                _idleStartUtc = DateTime.UtcNow;
                var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                
                // Report actual idle time, but cap at reasonable maximum (1 hour) to avoid showing absurdly large values
                // This gives accurate information about how long the user was idle before the threshold was crossed
                // Since we're in the branch where idleTime >= threshold, we know idleTime is at least the threshold
                var maxReportedSeconds = 3600; // 1 hour max
                var reportedIdleSeconds = Math.Min((int)idleTime.TotalSeconds, maxReportedSeconds);
                
                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.IdleStart,
                    EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                    NtAccount = ntAccount,
                    Metadata = new Dictionary<string, object> { { "idle_seconds", reportedIdleSeconds } }
                }, token);
                    var thresholdSeconds = (int)_idleThreshold.TotalSeconds;
                    _logger.LogInformation("Idle start detected after {Seconds}s (threshold: {Threshold}s)", idleTime.TotalSeconds, thresholdSeconds);

                    // Check for break detection
                    if (_breakDetector != null)
                    {
                        var totalIdleMinutes = idleTime.TotalMinutes;
                        _logger.LogDebug("Checking break detection: idleStartUtc={IdleStart}, totalIdleMinutes={TotalMinutes}", 
                            _idleStartUtc, totalIdleMinutes);
                        await _breakDetector.CheckBreakStatusAsync(true, _idleStartUtc, totalIdleMinutes);
                    }
            }
            else if (_isIdle && idleTime < _idleThreshold)
            {
                _isIdle = false;
                _idleCandidateHits = 0;
                
                // Only create IDLE_END if we have a valid idle start time
                // This prevents orphaned IDLE_END events (e.g., after service restart or if IDLE_START creation failed)
                if (_idleStartUtc.HasValue)
                {
                    var totalIdle = DateTime.UtcNow - _idleStartUtc.Value;
                    var totalIdleMinutes = totalIdle.TotalMinutes;
                    var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                    
                    // Ensure totalIdle is non-negative and reasonable (cap at 24 hours)
                    var idleSeconds = Math.Max(0, Math.Min((int)totalIdle.TotalSeconds, 86400));
                    var idleSessionSeconds = Math.Max(0, Math.Min((int)totalIdle.TotalSeconds, 86400));
                    
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.IdleEnd,
                        EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                        NtAccount = ntAccount,
                        Metadata = new Dictionary<string, object>
                        {
                            { "idle_seconds", idleSeconds },
                            { "idle_session_seconds", idleSessionSeconds }
                        }
                    }, token);
                    _logger.LogInformation("Idle end detected after {Seconds}s (session {SessionSeconds}s)", idleSeconds, idleSessionSeconds);

                    // Check for break detection
                    if (_breakDetector != null)
                    {
                        await _breakDetector.CheckBreakStatusAsync(false, null, 0);
                    }
                }
                else
                {
                    // State inconsistency: _isIdle was true but no _idleStartUtc
                    // This can happen after service restart or if IDLE_START creation failed
                    // Just reset state without creating IDLE_END event
                    _logger.LogWarning("Idle state reset without valid start time - skipping IDLE_END event (likely service restart or state inconsistency)");
                }
                
                _idleStartUtc = null;
                // We observed the user become active again (idle time dropped below threshold).
                // Keep a conservative "last known active" timestamp for fallback paths.
                _lastKnownActiveTime = DateTime.UtcNow;
            }
            else if (_isIdle && _breakDetector != null)
            {
                // Continue checking break status while idle (in case break window starts)
                var totalIdle = _idleStartUtc.HasValue ? (DateTime.UtcNow - _idleStartUtc.Value) : idleTime;
                await _breakDetector.CheckBreakStatusAsync(true, _idleStartUtc, totalIdle.TotalMinutes);
            }
            else if (!_isIdle && _breakDetector != null)
            {
                // Also check break status when not idle (in case break window starts and user becomes idle)
                // This ensures break windows are detected even if user is active when window starts
                await _breakDetector.CheckBreakStatusAsync(false, null, 0);
            }

            _lastIdleTimeSample = idleTime;
            if (!_isIdle && idleTime < _idleThreshold)
            {
                // reset idle candidate when the user is clearly active
                _idleCandidateHits = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idle monitor tick failed");
        }
    }

    /// <summary>
    /// Gets idle time using WTS APIs (works from Windows Service context).
    /// Falls back to GetLastInputInfo if WTS fails, but caps suspiciously large values.
    /// </summary>
    private TimeSpan GetIdleTime()
    {
        // Try WTS API first (works from service context)
        var wtsIdleTime = GetIdleTimeFromWts();
        if (wtsIdleTime.HasValue)
        {
            return wtsIdleTime.Value;
        }

        // Fallback to GetLastInputInfo (may not work from service, but try anyway)
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (GetLastInputInfo(ref info))
        {
            uint idleTicks = unchecked((uint)Environment.TickCount - info.dwTime);
            var idleTime = TimeSpan.FromMilliseconds(idleTicks);
            
            // Cap suspiciously large values (likely stale data from service context)
            if (idleTime.TotalDays > 1)
            {
                _logger.LogDebug("GetLastInputInfo returned suspiciously large idle time ({Days} days), using last known active time instead", idleTime.TotalDays);
                if (_lastKnownActiveTime.HasValue)
                {
                    return DateTime.UtcNow - _lastKnownActiveTime.Value;
                }
                return TimeSpan.Zero;
            }
            
            return idleTime;
        }

        // If both fail, use last known active time
        if (_lastKnownActiveTime.HasValue)
        {
            return DateTime.UtcNow - _lastKnownActiveTime.Value;
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Gets idle time using WTS APIs for the active console session.
    /// Returns null if unable to determine (e.g., no active session).
    /// </summary>
    private TimeSpan? GetIdleTimeFromWts()
    {
        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                return null; // No active console session
            }

            IntPtr buffer = IntPtr.Zero;
            uint bytesReturned = 0;
            try
            {
                if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSSessionInfo, out buffer, out bytesReturned))
                {
                    return null;
                }

                if (buffer == IntPtr.Zero || bytesReturned == 0)
                {
                    return null;
                }

                // Marshal WTSINFO structure
                var wtsInfo = Marshal.PtrToStructure<WTSINFO>(buffer);
                
                // WTSINFO.LastInputTime is in FILETIME (100-nanosecond intervals since 1601-01-01)
                // Convert to DateTime and calculate idle time
                if (wtsInfo.LastInputTime > 0 && wtsInfo.CurrentTime > 0)
                {
                    // FILETIME units are 100ns (same as TimeSpan ticks). Use raw math to avoid timezone/DateTimeKind pitfalls.
                    var idleFileTimeTicks = wtsInfo.CurrentTime - wtsInfo.LastInputTime;

                    // Validate: LastInputTime should not be in the future (negative idle)
                    if (idleFileTimeTicks < 0)
                    {
                        var lastInputUtc = DateTime.FromFileTimeUtc(wtsInfo.LastInputTime);
                        var currentUtc = DateTime.FromFileTimeUtc(wtsInfo.CurrentTime);
                        _logger.LogWarning("WTS LastInputTime is in the future (LastInput: {LastInput}, Current: {Current}), using last known active time",
                            lastInputUtc, currentUtc);
                        if (_lastKnownActiveTime.HasValue)
                        {
                            return DateTime.UtcNow - _lastKnownActiveTime.Value;
                        }
                        return TimeSpan.Zero;
                    }

                    var idleTime = TimeSpan.FromTicks(idleFileTimeTicks);
                    
                    // If LastInputTime is suspiciously old (> 1 day), it's likely stale
                    // Use last known active time instead
                    if (idleTime.TotalDays > 1)
                    {
                        _logger.LogDebug("WTS LastInputTime is suspiciously old ({Days} days), likely stale", idleTime.TotalDays);
                        if (_lastKnownActiveTime.HasValue)
                        {
                            return DateTime.UtcNow - _lastKnownActiveTime.Value;
                        }
                        return TimeSpan.Zero;
                    }

                    // Track last known activity as the last input time implied by this idle measurement.
                    // This is used only for fallback paths when WTS/GetLastInputInfo returns unusable values.
                    _lastKnownActiveTime = DateTime.UtcNow - idleTime;
                    
                    return idleTime;
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    WTSFreeMemory(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get idle time from WTS");
        }

        return null;
    }

    // --- WTS API declarations ---
    private enum WTS_INFO_CLASS
    {
        // IMPORTANT:
        // These values must match the Windows SDK `WTS_INFO_CLASS` enum.
        // A wrong value can still return success but the buffer will contain a different structure (or a string),
        // and marshalling it as `WTSINFO` yields garbage timestamps (e.g. 1601 dates / huge idle while user is active).
        WTSSessionInfo = 24
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WTSINFO
    {
        public WTS_CONNECTSTATE_CLASS State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    // --- Fallback GetLastInputInfo declarations ---
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}

