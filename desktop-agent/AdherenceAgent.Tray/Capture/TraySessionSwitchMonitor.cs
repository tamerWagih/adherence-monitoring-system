using System;
using System.Collections.Generic;
using System.Threading;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AdherenceAgent.Tray.Capture;

/// <summary>
/// Tray-owned lock/unlock detection using session switch events (interactive session).
/// Emits LOGIN on unlock and LOGOFF on lock with metadata to distinguish from true logon/logoff.
/// </summary>
public sealed class TraySessionSwitchMonitor
{
    private readonly ILogger<TraySessionSwitchMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private bool _started;
    private DateTime _lastEmitUtc = DateTime.MinValue;
    private string _lastEmitKey = string.Empty;

    public TraySessionSwitchMonitor(ILogger<TraySessionSwitchMonitor> logger, IEventBuffer buffer)
    {
        _logger = logger;
        _buffer = buffer;
    }

    public void Start()
    {
        if (_started) return;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _started = true;
        _logger.LogInformation("Tray session switch monitoring started (lock/unlock).");
    }

    public void Stop()
    {
        if (!_started) return;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _started = false;
    }

    private async void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        try
        {
            // Deduplicate rapid repeats (Windows can fire multiple related reasons around the same transition).
            // We only suppress identical (reason+eventType) repeats within a very small window.
            string? eventType = null;
            string? reason = null;

            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    eventType = EventTypes.Logoff;
                    reason = "lock";
                    break;
                case SessionSwitchReason.SessionUnlock:
                    eventType = EventTypes.Login;
                    reason = "unlock";
                    break;
                case SessionSwitchReason.SessionLogon:
                    eventType = EventTypes.Login;
                    reason = "logon";
                    break;
                case SessionSwitchReason.SessionLogoff:
                    eventType = EventTypes.Logoff;
                    reason = "logoff";
                    break;
                default:
                    break;
            }

            if (eventType == null || reason == null)
            {
                return;
            }

            var emitKey = $"{eventType}:{reason}";
            var nowUtc = DateTime.UtcNow;
            if (emitKey == _lastEmitKey && (nowUtc - _lastEmitUtc) < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _lastEmitKey = emitKey;
            _lastEmitUtc = nowUtc;

            var nt = WindowsIdentityHelper.GetCurrentNtAccount();
            await _buffer.AddAsync(new AdherenceEvent
            {
                EventType = eventType,
                EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(nowUtc),
                NtAccount = nt,
                Metadata = new Dictionary<string, object>
                {
                    { "source", "tray_session_switch" },
                    { "reason", reason },
                    { "machine", Environment.MachineName },
                }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tray session switch event failed");
        }
    }
}


