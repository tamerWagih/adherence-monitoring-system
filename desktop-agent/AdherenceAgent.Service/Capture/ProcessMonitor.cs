using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
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
/// Filters out system processes, short-lived processes, and processes without windows.
/// </summary>
public class ProcessMonitor
{
    private readonly ILogger<ProcessMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly Dictionary<int, ProcessStartInfo> _pendingProcesses; // Track pending processes waiting for debounce
    private readonly HashSet<int> _trackedProcesses; // Processes that have APPLICATION_START events created
    private ManagementEventWatcher? _processStartWatcher;
    private ManagementEventWatcher? _processStopWatcher;
    private readonly TimeSpan _minProcessLifetime = TimeSpan.FromSeconds(5); // Minimum 5 seconds to be considered a real application

    private class ProcessStartInfo
    {
        public string ProcessName { get; set; } = string.Empty;
        public string? ProcessPath { get; set; }
        public DateTime StartTime { get; set; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public ProcessMonitor(
        ILogger<ProcessMonitor> logger,
        IEventBuffer buffer)
    {
        _logger = logger;
        _buffer = buffer;
        _trackedProcesses = new HashSet<int>();
        _pendingProcesses = new Dictionary<int, ProcessStartInfo>();
    }

    public void Start(CancellationToken token)
    {
        try
        {
            // Watch for process start events
            var startQuery = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _processStartWatcher = new ManagementEventWatcher(startQuery);
            _processStartWatcher.EventArrived += (sender, e) => HandleProcessStartAsync(e, token);
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

    private void HandleProcessStartAsync(EventArrivedEventArgs e, CancellationToken token)
    {
        try
        {
            var processId = Convert.ToInt32(e.NewEvent["ProcessId"]);
            var processName = e.NewEvent["ProcessName"]?.ToString();

            if (string.IsNullOrEmpty(processName))
            {
                return;
            }

            // Normalize process name (remove .exe extension for comparison)
            var normalizedName = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)
                                           .Replace(".EXE", "", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug("Process started: {ProcessName} (normalized: {NormalizedName}, PID: {ProcessId})", 
                processName, normalizedName, processId);

            // Skip system processes and common background processes
            if (ShouldSkipProcess(normalizedName))
            {
                _logger.LogDebug("Skipping system process: {ProcessName} (PID: {ProcessId})", normalizedName, processId);
                return;
            }

            // Get process details
            string? processPath = null;
            try
            {
                using var proc = Process.GetProcessById(processId);
                processPath = proc.MainModule?.FileName;

                // Skip processes in system directories
                if (!string.IsNullOrEmpty(processPath) && IsSystemDirectory(processPath))
                {
                    _logger.LogDebug("Skipping system directory process: {ProcessName} (PID: {ProcessId}, Path: {Path})", 
                        normalizedName, processId, processPath);
                    return;
                }

                // Skip development tools and UWP apps
                if (!string.IsNullOrEmpty(processPath))
                {
                    if (IsDevelopmentToolDirectory(processPath))
                    {
                        _logger.LogDebug("Skipping development tool: {ProcessName} (PID: {ProcessId}, Path: {Path})", 
                            normalizedName, processId, processPath);
                        return;
                    }

                    if (IsUwpAppDirectory(processPath))
                    {
                        _logger.LogDebug("Skipping UWP app: {ProcessName} (PID: {ProcessId}, Path: {Path})", 
                            normalizedName, processId, processPath);
                        return;
                    }
                }

                // Skip browsers - already covered by BrowserTabMonitor
                if (IsBrowserProcess(normalizedName))
                {
                    _logger.LogDebug("Skipping browser process (covered by BrowserTabMonitor): {ProcessName} (PID: {ProcessId})", 
                        normalizedName, processId);
                    return;
                }

                // For GUI applications, windows may not be visible immediately (splash screens, initialization)
                // Known GUI apps skip window visibility check entirely
                bool isKnownGuiApp = IsKnownGuiApplication(normalizedName, processPath);
                
                _logger.LogDebug("Process check: {ProcessName} (PID: {ProcessId}, Path: {Path}, KnownGUI: {IsKnownGUI})", 
                    normalizedName, processId, processPath ?? "null", isKnownGuiApp);
                
                if (!isKnownGuiApp)
                {
                    // For unknown processes, check if they have visible windows (background utilities)
                    // Use fire-and-forget delayed check to allow windows to initialize
                    var processIdCopy = processId;
                    var normalizedNameCopy = normalizedName;
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Wait for windows to initialize
                            await Task.Delay(TimeSpan.FromMilliseconds(1000), token);
                            
                            // Re-check if process still exists and has visible windows
                            try
                            {
                                using var checkProc = Process.GetProcessById(processIdCopy);
                                if (!HasVisibleWindows(processIdCopy))
                                {
                                    _logger.LogDebug("Skipping process without visible windows: {ProcessName} (PID: {ProcessId})", 
                                        normalizedNameCopy, processIdCopy);
                                    // Remove from pending if it was added
                                    _pendingProcesses.Remove(processIdCopy);
                                    return;
                                }
                            }
                            catch
                            {
                                // Process may have exited
                                _pendingProcesses.Remove(processIdCopy);
                                return;
                            }
                        }
                        catch (TaskCanceledException)
                        {
                            // Cancelled
                        }
                    }, token);
                    
                    // Continue to add to pending - delayed check will remove it if no windows
                }
                else
                {
                    _logger.LogDebug("Detected known GUI application: {ProcessName} (PID: {ProcessId}) - skipping window check", 
                        normalizedName, processId);
                }
            }
            catch (Exception ex)
            {
                // Process may have exited already or access denied
                _logger.LogDebug(ex, "Failed to get process details: {ProcessName} (PID: {ProcessId})", normalizedName, processId);
                return;
            }

            // Delay creating APPLICATION_START event to filter out short-lived processes
            // Only create the event if the process is still running after minimum lifetime
            var startTime = DateTime.UtcNow;
            var cts = new CancellationTokenSource();
            
            var startInfo = new ProcessStartInfo
            {
                ProcessName = normalizedName,
                ProcessPath = processPath,
                StartTime = startTime,
                CancellationTokenSource = cts
            };
            
            _pendingProcesses[processId] = startInfo;

            // Schedule delayed event creation
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_minProcessLifetime, cts.Token);
                    
                    // Check if process is still pending (not ended yet)
                    if (_pendingProcesses.TryGetValue(processId, out var info) && info.CancellationTokenSource == cts)
                    {
                        // Process lived long enough, create APPLICATION_START event
                        _pendingProcesses.Remove(processId);
                        _trackedProcesses.Add(processId);

                        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
                        var evt = new AdherenceEvent
                        {
                            EventType = EventTypes.ApplicationStart,
                            EventTimestampUtc = info.StartTime, // Use original start time
                            NtAccount = ntAccount,
                            ApplicationName = info.ProcessName,
                            ApplicationPath = info.ProcessPath,
                            Metadata = new Dictionary<string, object>
                            {
                                { "process_id", processId },
                                { "process_name", info.ProcessName },
                                { "process_path", info.ProcessPath ?? string.Empty }
                            }
                        };

                        await _buffer.AddAsync(evt, token);
                        _logger.LogDebug("Application started: {ProcessName} (PID: {ProcessId})", info.ProcessName, processId);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Process ended before minimum lifetime, ignore
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create delayed APPLICATION_START event.");
                }
            }, cts.Token);
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

            if (string.IsNullOrEmpty(processName))
            {
                return;
            }

            // Normalize process name
            var normalizedName = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)
                                           .Replace(".EXE", "", StringComparison.OrdinalIgnoreCase);

            // Check if process was pending (waiting for debounce delay)
            if (_pendingProcesses.TryGetValue(processId, out var pendingInfo))
            {
                // Process ended before minimum lifetime - cancel the delayed APPLICATION_START
                pendingInfo.CancellationTokenSource?.Cancel();
                _pendingProcesses.Remove(processId);
                _logger.LogDebug("Skipping short-lived process: {ProcessName} (PID: {ProcessId})", normalizedName, processId);
                return;
            }

            // Check if we have a tracked APPLICATION_START event for this process
            if (!_trackedProcesses.Contains(processId))
            {
                return;
            }

            _trackedProcesses.Remove(processId);

            var endTime = DateTime.UtcNow;
            var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
            var evt = new AdherenceEvent
            {
                EventType = EventTypes.ApplicationEnd,
                EventTimestampUtc = endTime,
                NtAccount = ntAccount,
                ApplicationName = normalizedName,
                Metadata = new Dictionary<string, object>
                {
                    { "process_id", processId },
                    { "process_name", normalizedName }
                }
            };

            await _buffer.AddAsync(evt, token);
            _logger.LogDebug("Application ended: {ProcessName} (PID: {ProcessId})", normalizedName, processId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to handle process stop event.");
        }
    }

    private static bool ShouldSkipProcess(string processName)
    {
        // Skip system processes and common background processes
        // Note: processName should already be normalized (no .exe extension)
        var skipProcesses = new[]
        {
            // Core Windows processes
            "dwm", "csrss", "winlogon", "services", "lsass", "svchost",
            "smss", "wininit", "System", "Registry",
            
            // Windows utilities and helpers
            "explorer", "taskhost", "conhost", "RuntimeBroker", "SearchIndexer",
            "WmiPrvSE", "audiodg", "spoolsv", "dllhost", "WerFault",
            
            // Network/system utilities
            "ROUTE", "ARP", "PING", "IPCONFIG", "NETSTAT", "TRACERT",
            "NBTSTAT", "NETSH", "TASKLIST", "TASKILL", "SC", "WMIC",
            
            // Background services
            "SearchProtocolHost", "SearchFilterHost", "WmiApSrv",
            "CompatTelRunner", "CompatTelRunner64", "CompatTelRunner32",
            
            // Antivirus/security (common)
            "MsMpEng", "NisSrv", "SecurityHealthService",
            
            // Update services
            "wuauclt", "WaaSMedicSvc", "usoclient", "updater",
            
            // Hardware support tools
            "SupportAssistInstaller", "SupportAssistI", "SupportAssist",
            
            // Other system utilities
            "cmd", "powershell", "pwsh", "wscript", "cscript", "regsvr32"
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

    private static bool IsSystemDirectory(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(processPath);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            var systemDirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS"),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };

            foreach (var sysDir in systemDirs)
            {
                if (directory.StartsWith(sysDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore path parsing errors
        }

        return false;
    }

    private static bool IsDevelopmentToolDirectory(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(processPath);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            // Git installation directory
            if (directory.Contains("Program Files\\Git", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("Program Files (x86)\\Git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Node.js installation directory
            if (directory.Contains("Program Files\\nodejs", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("Program Files (x86)\\nodejs", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("AppData\\Roaming\\npm", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("AppData\\Local\\npm", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // .NET CLI installation directory
            if (directory.Contains("Program Files\\dotnet", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("Program Files (x86)\\dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Visual Studio development tools
            if (directory.Contains("Program Files\\Microsoft Visual Studio", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("Program Files (x86)\\Microsoft Visual Studio", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Common development tool directories
            if (directory.Contains("AppData\\Local\\Programs\\Git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // Ignore path parsing errors
        }

        return false;
    }

    private static bool IsUwpAppDirectory(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        try
        {
            // UWP apps are installed in WindowsApps directory
            if (processPath.Contains("WindowsApps\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // Ignore path parsing errors
        }

        return false;
    }

    private static bool IsBrowserProcess(string processName)
    {
        // Browsers are already covered by BrowserTabMonitor
        var browserProcesses = new[]
        {
            "chrome", "msedge", "firefox", "brave", "opera", "safari", "vivaldi"
        };

        foreach (var browser in browserProcesses)
        {
            if (processName.Equals(browser, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVisibleWindows(int processId)
    {
        bool hasWindow = false;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowProcessId);
            if (windowProcessId == processId)
            {
                // Check if window is visible
                if (IsWindowVisible(hWnd))
                {
                    hasWindow = true;
                    return false; // Stop enumeration
                }
            }
            return true; // Continue enumeration
        }, IntPtr.Zero);

        return hasWindow;
    }

    private static bool IsKnownGuiApplication(string processName, string? processPath)
    {
        // Known GUI applications that may not have visible windows immediately
        // Office applications, common desktop apps, etc.
        var knownGuiApps = new[]
        {
            // Microsoft Office
            "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "MSACCESS", "ONENOTE",
            
            // Adobe
            "Acrobat", "AcroRd32", "Photoshop", "Illustrator", "InDesign",
            
            // Other common GUI apps
            "notepad++", "Notepad++", "code", "devenv", "VisualStudio",
            "Teams", "ms-teams", "slack", "discord",
            "explorer", "winrar", "7zFM", "WinZip"
            // Note: Browsers (chrome, msedge, firefox, etc.) are filtered separately
            // since they're covered by BrowserTabMonitor
        };

        foreach (var app in knownGuiApps)
        {
            if (processName.Equals(app, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check if process path suggests it's a GUI application
        // Common GUI app directories
        if (!string.IsNullOrEmpty(processPath))
        {
            var guiDirs = new[]
            {
                "Program Files",
                "Program Files (x86)",
                "ProgramData",
                "AppData\\Local",
                "AppData\\Roaming"
            };

            foreach (var dir in guiDirs)
            {
                if (processPath.Contains(dir, StringComparison.OrdinalIgnoreCase))
                {
                    // If it's not in system directories and in a user/application directory, likely GUI
                    return true;
                }
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
