using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Watches Security log for logon/logoff events. Falls back to a startup LOGIN if unavailable.
/// </summary>
public class LoginLogoffMonitor
{
    private readonly ILogger<LoginLogoffMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private EventLogWatcher? _logonWatcher;
    private EventLogWatcher? _logoffWatcher;

    // Security Event IDs
    private const int LogonEventId = 4624;
    private const int LogoffEventId = 4634;
    private const int UserInitiatedLogoffEventId = 4647;
    private const int LockEventId = 4800;
    private const int UnlockEventId = 4801;

    public LoginLogoffMonitor(ILogger<LoginLogoffMonitor> logger, IEventBuffer buffer)
    {
        _logger = logger;
        _buffer = buffer;
    }

    public void Start(CancellationToken token)
    {
        try
        {
            var logonQuery = new EventLogQuery(
                "Security",
                PathType.LogName,
                $"*[System[(EventID={LogonEventId} or EventID={UnlockEventId})]]");
            _logonWatcher = new EventLogWatcher(logonQuery);
            _logonWatcher.EventRecordWritten += async (_, e) => await HandleEventAsync(e.EventRecord, EventTypes.Login, token);
            _logonWatcher.Enabled = true;

            var logoffQuery = new EventLogQuery(
                "Security",
                PathType.LogName,
                $"*[System[(EventID={LogoffEventId} or EventID={UserInitiatedLogoffEventId} or EventID={LockEventId})]]");
            _logoffWatcher = new EventLogWatcher(logoffQuery);
            _logoffWatcher.EventRecordWritten += async (_, e) => await HandleEventAsync(e.EventRecord, EventTypes.Logoff, token);
            _logoffWatcher.Enabled = true;

            _logger.LogInformation("Login/logoff monitoring started (Security log).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Security log watcher unavailable; falling back to startup LOGIN only.");
            _ = EmitStartupLoginAsync(token);
        }
    }

    public void Stop()
    {
        _logonWatcher?.Dispose();
        _logoffWatcher?.Dispose();
    }

    private async Task HandleEventAsync(EventRecord? record, string eventType, CancellationToken token)
    {
        if (record == null) return;

        try
        {
            await _buffer.AddAsync(new AdherenceEvent
            {
                EventType = eventType,
                EventTimestampUtc = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    { "event_id", record.Id },
                    { "machine", Environment.MachineName }
                }
            }, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to buffer {EventType} event from Security log", eventType);
        }
    }

    private async Task EmitStartupLoginAsync(CancellationToken token)
    {
        await _buffer.AddAsync(new AdherenceEvent
        {
            EventType = EventTypes.Login,
            EventTimestampUtc = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                { "note", "startup_fallback" },
                { "machine", Environment.MachineName }
            }
        }, token);
    }
}

