using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Xml;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using AdherenceAgent.Shared.Helpers;
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
    private readonly Dictionary<string, DateTime> _recentLogins; // Track recent logins to prevent duplicates
    private readonly Dictionary<string, DateTime> _recentLogoffs; // Track recent logoffs to prevent duplicates
    private readonly TimeSpan _duplicateWindow = TimeSpan.FromSeconds(5); // 5-second window for duplicate detection

    // Security Event IDs
    private const int LogonEventId = 4624;
    private const int LogoffEventId = 4634;
    private const int UserInitiatedLogoffEventId = 4647;
    // NOTE: Lock/unlock (4800/4801) is handled by the tray app in the interactive session.

    public LoginLogoffMonitor(ILogger<LoginLogoffMonitor> logger, IEventBuffer buffer)
    {
        _logger = logger;
        _buffer = buffer;
        _recentLogins = new Dictionary<string, DateTime>();
        _recentLogoffs = new Dictionary<string, DateTime>();
    }

    public void Start(CancellationToken token)
    {
        try
        {
            var logonQuery = new EventLogQuery(
                "Security",
                PathType.LogName,
                $"*[System[(EventID={LogonEventId})]]");
            _logonWatcher = new EventLogWatcher(logonQuery);
            _logonWatcher.EventRecordWritten += async (_, e) => await HandleEventAsync(e.EventRecord, EventTypes.Login, token);
            _logonWatcher.Enabled = true;

            var logoffQuery = new EventLogQuery(
                "Security",
                PathType.LogName,
                $"*[System[(EventID={LogoffEventId} or EventID={UserInitiatedLogoffEventId})]]");
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
            // Since tray owns lock/unlock, service should only emit "real" interactive logon/logoff.
            // For 4624/4634/4647, filter by LogonType allowlist:
            //  - 2  = Interactive (console)
            //  - 10 = RemoteInteractive (RDP)
            //  - 11 = CachedInteractive
            // This removes lock/unlock noise (typically LogonType=7) and other non-user sessions.
            if (record.Id == LogonEventId || record.Id == LogoffEventId || record.Id == UserInitiatedLogoffEventId)
            {
                var logonType = (ExtractEventDataValue(record, "LogonType") ?? string.Empty).Trim();
                if (!IsAllowedInteractiveLogonType(logonType))
                {
                    _logger.LogDebug("Ignoring Security event {EventId} LogonType={LogonType} (not interactive)",
                        record.Id, logonType);
                    return;
                }
            }

            // Extract NT account from Security Event Log
            var ntAccount = ExtractNtAccountFromEvent(record);
            
            // Filter out SYSTEM account and other service accounts
            if (string.IsNullOrEmpty(ntAccount) || 
                ntAccount.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                ntAccount.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) ||
                ntAccount.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping {EventType} event for system account: {NtAccount}", eventType, ntAccount ?? "unknown");
                return;
            }
            
            // Fallback to current session if extraction fails (but only if not a system account)
            if (string.IsNullOrEmpty(ntAccount))
            {
                ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                // Double-check it's not a system account
                if (ntAccount.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                    ntAccount.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) ||
                    ntAccount.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping {EventType} event - current session is system account", eventType);
                    return;
                }
                _logger.LogWarning("Could not extract NT account from Security Event Log, using current session: {NtAccount}", ntAccount);
            }

            // Deduplicate LOGIN events (Event ID 4624 can fire multiple times for same logon)
            if (eventType == EventTypes.Login)
            {
                var eventTime = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
                var recordId = record.RecordId?.ToString() ?? string.Empty;
                var logonId = (ExtractEventDataValue(record, "TargetLogonId") ?? string.Empty).Trim();
                var dedupKey = $"{eventType}_{ntAccount}_{record.Id}_{recordId}_{logonId}_{eventTime:yyyyMMddHHmmss}";
                
                // Clean up old entries
                var cutoff = DateTime.UtcNow - _duplicateWindow;
                var keysToRemove = _recentLogins.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _recentLogins.Remove(key);
                }
                
                // Check for duplicate
                if (_recentLogins.ContainsKey(dedupKey))
                {
                    _logger.LogDebug("Skipping duplicate LOGIN event: {NtAccount}, EventID: {EventId}, Time: {Time}", 
                        ntAccount, record.Id, eventTime);
                    return;
                }
                
                _recentLogins[dedupKey] = eventTime;
            }
            else if (eventType == EventTypes.Logoff)
            {
                // Deduplicate LOGOFF events too (4634/4647 can also fire multiple times).
                var eventTime = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
                var recordId = record.RecordId?.ToString() ?? string.Empty;
                var logonId = (ExtractEventDataValue(record, "TargetLogonId") ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(logonId))
                {
                    // Some logoff events use SubjectLogonId
                    logonId = (ExtractEventDataValue(record, "SubjectLogonId") ?? string.Empty).Trim();
                }
                var dedupKey = $"{eventType}_{ntAccount}_{record.Id}_{recordId}_{logonId}_{eventTime:yyyyMMddHHmmss}";

                var cutoff = DateTime.UtcNow - _duplicateWindow;
                var keysToRemove = _recentLogoffs.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _recentLogoffs.Remove(key);
                }

                if (_recentLogoffs.ContainsKey(dedupKey))
                {
                    _logger.LogDebug("Skipping duplicate LOGOFF event: {NtAccount}, EventID: {EventId}, Time: {Time}",
                        ntAccount, record.Id, eventTime);
                    return;
                }

                _recentLogoffs[dedupKey] = eventTime;
            }

            var eventTimeUtc = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
            await _buffer.AddAsync(new AdherenceEvent
            {
                EventType = eventType,
                EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(eventTimeUtc),
                NtAccount = ntAccount,
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

    /// <summary>
    /// Reads a value from the event XML's EventData Data[@Name='...'] node.
    /// Returns empty string if missing/unparseable.
    /// </summary>
    private string ExtractEventDataValue(EventRecord record, string name)
    {
        try
        {
            var xml = record.ToXml();
            if (string.IsNullOrEmpty(xml))
            {
                return string.Empty;
            }

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var nsManager = new XmlNamespaceManager(doc.NameTable);
            nsManager.AddNamespace("ns", "http://schemas.microsoft.com/win/2004/08/events/event");

            var node = doc.SelectSingleNode($"//ns:EventData/ns:Data[@Name='{name}']", nsManager);
            return node?.InnerText ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsAllowedInteractiveLogonType(string logonType)
    {
        // Allow only the interactive types we consider "real user sessions".
        return logonType == "2" || logonType == "10" || logonType == "11";
    }

    /// <summary>
    /// Extracts NT account (sam_account_name) from Security Event Log record.
    /// Looks for TargetUserName or SubjectUserName in event XML data.
    /// </summary>
    private string ExtractNtAccountFromEvent(EventRecord record)
    {
        try
        {
            // Get event XML
            var xml = record.ToXml();
            if (string.IsNullOrEmpty(xml))
            {
                return string.Empty;
            }

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            // Security events typically have TargetUserName or SubjectUserName in EventData
            var nsManager = new XmlNamespaceManager(doc.NameTable);
            nsManager.AddNamespace("ns", "http://schemas.microsoft.com/win/2004/08/events/event");

            // Try TargetUserName first (for logon events)
            var targetUserName = doc.SelectSingleNode("//ns:EventData/ns:Data[@Name='TargetUserName']", nsManager);
            if (targetUserName != null && !string.IsNullOrEmpty(targetUserName.InnerText))
            {
                return WindowsIdentityHelper.ExtractSamAccountName(targetUserName.InnerText);
            }

            // Try SubjectUserName (for logoff events)
            var subjectUserName = doc.SelectSingleNode("//ns:EventData/ns:Data[@Name='SubjectUserName']", nsManager);
            if (subjectUserName != null && !string.IsNullOrEmpty(subjectUserName.InnerText))
            {
                return WindowsIdentityHelper.ExtractSamAccountName(subjectUserName.InnerText);
            }

            // Try TargetUserSid and resolve to username (more complex, fallback)
            // For now, return empty and let fallback handle it
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract NT account from Security Event Log");
            return string.Empty;
        }
    }

    private async Task EmitStartupLoginAsync(CancellationToken token)
    {
        // Get NT account from current session
        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        
        await _buffer.AddAsync(new AdherenceEvent
        {
            EventType = EventTypes.Login,
            EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
            NtAccount = ntAccount,
            Metadata = new Dictionary<string, object>
            {
                { "note", "startup_fallback" },
                { "machine", Environment.MachineName }
            }
        }, token);
    }
}

