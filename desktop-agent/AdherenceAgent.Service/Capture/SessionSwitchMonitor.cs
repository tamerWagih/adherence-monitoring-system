using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using AdherenceAgent.Shared.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Fallback for lock/unlock detection using session switch events (when Security log events are not available).
/// </summary>
public class SessionSwitchMonitor
{
    private readonly ILogger<SessionSwitchMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private bool _started;

    public SessionSwitchMonitor(ILogger<SessionSwitchMonitor> logger, IEventBuffer buffer)
    {
        _logger = logger;
        _buffer = buffer;
    }

    public void Start(CancellationToken token)
    {
        if (_started) return;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _started = true;
        _logger.LogInformation("Session switch monitoring started (lock/unlock fallback).");
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
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    var lockNtAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.Logoff,
                        EventTimestampUtc = DateTime.UtcNow,
                        NtAccount = lockNtAccount,
                        Metadata = new Dictionary<string, object>
                        {
                            { "source", "session_switch" },
                            { "reason", "lock" },
                            { "machine", Environment.MachineName }
                        }
                    }, CancellationToken.None);
                    _logger.LogInformation("Session lock detected via SessionSwitch.");
                    break;
                case SessionSwitchReason.SessionUnlock:
                    var unlockNtAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.Login,
                        EventTimestampUtc = DateTime.UtcNow,
                        NtAccount = unlockNtAccount,
                        Metadata = new Dictionary<string, object>
                        {
                            { "source", "session_switch" },
                            { "reason", "unlock" },
                            { "machine", Environment.MachineName }
                        }
                    }, CancellationToken.None);
                    _logger.LogInformation("Session unlock detected via SessionSwitch.");
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to buffer session switch event");
        }
    }
}

