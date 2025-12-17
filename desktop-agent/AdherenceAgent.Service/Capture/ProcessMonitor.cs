using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Monitors process start and end events to track application lifecycle.
/// Creates APPLICATION_START and APPLICATION_END events.
/// </summary>
public class ProcessMonitor
{
    private readonly ILogger<ProcessMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly HashSet<int> _trackedProcesses;
    private ManagementEventWatcher? _processStartWatcher;
    private ManagementEventWatcher? _processStopWatcher;

    public ProcessMonitor(
        ILogger<ProcessMonitor> logger,
        IEventBuffer buffer)
    {
        _logger = logger;
        _buffer = buffer;
        _trackedProcesses = new HashSet<int>();
    }

    public void Start(CancellationToken token)
    {
        try
        {
            // Watch for process start events
            var startQuery = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _processStartWatcher = new ManagementEventWatcher(startQuery);
            _processStartWatcher.EventArrived += async (sender, e) => await HandleProcessStartAsync(e, token);
            _processStartWatcher.Start();

            // Watch for process stop events
            var stopQuery = new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace");
            _processStopWatcher = new ManagementEventWatcher(stopQuery);
            _processStopWatcher.EventArrived += async (sender, e) => await HandleProcessStopAsync(e, token);
            _processStopWatcher.Start();

            _logger.LogInformation("Process monitoring started (WMI event watchers).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start WMI process watchers. Process monitoring disabled.");
            // Fallback: Could use polling instead, but WMI is more efficient
        }
    }

    public void Stop()
    {
        _processStartWatcher?.Stop();
        _processStartWatcher?.Dispose();
        _processStopWatcher?.Stop();
        _processStopWatcher?.Dispose();
        _logger.LogInformation("Process monitoring stopped.");
    }

    private async Task HandleProcessStartAsync(EventArrivedEventArgs e, CancellationToken token)
    {
        try
        {
            var processId = Convert.ToInt32(e.NewEvent["ProcessId"]);
            var processName = e.NewEvent["ProcessName"]?.ToString();

            if (string.IsNullOrEmpty(processName))
            {
                return;
            }

            // Skip system processes and common background processes
            if (ShouldSkipProcess(processName))
            {
                return;
            }

            // Get process details
            string? processPath = null;
            try
            {
                using var proc = Process.GetProcessById(processId);
                processPath = proc.MainModule?.FileName;
            }
            catch
            {
                // Process may have exited already or access denied
            }

            _trackedProcesses.Add(processId);

            var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
            var evt = new AdherenceEvent
            {
                EventType = EventTypes.ApplicationStart,
                EventTimestampUtc = DateTime.UtcNow,
                NtAccount = ntAccount,
                ApplicationName = processName,
                ApplicationPath = processPath,
                Metadata = new Dictionary<string, object>
                {
                    { "process_id", processId },
                    { "process_name", processName },
                    { "process_path", processPath ?? string.Empty }
                }
            };

            await _buffer.AddAsync(evt, token);
            _logger.LogDebug("Application started: {ProcessName} (PID: {ProcessId})", processName, processId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to handle process start event.");
        }
    }

    private async Task HandleProcessStopAsync(EventArrivedEventArgs e, CancellationToken token)
    {
        try
        {
            var processId = Convert.ToInt32(e.NewEvent["ProcessId"]);
            var processName = e.NewEvent["ProcessName"]?.ToString();

            if (string.IsNullOrEmpty(processName) || !_trackedProcesses.Contains(processId))
            {
                return;
            }

            _trackedProcesses.Remove(processId);

            var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
            var evt = new AdherenceEvent
            {
                EventType = EventTypes.ApplicationEnd,
                EventTimestampUtc = DateTime.UtcNow,
                NtAccount = ntAccount,
                ApplicationName = processName,
                Metadata = new Dictionary<string, object>
                {
                    { "process_id", processId },
                    { "process_name", processName }
                }
            };

            await _buffer.AddAsync(evt, token);
            _logger.LogDebug("Application ended: {ProcessName} (PID: {ProcessId})", processName, processId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to handle process stop event.");
        }
    }

    private static bool ShouldSkipProcess(string processName)
    {
        // Skip system processes and common background processes
        var skipProcesses = new[]
        {
            "dwm", "csrss", "winlogon", "services", "lsass", "svchost",
            "explorer", "taskhost", "conhost", "RuntimeBroker", "SearchIndexer",
            "WmiPrvSE", "audiodg", "spoolsv", "smss", "wininit"
        };

        foreach (var skip in skipProcesses)
        {
            if (processName.Equals(skip, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
